using System.Text.Json;
using S9Battery.Core;

namespace S9Battery.Tray;

internal static class AlertPreferences
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "S9Battery", "settings.json");

    public static AlertSettings Load(Action<string> log)
    {
        try
        {
            return File.Exists(FilePath) ?
                (JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(FilePath)) ?? new()).Normalize() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { log($"提醒设置读取失败，使用默认设置：{ex.Message}"); return new(); }
    }

    public static void Save(AlertSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}

internal sealed class AlertSettingsDialog : Form
{
    private readonly CheckBox low, full;
    private readonly NumericUpDown threshold;
    public AlertSettings Settings => new(low.Checked, (int)threshold.Value, full.Checked);

    public AlertSettingsDialog(AlertSettings settings)
    {
        Text = "S9 Pro · 提醒设置";
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        ClientSize = new Size(420, 300);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 28, 35);
        ForeColor = Color.FromArgb(235, 240, 247);
        Controls.Add(new Label { Text = "电量提醒", Font = new Font(Font, FontStyle.Bold),
            Location = new Point(24, 20), Size = new Size(350, 28) });
        low = new() { Text = "低电量时提醒我充电", Checked = settings.LowBatteryEnabled,
            Location = new Point(24, 60), Size = new Size(330, 28) };
        Controls.Add(low);
        Controls.Add(new Label { Text = "电量低于", Location = new Point(45, 103), Size = new Size(80, 28) });
        threshold = new() { Minimum = 1, Maximum = 100, Value = settings.LowBatteryThreshold,
            Location = new Point(130, 100), Size = new Size(76, 30), BackColor = Color.FromArgb(40, 46, 57),
            ForeColor = ForeColor, Enabled = low.Checked };
        Controls.Add(threshold);
        Controls.Add(new Label { Text = "% 时提醒", Location = new Point(216, 103), Size = new Size(120, 28) });
        low.CheckedChanged += (_, _) => threshold.Enabled = low.Checked;
        full = new() { Text = "充电后电量达到 100% 时提醒", Checked = settings.FullBatteryEnabled,
            Location = new Point(24, 147), Size = new Size(350, 28) };
        Controls.Add(full);
        Controls.Add(new Label { Text = "气泡显示在右下角；悬停暂停，点击关闭。",
            ForeColor = Color.FromArgb(153, 166, 185), Location = new Point(24, 193), Size = new Size(380, 35) });
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel,
            Location = new Point(210, 248), Size = new Size(82, 32), FlatStyle = FlatStyle.Flat };
        var save = new Button { Text = "保存", DialogResult = DialogResult.OK,
            Location = new Point(304, 248), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(92, 220, 160), ForeColor = Color.FromArgb(15, 30, 24) };
        Controls.Add(cancel); Controls.Add(save);
        AcceptButton = save; CancelButton = cancel;
    }
}
