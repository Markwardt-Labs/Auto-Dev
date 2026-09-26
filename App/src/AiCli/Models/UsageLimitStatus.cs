namespace AutoDev.AiCli.Models;

/// <summary>One usage window (session or weekly) as reported by the CLI's own `/usage` command.</summary>
public sealed record UsagePeriodStatus(int PercentUsed, string ResetsAtDisplay, string ResetsAtFull, DateTimeOffset? ResetsAtUtc)
{
    /// <summary>
    /// Compares percent used against percent of the window elapsed (an even pace is the two matching, e.g. 50%
    /// used 2.5h into a 5h window), with no minimum usage - 0% used well into the window is already a low pace.
    /// Within 5 points is Even, 5-15 points under is Low and beyond that VeryLow, 5-15 over is High and beyond
    /// that VeryHigh (mid-window: 45-55% even, 35-45% low, under 35% very low, 55-65% high, over 65% very high).
    /// An exhausted limit is always VeryHigh; an unknown or already-passed reset time can't be judged and is Even.
    /// </summary>
    /// <param name="window">Total length of this limit's reset window (5 hours for a session, 7 days for a week).</param>
    /// <param name="now">The current time.</param>
    /// <returns>The pace classification.</returns>
    public UsagePace GetPace(TimeSpan window, DateTimeOffset now)
    {
        if (PercentUsed >= 100)
        {
            return UsagePace.VeryHigh;
        }

        if (ResetsAtUtc is not { } resetsAt || resetsAt <= now)
        {
            return UsagePace.Even;
        }

        double elapsedPercent = Math.Clamp((window - (resetsAt - now)) / window * 100, 0, 100);
        return Math.Round(PercentUsed - elapsedPercent, 6) switch
        {
            < -15 => UsagePace.VeryLow,
            < -5 => UsagePace.Low,
            <= 5 => UsagePace.Even,
            <= 15 => UsagePace.High,
            _ => UsagePace.VeryHigh,
        };
    }
}

public sealed record UsageLimitStatus(UsagePeriodStatus? Session, UsagePeriodStatus? Week);
