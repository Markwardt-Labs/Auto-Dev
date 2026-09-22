using System.Runtime.CompilerServices;
using AutoDev.AiCli;
using AutoDev.ViewModels.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers the bug where sending new input while the active request sits Paused used to start an unrelated new request and leave the paused one stuck forever (CanSend doesn't gate on Status) - SendAsync must instead fold the new text into that same request and resume it, exactly like ResumeAsync does but with real content instead of its generic placeholder message.</summary>
public sealed class GenerateTabViewModelPauseResumeTests
{
    private readonly string workspacePath = "/workspace";
    private readonly Mock<IAiSessionClientFactory> sessionClientFactory = new();
    private readonly Mock<IAiProviderSelectionService> providerSelection = new();
    private readonly Mock<IWorkspaceMetadataStore> metadataStore = new();
    private readonly Mock<IUsageAggregatorService> usageAggregator = new();
    private readonly Mock<ISoundService> soundService = new();
    private readonly Mock<IUiDispatcher> dispatcher = new();
    private readonly List<Mock<IAiSessionClient>> createdClients = [];

    public GenerateTabViewModelPauseResumeTests()
    {
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(action => action());
        metadataStore.Setup(store => store.LoadGenerateDraftAsync(workspacePath, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        metadataStore.Setup(store => store.LoadGenerateSessionIdAsync(workspacePath, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        metadataStore.Setup(store => store.LoadGenerateRequestsAsync(workspacePath, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);

        sessionClientFactory
            .Setup(factory => factory.Create(It.IsAny<AiProvider>(), workspacePath, It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(() =>
            {
                Mock<IAiSessionClient> client = new();
                client.Setup(c => c.SessionId).Returns($"session-{createdClients.Count}");
                client.Setup(c => c.ReadAllEventsAsync(It.IsAny<CancellationToken>())).Returns(NeverEndingEventsAsync());
                client.Setup(c => c.SendUserMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
                client.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
                createdClients.Add(client);
                return client.Object;
            });
    }

    /// <summary>Never produces an event or completes - simulates a live, still-running provider process. The background read loop (GenerateTabViewModel.ReadLoopAsync) treats a stream that ends with no ResultEvent as an abandoned/crashed turn and force-finalizes it, which would otherwise race this test's own Pause/Send calls immediately after Send starts a turn.</summary>
    private static async IAsyncEnumerable<AiStreamEvent> NeverEndingEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    private GenerateTabViewModel CreateViewModel() => new(
        workspacePath,
        sessionClientFactory.Object,
        providerSelection.Object,
        metadataStore.Object,
        usageAggregator.Object,
        soundService.Object,
        dispatcher.Object,
        NullLogger<GenerateTabViewModel>.Instance);

    [Fact]
    public async Task SendAsync_WhilePaused_ResumesTheSameRequestInsteadOfStartingANewOne()
    {
        GenerateTabViewModel viewModel = CreateViewModel();
        await viewModel.SwitchSessionAsync("feature/test");

        viewModel.InputText = "First instruction";
        await viewModel.SendCommand.ExecuteAsync(null);
        Assert.Single(viewModel.Requests);

        await viewModel.PauseCommand.ExecuteAsync(null);
        Assert.True(viewModel.Requests[0].IsPaused);
        Assert.False(viewModel.IsSending);

        viewModel.InputText = "Second instruction";
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Requests);
        Assert.Equal("First instruction\nSecond instruction", viewModel.Requests[0].Input);
        Assert.False(viewModel.Requests[0].IsPaused);
        Assert.True(viewModel.IsSending);

        Assert.Equal(2, createdClients.Count);
        createdClients[1].Verify(c => c.SendUserMessageAsync("Second instruction", It.IsAny<CancellationToken>()), Times.Once);
    }
}
