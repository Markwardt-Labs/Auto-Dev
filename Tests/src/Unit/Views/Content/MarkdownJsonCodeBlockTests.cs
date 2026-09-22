using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AutoDev.Tests.Infrastructure;
using AutoDev.ViewModels.Content;
using AutoDev.Views.Content;
using AvaloniaEdit;
using Markdown.Avalonia;

namespace AutoDev.Tests.Views.Content;

/// <summary>
/// Covers the bug where a fenced code block in a language other than C# (e.g. ```json) rendered with wrong
/// colors - MarkdownCodeHighlightTheme.Apply is C#-specific (its TypeNamePattern/IdentifierPattern rules match
/// almost any bare word) but used to run unconditionally on every embedded code-block TextEditor regardless of
/// which language's own highlighting definition it was actually handed, injecting those C#-only rules into
/// (for example) JSON's own definition and miscoloring bare tokens like true/false/null.
/// </summary>
public sealed class MarkdownJsonCodeBlockTests
{
    [Fact]
    public void JsonCodeBlock_KeepsItsOwnHighlightingUnpolluted() => TestAppBuilder.RunOnUiThread(async () =>
    {
        string dir = Directory.CreateTempSubdirectory("md-json-test-").FullName;
        try
        {
            string path = Path.Combine(dir, "test.md");
            File.WriteAllText(path, "```json\n{\n  \"key\": true\n}\n```\n");

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
            TextEditor jsonEditor = Assert.Single(preview.GetLogicalDescendants().OfType<TextEditor>());

            Assert.Equal("Re:Json", jsonEditor.SyntaxHighlighting!.Name);
            Assert.DoesNotContain(
                jsonEditor.SyntaxHighlighting.MainRuleSet.Rules,
                rule => rule.Regex is not null && rule.Regex.ToString().Contains("A-Za-z0-9_"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });
}
