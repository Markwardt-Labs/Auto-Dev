namespace AutoDev.AiCli.Models;

/// <summary>One usage window (session or weekly) as reported by the CLI's own `/usage` command.</summary>
public sealed record UsagePeriodStatus(int PercentUsed, string ResetsAtDisplay, string ResetsAtFull, DateTimeOffset? ResetsAtUtc)
{
    /// <summary>
    /// Compares percent used against percent of the window elapsed (an even pace is the two matching, e.g. 50%
    /// used 2.5h into a 5h window): 5 or more points apart is Low/High, 10 or more is VeryLow/VeryHigh (mid-window: 45%/55% and 40%/60%).
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
        return (PercentUsed - elapsedPercent) switch
        {
            <= -10 => UsagePace.VeryLow,
            <= -5 => UsagePace.Low,
            < 5 => UsagePace.Even,
            < 10 => UsagePace.High,
            _ => UsagePace.VeryHigh,
        };
    }
}

public sealed record UsageLimitStatus(UsagePeriodStatus? Session, UsagePeriodStatus? Week);
