namespace S9Battery.Core;

public interface IQueryConnection : IDisposable
{
    byte[] Query(Query query);
}
public record DeviceEndpoint(string Id, ushort Product, Func<IQueryConnection> Open);
public record BatteryEvent(int Level, DateTimeOffset ReceivedAt);

// Single-worker polling policy, independent of the Windows HID transport.
public sealed class BatteryMonitor(Func<IReadOnlyList<DeviceEndpoint>> enumerate,
    Action<string> log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.Now);
    private readonly HashSet<string> verifiedIds = [];
    public IReadOnlyCollection<string> VerifiedDeviceIds => verifiedIds.ToArray();
    public Func<string, BatteryEvent?>? EventProvider { get; set; }
    public MouseStatus Status { get; private set; } = new(Connection.Disconnected);
    private MouseStatus queriedStatus = new(Connection.Disconnected);
    private string fingerprint = "", selectedId = "";
    private DateTimeOffset nextQuery = DateTimeOffset.MinValue;
    private DateTimeOffset? chargeStarted;

    public MouseStatus Poll(bool force)
    {
        DeviceEndpoint[] devices;
        try { devices = enumerate().OrderBy(d => Protocol.IsWireless(d.Product)).ThenBy(d => d.Id).ToArray(); }
        catch (Exception ex)
        {
            log($"枚举设备失败：{ex.Message}");
            nextQuery = DateTimeOffset.MinValue;
            return SetResult(new(Connection.Error, Status.Battery, Status.UpdatedAt, ex.Message,
                LevelUpdatedAt: Status.LevelUpdatedAt), "");
        }
        var present = devices.Select(d => d.Id).ToHashSet();
        verifiedIds.IntersectWith(present);
        string current = string.Join('|', devices.Select(d => d.Id));
        if (current != fingerprint) { force = true; fingerprint = current; }
        if (devices.Length == 0)
            return SetResult(new(Connection.Disconnected, Status.Battery, Status.UpdatedAt,
                LevelUpdatedAt: Status.LevelUpdatedAt), "");
        if (!force && now() < nextQuery) return Status = ApplyEvent();
        nextQuery = now().AddSeconds(60);
        bool sleeping = false;
        string? error = null;
        (DeviceEndpoint Device, MouseStatus Status)? primary = null;
        foreach (var device in devices)
        {
            if (primary is not null && !Protocol.IsWireless(device.Product)) continue;
            try
            {
                using var connection = device.Open();
                ulong identity = Protocol.ParseIdentity(connection.Query(Query.Identity));
                if (identity != Protocol.S9ProIdentity)
                {
                    verifiedIds.Remove(device.Id);
                    log($"跳过其他型号：PID={device.Product:X4} UUID={identity:X12}");
                    continue;
                }
                verifiedIds.Add(device.Id);
                bool wireless = Protocol.IsWireless(device.Product);
                if (wireless && !Protocol.ParseOnline(connection.Query(Query.Online)))
                { sleeping = true; continue; }
                var battery = Protocol.ParseBattery(connection.Query(Query.Battery));
                var readAt = now();
                if (primary is null)
                {
                    primary = (device, new(wireless ? Connection.Wireless : Connection.Wired,
                        battery, readAt, LevelUpdatedAt: readAt));
                    if (wireless || !battery.Charging) break;
                }
                else if (wireless && battery.HasReliableLevel)
                {
                    // Keep the actual wired charging flag. The independently queried receiver
                    // must be online, have the same model identity, and report non-charging data.
                    primary = (primary.Value.Device, primary.Value.Status with
                    {
                        Battery = new(battery.Level, true, BatterySource.Receiver),
                        LevelUpdatedAt = readAt
                    });
                    log($"充电电量来自实时接收器查询：{battery.Level}%");
                    break;
                }
            }
            catch (Exception ex)
            { error = ex.Message; log($"查询失败 PID={device.Product:X4}：{ex.Message}"); }
        }
        if (primary is { } value) return SetResult(value.Status, value.Device.Id);
        nextQuery = now().AddSeconds(sleeping ? 15 : 5);
        return SetResult(new(sleeping ? Connection.Sleeping : error is not null ? Connection.Error : Connection.Disconnected,
            Status.Battery, Status.UpdatedAt, error ?? (sleeping ? null : "未检测到 S9 Pro 标识"),
            LevelUpdatedAt: Status.LevelUpdatedAt), "");
    }

    private MouseStatus SetResult(MouseStatus result, string id)
    {
        bool charging = result.Fresh && result.Battery?.Charging == true;
        if (charging) nextQuery = now().AddSeconds(15);
        if (!charging) chargeStarted = null;
        else if (chargeStarted is null || id != selectedId || !queriedStatus.Fresh)
            chargeStarted = now();
        queriedStatus = result;
        selectedId = id;
        return Status = ApplyEvent();
    }

    private MouseStatus ApplyEvent()
    {
        var result = queriedStatus;
        if (!result.Fresh || result.Battery is null || EventProvider?.Invoke(selectedId) is not { } sample)
            return result;
        var age = now() - sample.ReceivedAt;
        if (sample.Level is < 0 or > 100 || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(90))
            return result;
        if (result.Connection == Connection.Wireless && !result.Battery.Charging &&
            (result.LevelUpdatedAt ?? result.UpdatedAt) is { } levelAt && sample.ReceivedAt > levelAt)
        {
            // Real wireless FA 05 events agree with feature reads. Keep the latest accepted
            // reading until the next query, rather than reverting when that event expires.
            // An event does not change the query schedule or override a newer query.
            return queriedStatus = result with
            {
                Battery = new(sample.Level, false, BatterySource.WirelessEvent),
                LevelUpdatedAt = sample.ReceivedAt
            };
        }
        if (result.Battery is not { Charging: true, HasReliableLevel: false } ||
            chargeStarted is not { } start || sample.ReceivedAt < start)
            return result;
        // An unsolicited event doesn't contain a charging bit or proven completion flag.
        // Show sub-100 event values as candidates, without promoting them to trusted estimates.
        return result with
        {
            Battery = new(sample.Level, true, BatterySource.DeviceEvent),
            LevelUpdatedAt = sample.ReceivedAt
        };
    }
}
