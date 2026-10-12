using System.IO.Compression;
using Microsoft.Win32;
using S9Battery.Core;

namespace S9Battery.Tray;

internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon tray;
    private readonly ContextMenuStrip menu = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 5000 };
    private readonly Control dispatcher = new();
    private readonly DeviceWindow window;
    private readonly DiagnosticLog log = new();
    private readonly MouseMonitor monitor;
    private ChargeNotice chargeNotice = new();
    private LowBatteryNotice lowNotice = new();
    private readonly BatteryToast toast = new();
    private AlertSettings settings;
    private readonly ToolStripMenuItem statusItem = new("正在读取…") { Enabled = false };
    private readonly ToolStripMenuItem startupItem = new("开机启动") { CheckOnClick = false };
    private Icon? icon;
    private (TrayIconStyle Style, int Size)? iconKey;
    private bool busy, pendingForce;
    private volatile bool closing;
    private MouseStatus status = new(Connection.Disconnected);

    public TrayContext()
    {
        _ = dispatcher.Handle;
        settings = AlertPreferences.Load(log.Write);
        monitor = new(log.Write, QueueEventRefresh);
        menu.Items.Add(statusItem);
        menu.Items.Add("立即刷新", null, (_, _) => Refresh(true));
        startupItem.Checked = IsStartupEnabled();
        startupItem.Click += (_, _) => ToggleStartup();
        menu.Items.Add(startupItem);
        menu.Items.Add("提醒设置…", null, (_, _) => ConfigureAlerts());
        var preview = new ToolStripMenuItem("预览通知");
        preview.DropDownItems.Add("低电量提醒", null, (_, _) =>
            toast.Present(true, settings.LowBatteryThreshold - 1, $"电量低于你设置的 {settings.LowBatteryThreshold}%。\n请连接充电线。"));
        preview.DropDownItems.Add("100% 提醒", null, (_, _) =>
            toast.Present(false, 100, "鼠标上报电量为 100%。\n可以查看鼠标的充满指示灯。"));
        menu.Items.Add(preview);
        menu.Items.Add("导出诊断信息…", null, async (_, _) => await ExportDiagnostics());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitThread());
        tray = new() { ContextMenuStrip = menu, Text = "S9 Pro · 正在读取电量", Visible = false };
        tray.DoubleClick += (_, _) => Refresh(true);
        Render(status);
        tray.Visible = true;
        window = new(() => Refresh(true), () => Render(status, true));
        timer.Tick += (_, _) => Refresh(false);
        timer.Start();
        Refresh(true);
    }

    private async void Refresh(bool force)
    {
        if (closing) return;
        pendingForce |= force;
        if (busy) return;
        busy = true;
        try
        {
            do
            {
                bool requestedForce = pendingForce;
                pendingForce = false;
                MouseStatus result;
                try { result = await Task.Run(() => monitor.Poll(requestedForce)); }
                catch (Exception ex)
                {
                    log.Write($"轮询失败：{ex.Message}");
                    result = new(Connection.Error, status.Battery, status.UpdatedAt, ex.Message);
                }
                if (closing) return;
                status = result;
                Render(result);
                if (chargeNotice.Update(result) && settings.FullBatteryEnabled)
                {
                    toast.Present(false, 100, result.Battery?.Charging == true ?
                        "接收器电量读数为 100%。\n有线仍上报充电中。" : "鼠标上报电量为 100%。\n可以查看鼠标的充满指示灯。");
                    log.Write("已显示自定义电量 100% 提醒");
                }
                if (lowNotice.Update(result, settings.LowBatteryThreshold) && settings.LowBatteryEnabled)
                {
                    toast.Present(true, result.Battery!.Level,
                        $"电量低于你设置的 {settings.LowBatteryThreshold}%。\n请连接充电线。");
                    log.Write($"已显示低电量提醒：{result.Battery.Level}% 阈值={settings.LowBatteryThreshold}%");
                }
            } while (pendingForce && !closing);
        }
        finally { busy = false; }
    }

    private void QueueEventRefresh()
    {
        if (closing) return;
        try { dispatcher.BeginInvoke((Action)(() => Refresh(false))); }
        catch (InvalidOperationException) { }
    }

    private void Render(MouseStatus value, bool forceIcon = false)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        var key = (TrayIconStyle.From(value, settings.LowBatteryThreshold), size);
        if (forceIcon || iconKey != key)
        {
            var replacement = TrayIconRenderer.Create(key.Item1, size);
            var previous = icon;
            tray.Icon = replacement;
            icon = replacement; iconKey = key;
            previous?.Dispose();
        }
        string tooltip = value.Tooltip.Length > 127 ? value.Tooltip[..127] : value.Tooltip;
        if (tray.Text != tooltip) tray.Text = tooltip;
        string description = value.Description + " · " + value.BatteryText;
        if (statusItem.Text != description) statusItem.Text = description;
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return string.Equals(key?.GetValue("S9Battery") as string, $"\"{Environment.ProcessPath}\"", StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureAlerts()
    {
        using var dialog = new AlertSettingsDialog(settings);
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            var updated = dialog.Settings.Normalize();
            AlertPreferences.Save(updated);
            if (settings.LowBatteryThreshold != updated.LowBatteryThreshold ||
                settings.LowBatteryEnabled != updated.LowBatteryEnabled) lowNotice = new();
            if (settings.FullBatteryEnabled != updated.FullBatteryEnabled) chargeNotice = new();
            settings = updated;
            toast.Dismiss();
            Render(status);
            Refresh(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { MessageBox.Show(ex.Message, "提醒设置未保存"); }
    }

    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (IsStartupEnabled()) key.DeleteValue("S9Battery", false);
            else key.SetValue("S9Battery", $"\"{Environment.ProcessPath}\"");
            startupItem.Checked = IsStartupEnabled();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "无法修改开机启动"); }
    }

    private async Task ExportDiagnostics()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出 S9 电量诊断信息", Filter = "ZIP 文件|*.zip",
            FileName = $"S9Battery-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            string info = await Task.Run(monitor.Diagnostics);
            var logs = log.Snapshot();
            using var file = new FileStream(dialog.FileName, FileMode.Create);
            using var zip = new ZipArchive(file, ZipArchiveMode.Create);
            WriteEntry(zip, "devices.txt", info + $"\r\nLowBatteryEnabled={settings.LowBatteryEnabled} " +
                $"LowBatteryThreshold={settings.LowBatteryThreshold} FullBatteryEnabled={settings.FullBatteryEnabled}\r\n");
            WriteEntry(zip, "queries.log", logs);
            MessageBox.Show("诊断信息已导出。包含前行者设备标识、查询响应和错误，不包含鼠标按键或移动记录。", "S9 电量工具");
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "导出失败"); }
    }

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), System.Text.Encoding.UTF8);
        writer.Write(text);
    }

    protected override void ExitThreadCore()
    {
        closing = true;
        timer.Stop();
        toast.Dismiss();
        tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            timer.Dispose();
            toast.Dispose();
            monitor.Dispose();
            window.Dispose();
            tray.Dispose();
            menu.Dispose();
            icon?.Dispose();
            dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class DeviceWindow : NativeWindow, IDisposable
{
    private readonly Action refresh, redraw;
    private readonly int taskbarCreated;
    public DeviceWindow(Action refresh, Action redraw)
    {
        this.refresh = refresh;
        this.redraw = redraw;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        CreateHandle(new CreateParams { Caption = "S9 Battery events", Style = 0 });
    }
    protected override void WndProc(ref Message message)
    {
        // Debounced by the single polling worker; the 5-second scan also catches missed notifications.
        if (message.Msg == 0x0219 || message.Msg == 0x0218 && message.WParam.ToInt64() is 7 or 18)
            refresh();
        if (message.Msg == 0x02E0 || message.Msg == taskbarCreated) redraw();
        base.WndProc(ref message);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);
    public void Dispose() => DestroyHandle();
}

internal sealed class DiagnosticLog
{
    private readonly object gate = new();
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S9Battery");
    private string FilePath => Path.Combine(directory, "queries.log");
    public void Write(string message)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1_000_000)
                    File.Move(FilePath, FilePath + ".previous", true);
                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {message}\r\n");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    public string Snapshot()
    {
        lock (gate)
            return (File.Exists(FilePath + ".previous") ? File.ReadAllText(FilePath + ".previous") : "") +
                (File.Exists(FilePath) ? File.ReadAllText(FilePath) : "");
    }
}
