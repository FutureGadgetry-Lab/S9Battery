namespace S9Battery.Core;

public enum Connection { Disconnected, Wireless, Wired, Sleeping, Error }
public record MouseStatus(Connection Connection, BatteryReading? Battery = null,
    DateTimeOffset? UpdatedAt = null, string? Detail = null,
    DateTimeOffset? LevelUpdatedAt = null, int? EstimatedMinutes = null)
{
    public bool Fresh => Connection is Connection.Wired or Connection.Wireless;
    public string IconText => !Fresh && Connection != Connection.Sleeping ? "--" :
        Battery?.HasDisplayLevel == true ? Battery.Level.ToString() : Battery?.Charging == true ? "充" : "--";
    public string BatteryText => Battery is null ? "电量未知" :
        Battery.Source == BatterySource.DeviceEvent && Battery.HasDisplayLevel ? $"上报 {Battery.Level}%（待核实）" :
        !Battery.HasReliableLevel ? "充电时电量不可确认" :
        Battery.Source == BatterySource.Receiver ? $"{Battery.Level}%（接收器）" : $"{Battery.Level}%";
    public string Description => Connection switch
    {
        Connection.Disconnected => "设备未连接",
        Connection.Sleeping => "鼠标休眠或关闭 · 上次读数",
        Connection.Error => "读取失败 · 上次读数",
        _ when Battery?.Charging == true => "正在充电",
        _ when Battery is { Level: 100, HasReliableLevel: true } => "电量达到 100%",
        _ => "使用电池"
    };
    public string Tooltip => $"S9 Pro · {Description}\n" +
        $"{(Connection == Connection.Wired ? "有线" : Connection == Connection.Wireless ? "2.4G" : "状态待更新")} · " +
        $"{BatteryText}\n" +
        ((LevelUpdatedAt ?? UpdatedAt) is not { } at ? "尚未读取电量" :
            $"{(Battery?.Source is BatterySource.DeviceEvent or BatterySource.WirelessEvent ? "上报收到于" : "读数时间")} {at.LocalDateTime:HH:mm:ss}") +
        (Battery?.Charging == true ? Battery is { Level: 100, HasReliableLevel: true } ? "\n充电结束仍待确认" : EstimatedMinutes is { } minutes ?
            $"\n按近期速度约 {minutes} 分钟（后期可能变慢）" : "\n充电剩余时间：数据不足" : "");
}

public sealed class ChargeNotice
{
    private bool initialized, chargingObserved, reachedHundred;
    public bool Update(MouseStatus status)
    {
        if (!status.Fresh || status.Battery is not { } battery) return false;
        bool full = battery.HasReliableLevel && battery.Level == 100;
        if (!initialized)
        {
            initialized = true;
            reachedHundred = full;
            chargingObserved = battery.Charging && !full;
            return false; // The first reading is a baseline, never a completion event.
        }
        if (battery.HasReliableLevel && battery.Level < 100)
        {
            reachedHundred = false;
            if (!battery.Charging) chargingObserved = false;
        }
        // Wired E4 is useful evidence of charging, but cannot itself prove 100%.
        if (battery.Charging && !reachedHundred) chargingObserved = true;
        if (!full) return false;
        bool notify = chargingObserved && !reachedHundred;
        reachedHundred = true;
        chargingObserved = false;
        return notify;
    }
}
