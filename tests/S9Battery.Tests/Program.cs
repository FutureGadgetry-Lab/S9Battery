using S9Battery.Core;

int passed = 0;
void Test(string name, Action action)
{
    try { action(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex}"); Environment.Exit(1); }
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"expected {expected}, got {actual}");
}
void Invalid(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("invalid response was accepted");
}
byte[] Response(Query query, params byte[] payload)
{
    byte[] report = new byte[64];
    report[0] = 3; report[1] = 0x50; report[3] = (byte)payload.Length;
    report[5] = Protocol.ResponseOffset(query);
    payload.CopyTo(report, 6);
    return report;
}
MouseStatus Status(int level, bool charging = false, Connection connection = Connection.Wired) =>
    new(connection, new(level, charging), DateTimeOffset.Now);

Test("read command bytes agree with official SDK encoding", () =>
{
    // Native report ID + SDK's exact 63-byte packets, observed with a mock transport.
    var cases = new[] { (Query.Identity, "031F5000004F80"), (Query.Battery, "03205000004F81"), (Query.Online, "03975000004700") };
    foreach (var (query, prefix) in cases)
        Equal(prefix + new string('0', 114), Convert.ToHexString(Protocol.Encode(query)));
});
Test("captured S9 Pro wired and wireless identity responses", () =>
{
    foreach (var hex in new[]
    {
        "0350000B0000030109000000000E0001000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
        "0350000B0000030109000000000E0101000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000"
    })
    {
        var payload = Protocol.Decode(Query.Identity, Convert.FromHexString(hex));
        Equal(11, payload.Length);
        Equal(Protocol.S9ProIdentity, Protocol.ParseIdentity(payload));
    }
});
Test("captured basic-info response at returned offset one", () =>
{
    // Observed among Identity reads in the user log: reject as Identity, accept as Battery.
    var response = Convert.FromHexString("0350000B000149210701906504A55A00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
    Invalid(() => Protocol.Decode(Query.Identity, response));
    Equal(new BatteryReading(73, false), Protocol.ParseBattery(Protocol.Decode(Query.Battery, response)));
    // Synthetic charging-bit variant of the captured basic-info packet.
    response[6] |= 0x80;
    Equal(new BatteryReading(73, true), Protocol.ParseBattery(Protocol.Decode(Query.Battery, response)));
});
Test("captured busy responses remain rejected", () =>
{
    foreach (var prefix in new[] { "030002000000", "035002000000" })
        Invalid(() => Protocol.Decode(Query.Identity, Convert.FromHexString(prefix + new string('0', 116))));
});
Test("wireless zero and full battery", () =>
{
    foreach (byte level in new byte[] { 0, 20, 85, 100 })
        Equal(new BatteryReading(level, false), Protocol.ParseBattery(Protocol.Decode(Query.Battery,
            Response(Query.Battery, level, 0, 0, 0, 0, 0, 0))));
});
Test("charging flag is parsed but the raw percentage is not reliable", () =>
{
    Equal(new BatteryReading(85, true), Protocol.ParseBattery(new byte[] { 0xD5, 0, 0, 0, 0, 0, 0 }));
    Equal(new BatteryReading(100, true), Protocol.ParseBattery(new byte[] { 0xE4, 0, 0, 0, 0, 0, 0 }));
    Equal(false, Protocol.ParseBattery(new byte[] { 0xE4, 0, 0, 0, 0, 0, 0 }).HasReliableLevel);
});
Test("native response includes report ID, no second stripping", () =>
{
    var report = Response(Query.Battery, 85, 0, 0, 0, 0, 0, 0);
    Equal(85, Protocol.ParseBattery(Protocol.Decode(Query.Battery, report)).Level);
    Invalid(() => Protocol.Decode(Query.Battery, report.AsSpan(1)));
});
Test("stale, wrong, truncated and rejected responses", () =>
{
    var report = Response(Query.Battery, 85, 0, 0, 0, 0, 0, 0);
    Invalid(() => Protocol.Decode(Query.Identity, report));
    Invalid(() => Protocol.Decode(Query.Battery, report.AsSpan(0, 12)));
    foreach (int index in new[] { 0, 2, 4, 5 })
    {
        var bad = (byte[])report.Clone(); bad[index] ^= 1;
        Invalid(() => Protocol.Decode(Query.Battery, bad));
    }
    report[3] = 59; Invalid(() => Protocol.Decode(Query.Battery, report));
    report[3] = 255; Invalid(() => Protocol.Decode(Query.Battery, report));
    Invalid(() => Protocol.ParseBattery(new byte[] { 127, 0, 0, 0, 0, 0, 0 }));
    Invalid(() => Protocol.ParseBattery(Array.Empty<byte>()));
});
Test("identity matches S9 Pro; shared receiver is not enough", () =>
{
    Equal(Protocol.S9ProIdentity, Protocol.ParseIdentity(Protocol.Decode(Query.Identity,
        Response(Query.Identity, 0x21, 0, 9, 0, 0, 0, 0, 0x0E))));
    Equal(false, Protocol.S9ProIdentity == Protocol.ParseIdentity(new byte[] { 0, 0, 9, 0, 0, 0, 0, 0x2A }));
    Equal(true, Protocol.IsCandidate(0x372E, 0x103D));
    Equal(false, Protocol.IsCandidate(0x372E, 0x105E));
    Equal(false, Protocol.IsWireless(0x1029));
});
Test("online, sleeping and invalid online flag", () =>
{
    Equal(true, Protocol.ParseOnline(Protocol.Decode(Query.Online, Response(Query.Online, 1))));
    Equal(false, Protocol.ParseOnline(new byte[] { 0 }));
    Invalid(() => Protocol.ParseOnline(new byte[] { 2 }));
});
Test("charging to 100 percent notifies once across disconnect and failures", () =>
{
    var notice = new ChargeNotice();
    Equal(false, notice.Update(Status(99)));
    Equal(false, notice.Update(Status(100, true)));
    Equal(true, notice.Update(Status(100)));
    Equal(false, notice.Update(Status(100)));
    Equal(false, notice.Update(Status(100, true, Connection.Disconnected)));
    Equal(false, notice.Update(Status(99, true, Connection.Error)));
    Equal(false, notice.Update(Status(100)));
    Equal(false, notice.Update(Status(99)));
    Equal(false, notice.Update(Status(100, true)));
    Equal(true, notice.Update(Status(100, false)));
});
Test("unplugging and stale readings never claim charging completion", () =>
{
    var notice = new ChargeNotice();
    Equal(false, notice.Update(Status(100, true)));
    foreach (var connection in new[] { Connection.Disconnected, Connection.Error, Connection.Sleeping })
        Equal(false, notice.Update(Status(100, false, connection)));
    Equal(true, notice.Update(Status(100, false, Connection.Wireless)));
    Equal(false, notice.Update(Status(100, true, Connection.Wireless)));
    Equal("--", Status(85, true, Connection.Disconnected).IconText);
    Equal("85", Status(85, false, Connection.Sleeping).IconText);
    Equal(true, Status(100, true).Description.Contains("正在充电"));
    Equal(false, Status(100).Description.Contains("已充满"));
});
Test("captured wireless online response uses main command 47 in offset field", () =>
{
    var online = Convert.FromHexString("0350000B00470109000000000E000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
    Equal(true, Protocol.ParseOnline(Protocol.Decode(Query.Online, online)));
    var identity = Convert.FromHexString("0350000B0000030109000000000E0101000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
    Invalid(() => Protocol.Decode(Query.Online, identity));
    Invalid(() => Protocol.Decode(Query.Identity, online));
});
Test("captured wireless battery is 78 percent, not the charging sentinel", () =>
{
    var report = Convert.FromHexString("0350000B00014E210701906504A55A00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
    Invalid(() => Protocol.Decode(Query.Online, report));
    var battery = Protocol.ParseBattery(Protocol.Decode(Query.Battery, report));
    Equal(new BatteryReading(78, false), battery);
    Equal(true, battery.HasReliableLevel);
    Equal("78", new MouseStatus(Connection.Wireless, battery).IconText);
});
Test("captured wired charging 100 is hidden and never triggers full notice", () =>
{
    var report = Convert.FromHexString("0350000B0001E4210401906504A55A00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000");
    var battery = Protocol.ParseBattery(Protocol.Decode(Query.Battery, report));
    Equal(new BatteryReading(100, true), battery);
    var status = new MouseStatus(Connection.Wired, battery, DateTimeOffset.Now);
    Equal("充", status.IconText);
    Equal(false, status.Tooltip.Contains("100%"));
    Equal(true, status.Tooltip.Contains("电量不可确认"));
    Equal(false, new ChargeNotice().Update(status));
    Equal(false, new ChargeNotice().Update(Status(100, true, Connection.Wireless)));
});
Test("replay of the real wireless query sequence reaches battery reading", () =>
{
    var endpoint = new DeviceEndpoint("captured-receiver", 0x1026, () => new CapturedConnection());
    var monitor = new BatteryMonitor(() => new[] { endpoint }, _ => { });
    var status = monitor.Poll(false);
    Equal(Connection.Wireless, status.Connection);
    Equal(78, status.Battery!.Level);
    Equal("78", status.IconText);
});
Test("wireless to wired, unplug and replug bypass the 60 second interval", () =>
{
    var wireless = new FakeDevice(0x103D, 85);
    var wired = new FakeDevice(0x1029, 90, charging: true);
    IReadOnlyList<DeviceEndpoint> devices = new[] { wireless.Endpoint };
    var monitor = new BatteryMonitor(() => devices, _ => { });
    Equal(Connection.Wireless, monitor.Poll(false).Connection);
    devices = new[] { wireless.Endpoint, wired.Endpoint };
    Equal(Connection.Wired, monitor.Poll(false).Connection);
    Equal(85, monitor.Status.Battery!.Level);
    Equal(BatterySource.Receiver, monitor.Status.Battery.Source);
    Equal(true, monitor.Status.Battery.HasReliableLevel);
    Equal(true, monitor.Status.Battery.Charging);
    Equal(2, wireless.BatteryQueries);
    devices = new[] { wireless.Endpoint };
    Equal(Connection.Wireless, monitor.Poll(false).Connection);
    devices = Array.Empty<DeviceEndpoint>();
    Equal(Connection.Disconnected, monitor.Poll(false).Connection);
    Equal("--", monitor.Status.IconText);
    devices = new[] { wired.Endpoint };
    Equal(Connection.Wired, monitor.Poll(false).Connection);
    Equal(true, wireless.Disposals > 0 && wired.Disposals > 0);
});
Test("wired query failure falls back to a healthy receiver", () =>
{
    var wired = new FakeDevice(0x1029, 90) { Fail = true };
    var wireless = new FakeDevice(0x103D, 85);
    var monitor = new BatteryMonitor(() => new[] { wired.Endpoint, wireless.Endpoint }, _ => { });
    Equal(Connection.Wireless, monitor.Poll(false).Connection);
    Equal(1, wired.Disposals);
    Equal(1, wireless.Disposals);
});
Test("sleeping mouse preserves the reading and retries after 15 seconds", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x103D, 85);
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time);
    var initial = monitor.Poll(false);
    device.Online = false;
    var sleeping = monitor.Poll(true);
    Equal(Connection.Sleeping, sleeping.Connection);
    Equal(initial.UpdatedAt, sleeping.UpdatedAt);
    Equal(85, sleeping.Battery!.Level);
    device.Online = true;
    time = time.AddSeconds(14);
    Equal(Connection.Sleeping, monitor.Poll(false).Connection);
    time = time.AddSeconds(1);
    Equal(Connection.Wireless, monitor.Poll(false).Connection);
});
Test("query error preserves timestamp and retries after 5 seconds", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x1029, 90, charging: true);
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time);
    var initial = monitor.Poll(false);
    device.Fail = true;
    Equal(Connection.Error, monitor.Poll(true).Connection);
    Equal(initial.UpdatedAt, monitor.Status.UpdatedAt);
    Equal("--", monitor.Status.IconText);
    device.Fail = false;
    time = time.AddSeconds(4);
    Equal(Connection.Error, monitor.Poll(false).Connection);
    time = time.AddSeconds(1);
    Equal(Connection.Wired, monitor.Poll(false).Connection);
});
Test("shared receiver with another model never supplies battery readings", () =>
{
    var device = new FakeDevice(0x103D, 60) { Identity = 0x09000000002A };
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { });
    Equal(Connection.Disconnected, monitor.Poll(false).Connection);
    Equal(0, device.BatteryQueries);
});
Test("stable device is polled only every 60 seconds; manual refresh overrides", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x103D, 85);
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time);
    monitor.Poll(false);
    time = time.AddSeconds(59); monitor.Poll(false);
    Equal(1, device.BatteryQueries);
    time = time.AddSeconds(1); monitor.Poll(false);
    Equal(2, device.BatteryQueries);
    monitor.Poll(true);
    Equal(3, device.BatteryQueries);
});
Test("enumeration failure stays stale until a successful new query", () =>
{
    var device = new FakeDevice(0x1029, 85);
    bool fail = false;
    var monitor = new BatteryMonitor(() => fail ? throw new IOException("scan failure") : new[] { device.Endpoint }, _ => { });
    var initial = monitor.Poll(false);
    fail = true;
    Equal(Connection.Error, monitor.Poll(false).Connection);
    Equal(initial.UpdatedAt, monitor.Status.UpdatedAt);
    fail = false;
    device.Level = 86;
    Equal(86, monitor.Poll(false).Battery!.Level);
    Equal(2, device.BatteryQueries);
});
Test("vendor battery event parser follows official FA 05 format only", () =>
{
    Equal<int?>(78, Protocol.ParseBatteryEvent(new byte[] { 3, 0xFA, 5, 78, 0, 0 }));
    Equal<int?>(78, Protocol.ParseBatteryEvent(new byte[] { 7, 0xFA, 5, 78 }));
    Equal<int?>(null, Protocol.ParseBatteryEvent(new byte[] { 3, 0xFA, 3, 78 }));
    Equal<int?>(null, Protocol.ParseBatteryEvent(new byte[] { 3, 0xFA, 5, 228 }));
    Equal<int?>(null, Protocol.ParseBatteryEvent(new byte[] { 3, 0x50, 0, 78 }));
    Equal<int?>(null, Protocol.ParseBatteryEvent(new byte[] { 3, 0xFA, 5 }));
    Equal<int?>(null, Protocol.ParseBatteryEvent(new byte[] { 1, 1, 4, 78, 20 }));
});
Test("charging can use an independent live receiver percentage", () =>
{
    var wired = new FakeDevice(0x1029, 100, charging: true);
    var receiver = new FakeDevice(0x1026, 78);
    var monitor = new BatteryMonitor(() => new[] { wired.Endpoint, receiver.Endpoint }, _ => { });
    var status = monitor.Poll(false);
    Equal(Connection.Wired, status.Connection);
    Equal(true, status.Battery!.Charging);
    Equal(78, status.Battery.Level);
    Equal(BatterySource.Receiver, status.Battery.Source);
    Equal("78", status.IconText);
    Equal(true, status.Tooltip.Contains("接收器"));
    Equal(false, new ChargeNotice().Update(status));
    Equal(2, monitor.VerifiedDeviceIds.Count);
});
Test("offline, different-model or charging-sentinel receiver never fills the percentage", () =>
{
    foreach (var mode in new[] { "offline", "other-model", "sentinel", "failed" })
    {
        var wired = new FakeDevice(0x1029, 100, charging: true);
        var receiver = new FakeDevice(0x1026, 78);
        if (mode == "offline") receiver.Online = false;
        if (mode == "other-model") receiver.Identity = 0x09000000002A;
        if (mode == "sentinel") { receiver.Charging = true; receiver.Level = 100; }
        if (mode == "failed") receiver.Fail = true;
        var monitor = new BatteryMonitor(() => new[] { wired.Endpoint, receiver.Endpoint }, _ => { });
        var status = monitor.Poll(false);
        Equal(Connection.Wired, status.Connection);
        Equal("充", status.IconText);
        Equal(false, status.Tooltip.Contains("100%"));
    }
});
Test("charging events display candidates without clearing the charging flag", () =>
{
    var time = DateTimeOffset.Now;
    var wired = new FakeDevice(0x1029, 100, charging: true);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { wired.Endpoint }, _ => { }, () => time)
    { EventProvider = _ => sample };
    Equal("充", monitor.Poll(false).IconText);
    time = time.AddSeconds(1); sample = new(78, time);
    var status = monitor.Poll(false);
    Equal("78", status.IconText);
    Equal(true, status.Battery!.Charging);
    Equal(false, status.Battery.HasReliableLevel);
    Equal(BatterySource.DeviceEvent, status.Battery.Source);
    Equal(true, status.Tooltip.Contains("待核实"));
    Equal(time, status.LevelUpdatedAt);
    Equal<int?>(null, new ChargeEstimate().Update(status));
    sample = new(100, time);
    Equal("充", monitor.Poll(false).IconText);
    Equal(false, new ChargeNotice().Update(monitor.Status));
});
Test("old, expired and future events cannot appear as fresh charging percentage", () =>
{
    var time = DateTimeOffset.Now;
    var wired = new FakeDevice(0x1029, 100, charging: true);
    BatteryEvent? sample = new(78, time.AddSeconds(-1));
    var monitor = new BatteryMonitor(() => new[] { wired.Endpoint }, _ => { }, () => time)
    { EventProvider = _ => sample };
    Equal("充", monitor.Poll(false).IconText);
    sample = new(78, time.AddSeconds(1));
    Equal("充", monitor.Poll(false).IconText);
    time = time.AddSeconds(1);
    Equal("78", monitor.Poll(false).IconText);
    time = time.AddSeconds(91);
    Equal("充", monitor.Poll(false).IconText);
});
Test("disconnect or new charging session invalidates old battery events", () =>
{
    var time = DateTimeOffset.Now;
    var wired = new FakeDevice(0x1029, 100, charging: true);
    IReadOnlyList<DeviceEndpoint> devices = new[] { wired.Endpoint };
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => devices, _ => { }, () => time) { EventProvider = _ => sample };
    monitor.Poll(false);
    time = time.AddSeconds(1); sample = new(78, time);
    Equal("78", monitor.Poll(false).IconText);
    devices = Array.Empty<DeviceEndpoint>();
    Equal("--", monitor.Poll(false).IconText);
    Equal(0, monitor.VerifiedDeviceIds.Count);
    time = time.AddSeconds(1); devices = new[] { wired.Endpoint };
    Equal("充", monitor.Poll(false).IconText);
});
Test("an event never overrides a trusted receiver reading", () =>
{
    var time = DateTimeOffset.Now;
    var wired = new FakeDevice(0x1029, 100, charging: true);
    var receiver = new FakeDevice(0x1026, 78);
    var monitor = new BatteryMonitor(() => new[] { wired.Endpoint, receiver.Endpoint }, _ => { }, () => time)
    { EventProvider = _ => new(90, time) };
    Equal(78, monitor.Poll(false).Battery!.Level);
    Equal(BatterySource.Receiver, monitor.Status.Battery!.Source);
});
Test("time estimate requires 3 minutes and multiple trusted rising levels", () =>
{
    var estimator = new ChargeEstimate();
    var at = DateTimeOffset.Now;
    MouseStatus Sample(int level, int seconds) => new(Connection.Wired,
        new(level, true, BatterySource.Receiver), at.AddSeconds(seconds));
    Equal<int?>(null, estimator.Update(Sample(78, 0)));
    Equal<int?>(null, estimator.Update(Sample(80, 60)));
    Equal<int?>(null, estimator.Update(Sample(82, 120)));
    Equal<int?>(8, estimator.Update(Sample(84, 180)));
    Equal<int?>(8, estimator.Update(Sample(84, 180)));
    Equal<int?>(null, estimator.Update(Sample(83, 240)));
});
Test("candidate events, disconnects and long gaps reset charge estimate", () =>
{
    var estimator = new ChargeEstimate();
    var at = DateTimeOffset.Now;
    MouseStatus Sample(int level, int seconds, BatterySource source = BatterySource.Receiver) =>
        new(Connection.Wired, new(level, true, source), at.AddSeconds(seconds));
    estimator.Update(Sample(78, 0)); estimator.Update(Sample(80, 90));
    Equal<int?>(8, estimator.Update(Sample(84, 180)));
    Equal<int?>(null, estimator.Update(Sample(85, 200, BatterySource.DeviceEvent)));
    Equal<int?>(null, estimator.Update(Sample(85, 220)));
    estimator.Update(Sample(86, 310));
    Equal<int?>(null, estimator.Update(Sample(90, 600)));
    Equal<int?>(null, estimator.Update(new(Connection.Disconnected)));
    Equal<int?>(null, estimator.Update(Sample(90, 610)));
});
Test("time estimate rejects flat data and insufficient percentage gain", () =>
{
    var estimator = new ChargeEstimate();
    var at = DateTimeOffset.Now;
    MouseStatus Sample(int level, int seconds) => new(Connection.Wired,
        new(level, true, BatterySource.Receiver), at.AddSeconds(seconds));
    estimator.Update(Sample(78, 0)); estimator.Update(Sample(78, 90));
    Equal<int?>(null, estimator.Update(Sample(78, 180)));
    estimator = new();
    estimator.Update(Sample(78, 0)); estimator.Update(Sample(79, 90));
    Equal<int?>(null, estimator.Update(Sample(80, 180)));
});
Test("reliable receiver 100 can notify but still does not assert charging has ended", () =>
{
    var status = new MouseStatus(Connection.Wired, new(100, true, BatterySource.Receiver), DateTimeOffset.Now);
    Equal("100", status.IconText);
    Equal(true, status.Tooltip.Contains("充电结束仍待确认"));
    Equal(false, status.Tooltip.Contains("已充满"));
    var notice = new ChargeNotice();
    Equal(false, notice.Update(new(Connection.Wired, new(99, true, BatterySource.Receiver), DateTimeOffset.Now)));
    Equal(true, notice.Update(status));
});
Test("starting or restarting at 100 never sends a completion notification", () =>
{
    foreach (var connection in new[] { Connection.Wired, Connection.Wireless })
    {
        var notice = new ChargeNotice();
        Equal(false, notice.Update(Status(100, connection: connection)));
        Equal(false, notice.Update(Status(100, connection: connection)));
        Equal(false, notice.Update(Status(100, connection: Connection.Disconnected)));
        Equal(false, notice.Update(Status(100, connection: connection)));
    }
    var receiverFull = new MouseStatus(Connection.Wired, new(100, true, BatterySource.Receiver), DateTimeOffset.Now);
    var chargingNotice = new ChargeNotice();
    Equal(false, chargingNotice.Update(receiverFull));
    Equal(false, chargingNotice.Update(receiverFull));
});
Test("a battery rise to 100 without observed charging does not notify", () =>
{
    var notice = new ChargeNotice();
    Equal(false, notice.Update(Status(99, connection: Connection.Wireless)));
    Equal(false, notice.Update(Status(100, connection: Connection.Wireless)));
    Equal(false, notice.Update(Status(100)));
});
Test("replug after observed wired charging can report completion", () =>
{
    var notice = new ChargeNotice();
    Equal(false, notice.Update(Status(100, true)));
    Equal(false, notice.Update(Status(100, true, Connection.Disconnected)));
    Equal(false, notice.Update(Status(100, true, Connection.Error)));
    Equal(true, notice.Update(Status(100)));
    Equal(false, notice.Update(Status(100, true)));
    Equal(false, notice.Update(Status(100)));
});
Test("unplugging before full ends the charging episode without a false later notice", () =>
{
    var notice = new ChargeNotice();
    Equal(false, notice.Update(Status(100, true)));
    Equal(false, notice.Update(Status(99, connection: Connection.Wireless)));
    Equal(false, notice.Update(Status(100, connection: Connection.Wireless)));
});
Test("startup full followed by a real charge cycle notifies on the next 100", () =>
{
    var notice = new ChargeNotice();
    Equal(false, notice.Update(Status(100)));
    Equal(false, notice.Update(Status(100, true))); // Reconnecting an already full mouse is not another cycle.
    Equal(false, notice.Update(Status(100)));
    Equal(false, notice.Update(Status(85)));
    Equal(false, notice.Update(Status(100, true)));
    Equal(true, notice.Update(Status(100)));
});
Test("low-battery reminder uses strictly below the chosen threshold including zero", () =>
{
    var notice = new LowBatteryNotice();
    Equal(false, notice.Update(Status(20, connection: Connection.Wireless), 20));
    Equal(true, notice.Update(Status(19, connection: Connection.Wireless), 20));
    Equal(false, notice.Update(Status(18, connection: Connection.Wireless), 20));
    notice = new();
    Equal(false, notice.Update(Status(1), 1));
    Equal(true, notice.Update(Status(0), 1));
});
Test("threshold fluctuations do not repeat low-battery reminders", () =>
{
    var notice = new LowBatteryNotice();
    Equal(true, notice.Update(Status(19), 20));
    Equal(false, notice.Update(Status(20), 20));
    Equal(false, notice.Update(Status(19), 20));
    Equal(false, notice.Update(Status(22), 20));
    Equal(false, notice.Update(Status(19), 20));
    Equal(false, notice.Update(Status(23), 20));
    Equal(true, notice.Update(Status(19), 20));
});
Test("charging suppresses and rearms the low-battery reminder", () =>
{
    var notice = new LowBatteryNotice();
    Equal(true, notice.Update(Status(10), 20));
    Equal(false, notice.Update(Status(100, true), 20));
    Equal(false, notice.Update(new(Connection.Wired,
        new(10, true, BatterySource.Receiver), DateTimeOffset.Now), 20));
    Equal(true, notice.Update(Status(10), 20));
});
Test("stale readings cannot trigger or reset a low-battery reminder", () =>
{
    var notice = new LowBatteryNotice();
    foreach (var connection in new[] { Connection.Disconnected, Connection.Sleeping, Connection.Error })
        Equal(false, notice.Update(Status(10, connection: connection), 20));
    Equal(true, notice.Update(Status(10), 20));
    Equal(false, notice.Update(Status(80, connection: Connection.Error), 20));
    Equal(false, notice.Update(Status(100, true, Connection.Disconnected), 20));
    Equal(false, notice.Update(Status(10), 20));
});
Test("unverified charging events do not trigger any battery reminder", () =>
{
    var status = new MouseStatus(Connection.Wired,
        new(10, true, BatterySource.DeviceEvent), DateTimeOffset.Now);
    Equal(false, new LowBatteryNotice().Update(status, 20));
    Equal(false, new ChargeNotice().Update(status));
});
Test("changing threshold immediately evaluates the new low-battery condition", () =>
{
    var notice = new LowBatteryNotice();
    Equal(false, notice.Update(Status(30), 20));
    Equal(true, notice.Update(Status(30), 40));
    Equal(false, notice.Update(Status(30), 40));
    Equal(false, notice.Update(Status(30), 25));
    Equal(true, notice.Update(Status(24), 25));
});
Test("high threshold hysteresis can rearm at 100 percent", () =>
{
    var notice = new LowBatteryNotice();
    Equal(true, notice.Update(Status(99), 100));
    Equal(false, notice.Update(Status(100), 100));
    Equal(true, notice.Update(Status(99), 100));
});
Test("alert settings round trip, supply defaults and clamp malformed thresholds", () =>
{
    var original = new AlertSettings(false, 35, false);
    var json = System.Text.Json.JsonSerializer.Serialize(original);
    Equal(original, System.Text.Json.JsonSerializer.Deserialize<AlertSettings>(json));
    Equal(new AlertSettings(), System.Text.Json.JsonSerializer.Deserialize<AlertSettings>("{}"));
    Equal(1, new AlertSettings(LowBatteryThreshold: -3).Normalize().LowBatteryThreshold);
    Equal(100, new AlertSettings(LowBatteryThreshold: 150).Normalize().LowBatteryThreshold);
});
Test("captured wireless 97 to 98 event updates without an extra query", () =>
{
    var time = new DateTimeOffset(2026, 10, 12, 11, 57, 50, TimeSpan.FromHours(8));
    var device = new FakeDevice(0x1026, 97);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = _ => sample };
    Equal(97, monitor.Poll(false).Battery!.Level);
    time = time.AddSeconds(30);
    sample = new(Protocol.ParseBatteryEvent(Convert.FromHexString("09FA0562"))!.Value, time);
    var status = monitor.Poll(false);
    Equal(98, status.Battery!.Level);
    Equal(BatterySource.WirelessEvent, status.Battery.Source);
    Equal(true, status.Battery.HasReliableLevel);
    Equal(time, status.LevelUpdatedAt);
    Equal(true, status.Tooltip.Contains("上报收到于"));
    Equal(false, status.Tooltip.Contains("待核实"));
    time = time.AddSeconds(5); monitor.Poll(false);
    Equal(1, device.BatteryQueries);
    device.Level = 98; time = time.AddSeconds(25);
    Equal(98, monitor.Poll(false).Battery!.Level);
    Equal(2, device.BatteryQueries);
});
Test("an old wireless event cannot overwrite a newer forced query", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x1026, 97);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = _ => sample };
    monitor.Poll(false);
    time = time.AddSeconds(1); sample = new(98, time);
    Equal(98, monitor.Poll(false).Battery!.Level);
    device.Level = 99; time = time.AddSeconds(1);
    Equal(99, monitor.Poll(true).Battery!.Level);
    Equal(99, monitor.Poll(false).Battery!.Level);
});
Test("wireless ignores old future expired invalid and other-device events", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x1026, 97);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = id => id == device.Endpoint.Id ? sample : new(99, time) };
    monitor.Poll(false); time = time.AddSeconds(1);
    foreach (var bad in new[] { new BatteryEvent(98, time.AddSeconds(-2)), new(98, time.AddSeconds(-1)),
        new(98, time.AddSeconds(1)), new(98, time.AddSeconds(-91)), new(101, time) })
    {
        // Timestamp equal to the initial query is also stale.
        sample = bad;
        Equal(97, monitor.Poll(false).Battery!.Level);
    }
    var otherMonitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = id => id == "other-device" ? new(99, time) : null };
    Equal(97, otherMonitor.Poll(false).Battery!.Level);
});
Test("wireless event cannot revive offline or failed device status", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x1026, 97);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = _ => sample };
    monitor.Poll(false); time = time.AddSeconds(1); sample = new(98, time);
    monitor.Poll(false);
    device.Online = false; time = time.AddSeconds(1);
    Equal(Connection.Sleeping, monitor.Poll(true).Connection);
    sample = new(99, time); Equal(98, monitor.Poll(false).Battery!.Level);
    Equal(time.AddSeconds(-1), monitor.Status.LevelUpdatedAt);
    device.Fail = true;
    Equal(Connection.Error, monitor.Poll(true).Connection);
});
Test("events keep the latest wireless reading without clearing charging on other connections", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x1026, 97);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = _ => sample };
    monitor.Poll(false); time = time.AddSeconds(1); sample = new(98, time);
    Equal(98, monitor.Poll(false).Battery!.Level);
    sample = null;
    Equal(98, monitor.Poll(false).Battery!.Level);
    var wired = new FakeDevice(0x1029, 97);
    var wiredMonitor = new BatteryMonitor(() => new[] { wired.Endpoint }, _ => { }, () => time)
        { EventProvider = _ => new(98, time.AddSeconds(-1)) };
    Equal(97, wiredMonitor.Poll(false).Battery!.Level);
});
Test("wireless events update low-battery alerts without inventing a charge cycle", () =>
{
    var time = DateTimeOffset.Now;
    var device = new FakeDevice(0x1026, 20);
    BatteryEvent? sample = null;
    var monitor = new BatteryMonitor(() => new[] { device.Endpoint }, _ => { }, () => time)
        { EventProvider = _ => sample };
    var low = new LowBatteryNotice(); var full = new ChargeNotice();
    var initial = monitor.Poll(false);
    Equal(false, low.Update(initial, 20)); Equal(false, full.Update(initial));
    time = time.AddSeconds(1); sample = new(19, time);
    var updated = monitor.Poll(false);
    Equal(true, low.Update(updated, 20)); Equal(false, low.Update(updated, 20));
    time = time.AddSeconds(1); sample = new(100, time);
    Equal(false, full.Update(monitor.Poll(false)));
});
Console.WriteLine($"{passed} tests passed.");

