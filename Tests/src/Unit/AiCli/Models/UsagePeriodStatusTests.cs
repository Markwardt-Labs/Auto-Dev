namespace AutoDev.Tests.AiCli.Models;

/// <summary>Covers UsagePeriodStatus.GetPace - usage compared against how much of the reset window has elapsed.</summary>
public sealed class UsagePeriodStatusTests
{
    private static readonly DateTimeOffset now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan window = TimeSpan.FromHours(5);

    [Theory]
    [InlineData(50, 2.5, UsagePace.Even)]
    [InlineData(45, 2.5, UsagePace.Even)]
    [InlineData(55, 2.5, UsagePace.Even)]
    [InlineData(44, 2.5, UsagePace.Low)]
    [InlineData(35, 2.5, UsagePace.Low)]
    [InlineData(34, 2.5, UsagePace.VeryLow)]
    [InlineData(0, 2.5, UsagePace.VeryLow)]
    [InlineData(56, 2.5, UsagePace.High)]
    [InlineData(65, 2.5, UsagePace.High)]
    [InlineData(66, 2.5, UsagePace.VeryHigh)]
    [InlineData(95, 2.5, UsagePace.VeryHigh)]
    [InlineData(0, 4.9, UsagePace.Even)]
    [InlineData(33, 3.0, UsagePace.Low)]
    [InlineData(80, 1.0, UsagePace.Even)]
    public void GetPace_ClassifiesUsageAgainstElapsedTime(int percentUsed, double hoursRemaining, UsagePace expected)
    {
        UsagePeriodStatus status = new(percentUsed, "", "", now + TimeSpan.FromHours(hoursRemaining));

        Assert.Equal(expected, status.GetPace(window, now));
    }

    [Fact]
    public void GetPace_ExhaustedLimit_IsVeryHighRegardlessOfTime() =>
        Assert.Equal(UsagePace.VeryHigh, new UsagePeriodStatus(100, "", "", now + TimeSpan.FromHours(0.1)).GetPace(window, now));

    [Fact]
    public void GetPace_UnknownResetTime_IsEven() =>
        Assert.Equal(UsagePace.Even, new UsagePeriodStatus(80, "", "", null).GetPace(window, now));

    [Fact]
    public void GetPace_ResetAlreadyPassed_IsEven() =>
        Assert.Equal(UsagePace.Even, new UsagePeriodStatus(10, "", "", now - TimeSpan.FromMinutes(1)).GetPace(window, now));
}
