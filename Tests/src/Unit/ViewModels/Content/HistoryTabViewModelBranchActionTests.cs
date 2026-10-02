using AutoDev.AiCli;
using CommunityToolkit.Mvvm.Input;
using AutoDev.ViewModels.Infrastructure;
using AutoDev.ViewModels.Sidebar;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers the History tab's branch context-menu Squash/Rebase/Merge actions - which branch each direction operates on and relative to what, the squash-message prompt, and the order of the steps Merge chains together.</summary>
public sealed class HistoryTabViewModelBranchActionTests
{
    private readonly string workspacePath = "/workspace";
    private readonly Mock<IWorkspaceVersioningService> versioning = new();
    private readonly Mock<IDialogService> dialogs = new();
    private readonly List<string> calls = [];
    private readonly HistoryTabViewModel viewModel;
    private readonly VersionSectionViewModel version;

    public HistoryTabViewModelBranchActionTests()
    {
        Mock<IUiDispatcher> dispatcher = new();
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(action => action());

        versioning.Setup(v => v.HasUserIdentityConfiguredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        versioning.Setup(v => v.CaptureSnapshotAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new GitActionSnapshot("feature", "abc", false, new Dictionary<string, string>()));
        versioning.Setup(v => v.GetCurrentTargetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new GitTarget(GitTargetKind.Branch, "feature", null, "abc", "msg"));
        versioning.Setup(v => v.GetDefaultSquashMessageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string branch, CancellationToken _) => $"last message of {branch}");
        versioning.Setup(v => v.ListAllBranchesAsync(It.IsAny<CancellationToken>())).ReturnsAsync([new BranchSummary("feature", true), new BranchSummary("main", false)]);
        versioning.Setup(v => v.SquashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SquashOutcome.Succeeded);
        versioning.Setup(v => v.RebaseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string?, CancellationToken>((branch, onto, _, _) => calls.Add($"rebase {branch} onto {onto}"))
            .ReturnsAsync(GitOperationOutcome.Succeeded);
        versioning.Setup(v => v.FastForwardAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, CancellationToken>((target, source, _) => calls.Add($"fast-forward {target} to {source}"))
            .ReturnsAsync(true);
        versioning.Setup(v => v.PushBranchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Callback<string, bool, CancellationToken>((branch, force, _) => calls.Add($"push {branch}{(force ? " (force)" : "")}"))
            .ReturnsAsync(true);
        versioning.Setup(v => v.DeleteBranchEverywhereAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((branch, _) => calls.Add($"delete {branch}"))
            .ReturnsAsync(true);
        dialogs.Setup(d => d.ShowInputDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string _, string _, string initial, bool _) => initial);

        Mock<IWorkspaceMetadataStore> metadataStore = new();
        GenerateTabViewModel generate = new(
            workspacePath,
            new Mock<IAiSessionClientFactory>().Object,
            new Mock<IAiProviderSelectionService>().Object,
            metadataStore.Object,
            new Mock<IUsageAggregatorService>().Object,
            new Mock<ISoundService>().Object,
            dispatcher.Object,
            NullLogger<GenerateTabViewModel>.Instance);
        version = new(versioning.Object, dialogs.Object, new Mock<ITemplateService>().Object, generate, dispatcher.Object);
        EditTabViewModel edit = new(new Mock<IFileTreeService>().Object, new Mock<IExternalOpenService>().Object);
        viewModel = new HistoryTabViewModel(versioning.Object, version, dialogs.Object, edit);
    }

    /// <summary>Runs a command to completion, dismissing the failure overlay (which RunBusyAsync otherwise waits on forever) if the action ends up reporting one - bounded so a mistake here fails the test instead of hanging the run.</summary>
    private async Task RunAsync(IAsyncRelayCommand command, string selectedBranch)
    {
        Task run = command.ExecuteAsync(selectedBranch);
        while (!run.IsCompleted)
        {
            if (version.IsBusyFailed)
            {
                version.ConfirmBusyCommand.Execute(null);
            }

            await Task.Delay(5);
        }

        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private void SetUniqueCommits(string branch, string baseBranch, int count, bool basedOn = true)
    {
        versioning.Setup(v => v.CountUniqueCommitsAsync(branch, baseBranch, It.IsAny<CancellationToken>())).ReturnsAsync(count);
        versioning.Setup(v => v.IsBasedOnAsync(branch, baseBranch, It.IsAny<CancellationToken>())).ReturnsAsync(basedOn);
    }

    [Fact]
    public async Task SquashCurrent_SquashesTheCurrentBranchSinceTheClickedOneWithTheDefaultMessage()
    {
        SetUniqueCommits("feature", "main", 3);

        await RunAsync(viewModel.SquashCurrentCommand, "main");

        versioning.Verify(v => v.SquashAsync("feature", "main", "last message of feature", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SquashSelected_SquashesTheClickedBranchSinceTheCurrentOne()
    {
        SetUniqueCommits("main", "feature", 2);

        await RunAsync(viewModel.SquashSelectedCommand, "main");

        versioning.Verify(v => v.SquashAsync("main", "feature", "last message of main", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Squash_NothingToSquash_ExplainsAndDoesNotPromptOrSquash()
    {
        SetUniqueCommits("feature", "main", 0);

        await RunAsync(viewModel.SquashCurrentCommand, "main");

        dialogs.Verify(d => d.ShowMessageDialogAsync("Squash", It.IsAny<string>()), Times.Once);
        dialogs.Verify(d => d.ShowInputDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
        versioning.Verify(v => v.SquashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Squash_PromptCancelled_DoesNothing()
    {
        SetUniqueCommits("feature", "main", 2);
        dialogs.Setup(d => d.ShowInputDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>())).ReturnsAsync((string?)null);

        await RunAsync(viewModel.SquashCurrentCommand, "main");

        versioning.Verify(v => v.SquashAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RebaseSelected_RebasesTheClickedBranchOntoTheCurrentOneAndForcePushesIt()
    {
        SetUniqueCommits("main", "feature", 1, basedOn: false);

        await RunAsync(viewModel.RebaseSelectedCommand, "main");

        Assert.Equal(["rebase main onto feature", "push main (force)"], calls);
        dialogs.Verify(d => d.ShowInputDialogAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RebaseCurrent_SeveralCommits_PromptsForASquashMessageAndPassesItOn()
    {
        SetUniqueCommits("feature", "main", 3, basedOn: false);

        await RunAsync(viewModel.RebaseCurrentCommand, "main");

        versioning.Verify(v => v.RebaseAsync("feature", "main", "last message of feature", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("push feature (force)", calls);
    }

    [Fact]
    public async Task Rebase_PendingChanges_IsRefusedBeforeAnythingHappens()
    {
        SetUniqueCommits("feature", "main", 1, basedOn: false);
        versioning.Setup(v => v.HasUncommittedChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await RunAsync(viewModel.RebaseCurrentCommand, "main");

        dialogs.Verify(d => d.ShowMessageDialogAsync("Rebase", It.IsAny<string>()), Times.Once);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task MergeSelected_RebasesTheClickedBranchFastForwardsTheCurrentOneToItPushesThatAndDeletesTheClickedBranch()
    {
        SetUniqueCommits("main", "feature", 1, basedOn: false);

        await RunAsync(viewModel.MergeSelectedCommand, "main");

        Assert.Equal(["rebase main onto feature", "fast-forward feature to main", "push feature", "delete main"], calls);
    }

    [Fact]
    public async Task MergeCurrent_AlreadyASingleCommitOnTheTarget_SkipsTheRebase()
    {
        SetUniqueCommits("feature", "main", 1, basedOn: true);

        await RunAsync(viewModel.MergeCurrentCommand, "main");

        Assert.Equal(["fast-forward main to feature", "push main", "delete feature"], calls);
    }

    [Fact]
    public async Task Merge_SeveralCommits_SquashesEvenWhenAlreadyBasedOnTheTarget()
    {
        SetUniqueCommits("feature", "main", 4, basedOn: true);

        await RunAsync(viewModel.MergeCurrentCommand, "main");

        versioning.Verify(v => v.RebaseAsync("feature", "main", "last message of feature", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(["rebase feature onto main", "fast-forward main to feature", "push main", "delete feature"], calls);
    }

    [Fact]
    public async Task Merge_FastForwardFails_PushesAndDeletesNothing()
    {
        SetUniqueCommits("feature", "main", 1, basedOn: true);
        versioning.Setup(v => v.FastForwardAsync("main", "feature", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        await RunAsync(viewModel.MergeCurrentCommand, "main");

        versioning.Verify(v => v.PushBranchAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        versioning.Verify(v => v.DeleteBranchEverywhereAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void BranchRow_HeadersNameBothBranchesSoEachDirectionIsUnambiguous()
    {
        BranchRowViewModel row = new(new BranchSummary("main", false), "feature_x");

        Assert.Equal("Squash 'feature__x' (current) since 'main'", row.SquashCurrentHeader);
        Assert.Equal("Squash 'main' since 'feature__x' (current)", row.SquashSelectedHeader);
        Assert.Equal("Rebase 'feature__x' (current) onto 'main'", row.RebaseCurrentHeader);
        Assert.Equal("Rebase 'main' onto 'feature__x' (current)", row.RebaseSelectedHeader);
        Assert.Equal("Merge 'feature__x' (current) into 'main'", row.MergeCurrentHeader);
        Assert.Equal("Merge 'main' into 'feature__x' (current)", row.MergeSelectedHeader);
        Assert.True(row.HasCurrentBranch);
        Assert.False(new BranchRowViewModel(new BranchSummary("main", false), null).HasCurrentBranch);
    }
}
