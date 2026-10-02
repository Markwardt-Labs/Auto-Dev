using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using AutoDev.AiCli;
using AutoDev.Tests.Infrastructure;
using AutoDev.ViewModels.Infrastructure;
using AutoDev.Views.Content;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.Views.Content;

/// <summary>Covers the Generate tab's "Request input" box (the previously-submitted input shown above the status/activity box) being capped at 6 lines and scrolling internally rather than growing with a long submitted input.</summary>
public sealed class GenerateTabViewInputDisplayTests
{
    [Fact]
    public void RequestInput_ManyLines_IsHeightCappedAndScrolls() => TestAppBuilder.RunOnUiThread(async () =>
    {
        Mock<IUiDispatcher> dispatcher = new();
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(action => action());
        Mock<IWorkspaceMetadataStore> metadataStore = new();
        metadataStore.Setup(m => m.LoadGenerateDraftAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        metadataStore.Setup(m => m.LoadGenerateSessionIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        metadataStore.Setup(m => m.LoadGenerateRequestsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        GenerateTabViewModel viewModel = new(
            "/workspace",
            new Mock<IAiSessionClientFactory>().Object,
            new Mock<IAiProviderSelectionService>().Object,
            metadataStore.Object,
            new Mock<IUsageAggregatorService>().Object,
            new Mock<ISoundService>().Object,
            dispatcher.Object,
            NullLogger<GenerateTabViewModel>.Instance);
        await viewModel.SwitchSessionAsync("main");
        viewModel.Requests.Add(new GenerateRequestViewModel
        {
            Id = "1",
            Input = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")),
            Status = GenerateRequestStatus.Working,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        viewModel.DisplayedIndex = 0;

        GenerateTabView view = new() { DataContext = viewModel };
        Window window = new() { Content = view, Width = 700, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroller = view.GetLogicalDescendants().OfType<ScrollViewer>()
            .First(sv => sv.Content is SelectableTextBlock text && text.Text!.StartsWith("line 1\n"));
        Assert.True(scroller.Bounds.Height <= 102.5, $"request input height was {scroller.Bounds.Height}");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
    });
}
