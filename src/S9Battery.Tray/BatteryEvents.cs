using System.Collections.Concurrent;
using S9Battery.Core;

namespace S9Battery.Tray;

// Only vendor collections belonging to a UUID-verified device are listened to.
// Input report reads use overlapped IO and cancellation; ordinary mouse collections are excluded.
internal sealed class BatteryEvents(Action<string> log, Action changed) : IDisposable
{
    private sealed class Listener(HidDevice device, string queryId)
    {
        public HidDevice Device { get; } = device;
        public string QueryId { get; } = queryId;
        public CancellationTokenSource Stop { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public volatile bool Stopped;
        public int Reports, BatteryReports;
        public DateTimeOffset RetryAfter { get; } = DateTimeOffset.Now.AddSeconds(30);
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Listener> listeners = [];
    private sealed record Received(BatteryEvent Sample, Listener Owner);
    private readonly ConcurrentDictionary<string, Received> samples = new();
    private bool disposed;

    public BatteryEvent? Get(string id) => samples.TryGetValue(id, out var received) &&
        !received.Owner.Stopped && !received.Owner.Task.IsCompleted ? received.Sample : null;

    public void Sync(IReadOnlyList<HidDevice> devices, IReadOnlyCollection<string> verifiedIds)
    {
        var verified = devices.Where(d => verifiedIds.Contains(d.Path)).ToArray();
        var desired = devices.Where(d => Protocol.IsCandidate(d.Vendor, d.Product) && d.UsagePage >= 0xFF00 &&
                d.InputLength is >= 4 and <= 4096)
            .Select(d => (Device: d, Owner: verified.FirstOrDefault(v => v.Path == d.Path ||
                v.Product == d.Product && v.ContainerId != Guid.Empty && v.ContainerId == d.ContainerId)))
            .Where(d => d.Owner is not null).ToDictionary(d => d.Device.Path);
        lock (gate)
        {
            if (disposed) return;
            foreach (var key in listeners.Keys.ToArray())
            {
                var existing = listeners[key];
                if (!desired.TryGetValue(key, out var expected) || existing.QueryId != expected.Owner!.Path ||
                    existing.Task.IsCompleted && DateTimeOffset.Now >= existing.RetryAfter)
                {
                    Stop(existing); listeners.Remove(key);
                    samples.TryRemove(existing.QueryId, out _);
                }
            }
            foreach (var entry in desired.Values)
            {
                if (listeners.ContainsKey(entry.Device.Path)) continue;
                var listener = new Listener(entry.Device, entry.Owner!.Path);
                listeners.Add(entry.Device.Path, listener);
                listener.Task = Task.Run(() => Listen(entry.Device, listener));
            }
        }
    }

    private async Task Listen(HidDevice device, Listener listener)
    {
        var token = listener.Stop.Token;
        try
        {
            using var stream = device.OpenInput();
            log($"事件通道已打开 PID={device.Product:X4} Usage={device.UsagePage:X4}:{device.Usage:X4} InputLength={device.InputLength}");
            var buffer = new byte[device.InputLength];
            while (!token.IsCancellationRequested)
            {
                int count = await stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (token.IsCancellationRequested) break;
                if (count == 0) throw new IOException("电量事件通道关闭");
                Interlocked.Increment(ref listener.Reports);
                if (count < 4 || buffer[1] != 0xFA || buffer[2] != 5) continue;
                Interlocked.Increment(ref listener.BatteryReports);
                // Preserve even out-of-range battery events for protocol research, never for display.
                log($"BatteryEventRaw PID={device.Product:X4} Raw={buffer[3]} RX={Convert.ToHexString(buffer.AsSpan(0, count))}");
                if (Protocol.ParseBatteryEvent(buffer.AsSpan(0, count)) is not { } level) continue;
                var sample = new BatteryEvent(level, DateTimeOffset.Now);
                lock (gate)
                {
                    if (disposed || !listeners.TryGetValue(device.Path, out var active) || active != listener)
                        break;
                    var received = new Received(sample, listener);
                    samples.AddOrUpdate(listener.QueryId, received, (_, previous) =>
                        !previous.Owner.Stopped && previous.Sample.ReceivedAt > sample.ReceivedAt ? previous : received);
                }
                changed();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { if (!token.IsCancellationRequested) log($"事件通道失败 PID={device.Product:X4}：{ex.Message}"); }
        finally { listener.Stopped = true; listener.Stop.Dispose(); }
    }

    public string Diagnostics()
    {
        lock (gate)
            return $"EventListeners={listeners.Count}\r\n" + string.Join("\r\n", listeners.Select(l =>
                $"Listener PID={l.Value.Device.Product:X4} Usage={l.Value.Device.UsagePage:X4}:{l.Value.Device.Usage:X4} " +
                $"Task={l.Value.Task.Status} Reports={Volatile.Read(ref l.Value.Reports)} " +
                $"BatteryEvents={Volatile.Read(ref l.Value.BatteryReports)} LatestLevel=" +
                (Get(l.Value.QueryId) is { } sample ? $"{sample.Level} Time={sample.ReceivedAt:O}" : "none")));
    }

    private static void Stop(Listener listener)
    {
        listener.Stopped = true;
        try { listener.Stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            foreach (var listener in listeners.Values) Stop(listener);
            listeners.Clear(); samples.Clear();
        }
    }
}
