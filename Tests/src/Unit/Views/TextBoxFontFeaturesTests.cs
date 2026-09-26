using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using AutoDev.Tests.Infrastructure;

namespace AutoDev.Tests.Views;

/// <summary>Covers ControlStyles.axaml's TextBox style disabling the Inter font's contextual alternates, which otherwise draw "->" and "=>" typed into an input as single arrow glyphs.</summary>
public sealed class TextBoxFontFeaturesTests
{
    [Fact]
    public void TextBox_DisablesContextualAlternates() => TestAppBuilder.RunOnUiThread(() =>
    {
        Window window = new();
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://AutoDev/")) { Source = new Uri("avares://AutoDev/src/Styles/VsCodeColors.axaml") });
        window.Styles.Add(new StyleInclude(new Uri("avares://AutoDev/")) { Source = new Uri("avares://AutoDev/src/Styles/ControlStyles.axaml") });
        TextBox box = new() { Text = "a => b -> c" };
        window.Content = box;
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(box.FontFeatures);
        Assert.Contains(box.FontFeatures!, feature => feature.Tag == "calt" && feature.Value == 0);
        Assert.Equal("a => b -> c", box.Text);
    });
}
