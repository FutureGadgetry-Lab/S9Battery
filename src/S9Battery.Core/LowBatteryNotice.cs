namespace S9Battery.Core;

public sealed class LowBatteryNotice
{
    private bool reminded;
    private int lastThreshold;

    public bool Update(MouseStatus status, int threshold)
    {
        threshold = Math.Clamp(threshold, 1, 100);
        if (threshold != lastThreshold) { reminded = false; lastThreshold = threshold; }
        if (!status.Fresh || status.Battery is not { } battery) return false;
        // Connecting to charge ends the low-battery episode, even if its percentage is unknown.
        if (battery.Charging) { reminded = false; return false; }
        if (!battery.HasReliableLevel) return false;
        // A little hysteresis avoids repeating notifications when the reading fluctuates.
        if (battery.Level >= Math.Min(100, threshold + 3)) reminded = false;
        if (battery.Level >= threshold || reminded) return false;
        reminded = true;
        return true;
    }
}

public sealed record AlertSettings(bool LowBatteryEnabled = true, int LowBatteryThreshold = 20,
    bool FullBatteryEnabled = true)
{
    public AlertSettings Normalize() => this with { LowBatteryThreshold = Math.Clamp(LowBatteryThreshold, 1, 100) };
}
