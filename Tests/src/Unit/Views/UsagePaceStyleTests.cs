using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using AutoDev.Tests.Infrastructure;

namespace AutoDev.Tests.Views;

/// <summary>Covers ControlStyles.axaml's TextBlock.usageText[Tag=...] selectors, which color the header's session/week text by the UsagePace enum value bound to Tag.</summary>
public sealed class UsagePaceStyleTests
{
    [Theory]
    [InlineData(UsagePace.VeryLow, "UsagePaceVeryLowBrush")]
    [InlineData(UsagePace.Low, "UsagePaceLowBrush")]
    [InlineData(UsagePace.Even, "TextMutedBrush")]
    [InlineData(UsagePace.High, "UsagePaceHighBrush")]
    [InlineData(UsagePace.VeryHigh, "UsagePaceVeryHighBrush")]
    public void UsageText_ForegroundFollowsPaceTag(UsagePace pace, string expectedBrushKey) => TestAppBuilder.RunOnUiThread(() =>
    {
        Window window = new();
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://AutoDev/")) { Source = new Uri("avares://AutoDev/src/Styles/VsCodeColors.axaml") });
        window.Styles.Add(new StyleInclude(new Uri("avares://AutoDev/")) { Source = new Uri("avares://AutoDev/src/Styles/ControlStyles.axaml") });
        TextBlock text = new() { Text = "Session 40%", Tag = pace };
        text.Classes.Add("usageText");
        window.Content = text;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        window.TryFindResource(expectedBrushKey, out object? expected);
        Assert.Equal(((ISolidColorBrush)expected!).Color, ((ISolidColorBrush)text.Foreground!).Color);
    });
}
