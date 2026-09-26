using System.ComponentModel;
using System.Runtime.CompilerServices;
using AutoDev.AiCli;
using AutoDev.ViewModels.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers GenerateTabViewModel's handling of an AI process that's gone (exited, never launched) - a send must end the turn cleanly and recover on the next send, never throw out of the command (which crashed the app), and closing mid-turn must keep the session resumable.</summary>
public sealed class GenerateTabViewModelDeadClientTests
{
    private readonly string workspacePath = "/workspace";
    private readonly Mock<IAiSessionClientFactory> sessionClientFactory = new();
    private readonly Mock<IAiProviderSelectionService> providerSelection = new();
    private readonly Mock<IWorkspaceMetadataStore> metadataStore = new();
    private readonly Mock<IUsageAggregatorService> usageAggregator = new();
    private readonly Mock<ISoundService> soundService = new();
    private readonly Mock<IUiDispatcher> dispatcher = new();
    private readonly List<Mock<IAiSessionClient>> createdClients = [];
    private readonly Queue<Action<Mock<IAiSessionClient>>> clientSetups = new();

    public GenerateTabViewModelDeadClientTests()
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
                if (clientSetups.TryDequeue(out Action<Mock<IAiSessionClient>>? setup))
                {
                    setup(client);
                }

                createdClients.Add(client);
                return client.Object;
            });
    }

    /// <summary>Never produces an event or completes - a live process with nothing to say yet, so the read loop never finalizes the turn on its own.</summary>
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
    public async Task SendAsync_ProcessAlreadyExited_EndsTheTurnAndTheNextSendStartsAFreshProcess()
    {
        clientSetups.Enqueue(client => client
            .Setup(c => c.SendUserMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Broken pipe")));
        GenerateTabViewModel viewModel = CreateViewModel();
        await viewModel.SwitchSessionAsync("main");

        viewModel.InputText = "First";
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSending);
        Assert.Equal(GenerateRequestStatus.Cancelled, viewModel.Requests[0].Status);

        viewModel.InputText = "Second";
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, createdClients.Count);
        Assert.True(viewModel.IsSending);
        createdClients[1].Verify(c => c.SendUserMessageAsync("Second", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ProcessFailsToLaunch_EndsTheTurnWithoutThrowing()
    {
        clientSetups.Enqueue(client => client.Setup(c => c.Start(It.IsAny<string?>())).Throws(new Win32Exception("No such file or directory")));
        GenerateTabViewModel viewModel = CreateViewModel();
        await viewModel.SwitchSessionAsync("main");

        viewModel.InputText = "Hello";
        await viewModel.SendCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsSending);
        Assert.Equal(GenerateRequestStatus.Cancelled, viewModel.Requests[0].Status);
    }

    [Fact]
    public async Task DisposeAsync_MidTurn_SavesTheSessionIdSoThePausedRequestResumesTheSameConversation()
    {
        GenerateTabViewModel viewModel = CreateViewModel();
        await viewModel.SwitchSessionAsync("main");

        viewModel.InputText = "Long task";
        await viewModel.SendCommand.ExecuteAsync(null);
        await viewModel.DisposeAsync();

        Assert.Equal(GenerateRequestStatus.Paused, viewModel.Requests[0].Status);
        metadataStore.Verify(store => store.SaveGenerateSessionIdAsync(workspacePath, "main", It.Is<string>(id => id.EndsWith(":session-0")), It.IsAny<CancellationToken>()), Times.Once);
    }
}
