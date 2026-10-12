using S9Battery.Core;

namespace S9Battery.Tray;

internal sealed class MouseMonitor : IDisposable
{
    private readonly BatteryMonitor monitor;
    private readonly BatteryEvents events;
    private readonly ChargeEstimate estimate = new();
    private IReadOnlyList<HidDevice> devices = Array.Empty<HidDevice>();
    private DateTimeOffset nextEnumeration;
    private MouseStatus last = new(Connection.Disconnected);
    public MouseMonitor(Action<string> log, Action changed)
    {
        events = new(log, changed);
        monitor = new(() =>
        {
            // Device-change messages and explicit refresh invalidate this cache immediately.
            // The fallback scan catches missed events without opening every HID every five seconds.
            if (DateTimeOffset.UtcNow >= nextEnumeration)
            {
                devices = HidDevice.Enumerate();
                nextEnumeration = DateTimeOffset.UtcNow.AddSeconds(30);
            }
            return devices.Where(d => d.Candidate)
                .Select(d => new DeviceEndpoint(d.Path, d.Product, () => new HidQueryConnection(d, log))).ToArray();
        }, log) { EventProvider = events.Get };
    }
    public MouseStatus Poll(bool force)
    {
        if (force) nextEnumeration = DateTimeOffset.MinValue;
        var status = monitor.Poll(force);
        events.Sync(devices, monitor.VerifiedDeviceIds);
        return last = status with { EstimatedMinutes = estimate.Update(status) };
    }

    public string Diagnostics()
    {
        var devices = HidDevice.Enumerate();
        var status = last;
        return "S9 Battery 1.2.3\r\n" + $"Windows: {Environment.OSVersion}\r\n" +
            $"状态: {status.Tooltip}\r\n错误: {status.Detail}\r\n" + events.Diagnostics() + "\r\n" +
            string.Join("\r\n", devices.Select(d =>
                $"VID={d.Vendor:X4} PID={d.Product:X4} Usage={d.UsagePage:X4}:{d.Usage:X4} " +
                $"FeatureLength={d.FeatureLength} InputLength={d.InputLength} ContainerId={d.ContainerId} Candidate={d.Candidate} Name={d.Name}"));
    }
    public void Dispose() => events.Dispose();

    private sealed class HidQueryConnection : IQueryConnection
    {
        private readonly HidDevice device;
        private readonly Action<string> log;
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
        public HidQueryConnection(HidDevice device, Action<string> log)
        { this.device = device; this.log = log; handle = device.Open(); }
        public byte[] Query(Query query) => device.Query(handle, query, log);
        public void Dispose() => handle.Dispose();
    }
}
