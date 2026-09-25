namespace AutoDev.AiCli.Models;

/// <summary>How fast a usage limit is being consumed relative to how much of its reset window has elapsed - see <see cref="UsagePeriodStatus.GetPace"/>.</summary>
public enum UsagePace
{
    /// <summary>Well under an even pace - usage is far behind the time elapsed.</summary>
    VeryLow,

    /// <summary>Somewhat under an even pace.</summary>
    Low,

    /// <summary>Roughly matching the time elapsed (also the fallback when the reset time is unknown).</summary>
    Even,

    /// <summary>Somewhat over an even pace.</summary>
    High,

    /// <summary>Well over an even pace, or the limit is already exhausted.</summary>
    VeryHigh,
}
