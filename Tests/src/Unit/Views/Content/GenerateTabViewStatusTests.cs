using Avalonia.Controls;
using Avalonia.Threading;
using AutoDev.AiCli;
using AutoDev.Tests.Infrastructure;
using AutoDev.ViewModels.Infrastructure;
using AutoDev.Views.Content;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.Views.Content;

/// <summary>Covers the Generate tab's status text being capped at a few lines (scrolling the overflow) rather than growing with a long current action.</summary>
public sealed class GenerateTabViewStatusTests
{
    [Fact]
    public void StatusText_LongAction_IsHeightCappedAndScrolls() => TestAppBuilder.RunOnUiThread(async () =>
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
            Input = "input",
            Status = GenerateRequestStatus.Working,
            CreatedAt = DateTimeOffset.UtcNow,
            CurrentAction = string.Join(' ', Enumerable.Repeat("Running: a very long shell command with many arguments", 20)),
        });
        viewModel.DisplayedIndex = 0;

        GenerateTabView view = new() { DataContext = viewModel };
        Window window = new() { Content = view, Width = 700, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        ScrollViewer scroller = view.FindControl<ScrollViewer>("StatusScroller")!;
        Assert.True(scroller.Bounds.Height <= 84.5, $"status height was {scroller.Bounds.Height}");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height);
    });
}
