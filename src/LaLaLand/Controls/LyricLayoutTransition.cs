namespace LaLaLand.Controls;

/// <summary>A finite, time-based transition shared by scrolling and token reflow. No layout inputs depend on it.</summary>
internal sealed class LyricLayoutTransition
{
    internal const double DurationSeconds = 0.38;
    private double _elapsed = DurationSeconds;

    public bool IsMoving => _elapsed < DurationSeconds;
    public double Remaining
    {
        get
        {
            double t = Math.Clamp(_elapsed / DurationSeconds, 0, 1);
            return 1 - t * t * (3 - 2 * t); // smooth start and finish, without overshoot
        }
    }

    public void Begin() => _elapsed = 0;
    public void Snap() => _elapsed = DurationSeconds;
    public void Advance(double seconds)
    {
        if (double.IsFinite(seconds) && seconds > 0)
            _elapsed = Math.Min(DurationSeconds, _elapsed + seconds);
    }
}
