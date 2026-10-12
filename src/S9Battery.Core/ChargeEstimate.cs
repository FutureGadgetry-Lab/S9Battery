namespace S9Battery.Core;

// Only fresh, trusted percentages are used; events awaiting validation never predict time.
public sealed class ChargeEstimate
{
    private readonly List<(DateTimeOffset At, int Level)> points = [];
    public int? Update(MouseStatus status)
    {
        if (!status.Fresh || status.Battery is not { Charging: true, HasReliableLevel: true } battery ||
            (status.LevelUpdatedAt ?? status.UpdatedAt) is not { } at)
        { points.Clear(); return null; }
        if (battery.Level >= 100) { points.Clear(); return null; }
        if (points.Count > 0)
        {
            if (at < points[^1].At || at - points[^1].At > TimeSpan.FromMinutes(3) || battery.Level < points[^1].Level)
                points.Clear();
            else if (at == points[^1].At) return Calculate();
        }
        points.Add((at, battery.Level));
        points.RemoveAll(p => at - p.At > TimeSpan.FromMinutes(30));
        return Calculate();
    }
    private int? Calculate()
    {
        if (points.Count < 3 || points.Select(p => p.Level).Distinct().Count() < 3) return null;
        var first = points[0]; var last = points[^1];
        double elapsed = (last.At - first.At).TotalMinutes;
        int gained = last.Level - first.Level;
        if (elapsed < 3 || gained < 3) return null;
        double minutes = (100 - last.Level) * elapsed / gained;
        return minutes is > 0 and <= 720 ? (int)Math.Ceiling(minutes) : null;
    }
}