sealed class FakeDevice(ushort product, byte level, bool charging = false)
{
    public byte Level = level;
    public bool Charging = charging;
    public bool Online = true, Fail;
    public ulong Identity = Protocol.S9ProIdentity;
    public int BatteryQueries, Disposals;
    public DeviceEndpoint Endpoint => new($"fake-{product:X4}", product, () => new Session(this));
    private sealed class Session(FakeDevice device) : IQueryConnection
    {
        public byte[] Query(Query query)
        {
            if (device.Fail) throw new IOException("simulated transport failure");
            if (query == S9Battery.Core.Query.Identity)
            {
                var bytes = new byte[8];
                for (int i = 0; i < 6; i++) bytes[2 + i] = (byte)(device.Identity >> ((5 - i) * 8));
                return bytes;
            }
            if (query == S9Battery.Core.Query.Online) return new[] { device.Online ? (byte)1 : (byte)0 };
            device.BatteryQueries++;
            return new byte[] { (byte)(device.Level | (device.Charging ? 128 : 0)), 0, 0, 0, 0, 0, 0 };
        }
        public void Dispose() => device.Disposals++;
    }
}

sealed class CapturedConnection : IQueryConnection
{
    public byte[] Query(Query query)
    {
        string prefix = query switch
        {
            S9Battery.Core.Query.Identity => "0350000B0000030109000000000E010100",
            S9Battery.Core.Query.Online => "0350000B00470109000000000E000000",
            _ => "0350000B00014E210701906504A55A0000"
        };
        return Protocol.Decode(query, Convert.FromHexString(prefix.PadRight(128, '0')));
    }
    public void Dispose() { }
}
