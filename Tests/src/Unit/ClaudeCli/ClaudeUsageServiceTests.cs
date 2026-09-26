using AutoDev.ClaudeCli;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.ClaudeCli;

/// <summary>Covers ClaudeUsageService.TryParseResetsAtUtc - the CLI's reset text has no year, which must resolve to the next occurrence rather than one already long past.</summary>
public sealed class ClaudeUsageServiceTests
{
    private readonly ClaudeUsageService service = new(NullLogger<ClaudeUsageService>.Instance);

    [Fact]
    public void TryParseResetsAtUtc_SameYear_ResolvesInTheCurrentYear()
    {
        DateTimeOffset? resetsAt = service.TryParseResetsAtUtc("Jul 19, 8:50am", "UTC", new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTimeOffset(2026, 7, 19, 8, 50, 0, TimeSpan.Zero), resetsAt);
    }

    [Fact]
    public void TryParseResetsAtUtc_ResetAcrossNewYear_ResolvesInTheNextYear()
    {
        DateTimeOffset? resetsAt = service.TryParseResetsAtUtc("Jan 3, 2am", "UTC", new DateTime(2026, 12, 30, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTimeOffset(2027, 1, 3, 2, 0, 0, TimeSpan.Zero), resetsAt);
    }

    [Fact]
    public void TryParseResetsAtUtc_NoTimezone_ReturnsNull() =>
        Assert.Null(service.TryParseResetsAtUtc("Jul 19, 8:50am", null, new DateTime(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc)));
}
