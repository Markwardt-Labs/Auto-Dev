using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using AutoDev.Tests.Infrastructure;
using AutoDev.ViewModels.Content;
using AutoDev.Views.Content;
using AvaloniaEdit;
using Markdown.Avalonia;

namespace AutoDev.Tests.Views.Content;

/// <summary>
/// Covers the bug where a fenced code block whose language tag has no AvaloniaEdit highlighting definition
/// (e.g. ```yaml, ```toml, ```dockerfile - anything TextMateSharp's own grammar registry covers but this
/// classic XSHD-based highlighter doesn't) still renders as a CodePad TextEditor with SyntaxHighlighting left
/// null, whose own default Foreground (Black, picked for a light editor background) was unreadable against
/// this app's dark theme - not genuinely "no highlighting" (plain, readable text) the way it should be.
/// </summary>
public sealed class MarkdownUnknownLangCodeBlockTests
{
    [Theory]
    [InlineData("yaml")]
    [InlineData("toml")]
    [InlineData("dockerfile")]
    [InlineData("nonexistentlang")]
    public void UnrecognizedLanguage_RendersWithNoHighlightingAndReadableForeground(string lang) => TestAppBuilder.RunOnUiThread(async () =>
    {
        string dir = Directory.CreateTempSubdirectory("md-unk-lang-test-").FullName;
        try
        {
            string path = Path.Combine(dir, "test.md");
            File.WriteAllText(path, $"```{lang}\nsome_value: true\n```\n");

            Mock<IGitService> gitService = new();
            FileTreeService fileTreeService = new(gitService.Object);
            Mock<IExternalOpenService> externalOpenService = new();
            EditTabViewModel viewModel = new(fileTreeService, externalOpenService.Object);
            await viewModel.LoadFileAsync(path);

            EditTabView view = new() { DataContext = viewModel };
            Window window = new() { Content = view, Width = 600, Height = 500 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            MarkdownScrollViewer preview = view.FindControl<MarkdownScrollViewer>("MarkdownPreview")!;
            TextEditor editor = Assert.Single(preview.GetLogicalDescendants().OfType<TextEditor>());

            Assert.Null(editor.SyntaxHighlighting);
            Assert.Equal(Brushes.White, editor.Foreground);
            Assert.Equal(Brushes.White, editor.TextArea.Foreground);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });
}
