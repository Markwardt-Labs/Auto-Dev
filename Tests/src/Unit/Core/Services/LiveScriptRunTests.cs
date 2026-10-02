using System.ComponentModel;

namespace AutoDev.Tests.Core.Services;

/// <summary>Covers LiveScriptRun's throttled PropertyChanged notification - the text itself is always appended immediately, but notifications coalesce rapid-fire chunks so a UI bound to OutputText isn't forced to fully re-lay-out on every single one.</summary>
public sealed class LiveScriptRunTests
{
    [Fact]
    public void AppendText_AlwaysAppendsImmediately_RegardlessOfNotificationThrottling()
    {
        LiveScriptRun liveRun = new();

        liveRun.AppendText("one");
        liveRun.AppendText("two");

        Assert.Equal("onetwo", liveRun.OutputText);
    }

    [Fact]
    public void AppendText_RapidChunks_CoalescesIntoFarFewerNotificationsThanAppends()
    {
        LiveScriptRun liveRun = new();
        int notifications = 0;
        liveRun.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveScriptRun.OutputText))
            {
                notifications++;
            }
        };

        for (int i = 0; i < 200; i++)
        {
            liveRun.AppendText($"line {i}\n");
        }

        Assert.True(notifications < 200, $"expected far fewer than 200 notifications for 200 rapid-fire appends, got {notifications}");
        Assert.Equal(200, liveRun.OutputText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task AppendText_ThenQuiet_StillEventuallyFlushesTheTrailingText()
    {
        LiveScriptRun liveRun = new();
        liveRun.AppendText("first"); // consumes the initial immediate-notify slot

        string? lastNotifiedText = null;
        liveRun.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveScriptRun.OutputText))
            {
                lastNotifiedText = liveRun.OutputText;
            }
        };
        liveRun.AppendText("second"); // arrives within the throttle window - not notified yet

        await Task.Delay(TimeSpan.FromMilliseconds(300));

        Assert.Equal("firstsecond", lastNotifiedText);
    }

    [Fact]
    public void MarkFinished_FlushesImmediatelyEvenMidThrottleWindow()
    {
        LiveScriptRun liveRun = new();
        liveRun.AppendText("first");

        string? lastNotifiedText = null;
        liveRun.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LiveScriptRun.OutputText))
            {
                lastNotifiedText = liveRun.OutputText;
            }
        };
        liveRun.AppendText("second");
        Assert.Null(lastNotifiedText); // still inside the throttle window - no notification yet

        liveRun.MarkFinished();

        Assert.Equal("firstsecond", lastNotifiedText);
        Assert.False(liveRun.IsRunning);
    }
}
