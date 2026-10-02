namespace AutoDev.Tests.Core.Services;

/// <summary>Covers WorkspaceVersioningService.ListAllBranchesAsync's handling of branches that exist only as remote-tracking refs, the empty-workspace template offer, remote sync's protection of unpushed work, and the fast-forward merge's checkout precondition.</summary>
public sealed class WorkspaceVersioningServiceTests
{
    [Fact]
    public async Task ListAllBranchesAsync_RemoteOnlyBranch_MaterializesLocalBranchAndListsIt()
    {
        Mock<IGitService> git = new();
        git.Setup(g => g.GetCurrentBranchAsync("ws", It.IsAny<CancellationToken>())).ReturnsAsync("main");
        git.Setup(g => g.ListBranchesAsync("ws", "", It.IsAny<CancellationToken>())).ReturnsAsync(["main", "codex/feature"]);
        git.Setup(g => g.BranchExistsAsync("ws", "main", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        git.Setup(g => g.BranchExistsAsync("ws", "codex/feature", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        WorkspaceVersioningService service = new("ws", git.Object);

        IReadOnlyList<BranchSummary> branches = await service.ListAllBranchesAsync();

        Assert.Equal(["main", "codex/feature"], branches.Select(b => b.Name));
        Assert.True(branches[0].IsCurrent);
        git.Verify(g => g.EnsureLocalBranchAsync("ws", "codex/feature", It.IsAny<CancellationToken>()), Times.Once);
        git.Verify(g => g.EnsureLocalBranchAsync("ws", "main", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(true, false, 0, false, true)]
    [InlineData(true, true, 0, false, false)]
    [InlineData(true, false, 1, false, false)]
    [InlineData(false, false, 0, false, false)]
    [InlineData(true, false, 0, true, false)]
    public async Task ShouldOfferTemplateAsync_OnlyForEmptyWorkspaceNotYetOffered(bool hasCommits, bool hasTrackedFiles, int workingTreeChanges, bool alreadyOffered, bool expected)
    {
        string dir = Directory.CreateTempSubdirectory("template-offer-test-").FullName;
        try
        {
            Mock<IGitService> git = new();
            git.Setup(g => g.HasCommitsAsync(dir, It.IsAny<CancellationToken>())).ReturnsAsync(hasCommits);
            git.Setup(g => g.HasTrackedFilesAsync(dir, It.IsAny<CancellationToken>())).ReturnsAsync(hasTrackedFiles);
            git.Setup(g => g.GetWorkingTreeChangesAsync(dir, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Enumerable.Range(0, workingTreeChanges).Select(i => new GitChange($"f{i}", GitChangeStatus.Added)).ToList());

            WorkspaceVersioningService service = new(dir, git.Object);
            if (alreadyOffered)
            {
                await service.MarkTemplateOfferedAsync();
            }

            Assert.Equal(expected, await service.ShouldOfferTemplateAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task MarkTemplateOfferedAsync_StopsFurtherOffers()
    {
        string dir = Directory.CreateTempSubdirectory("template-offer-test-").FullName;
        try
        {
            Mock<IGitService> git = new();
            git.Setup(g => g.HasCommitsAsync(dir, It.IsAny<CancellationToken>())).ReturnsAsync(true);
            git.Setup(g => g.HasTrackedFilesAsync(dir, It.IsAny<CancellationToken>())).ReturnsAsync(false);
            git.Setup(g => g.GetWorkingTreeChangesAsync(dir, It.IsAny<CancellationToken>())).ReturnsAsync([]);
            WorkspaceVersioningService service = new(dir, git.Object);

            Assert.True(await service.ShouldOfferTemplateAsync());
            await service.MarkTemplateOfferedAsync();
            Assert.False(await service.ShouldOfferTemplateAsync());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("old", true)]
    [InlineData("unpushed", false)]
    public async Task SyncWithRemoteAsync_ResetsANonCurrentBranchOnlyWhenItHadNothingUnpushed(string localTip, bool expectReset)
    {
        Mock<IGitService> git = new();
        git.Setup(g => g.GetCurrentBranchAsync("ws", It.IsAny<CancellationToken>())).ReturnsAsync("main");
        git.Setup(g => g.ListBranchesAsync("ws", "", It.IsAny<CancellationToken>())).ReturnsAsync(["main", "feature"]);
        git.Setup(g => g.BranchExistsAsync("ws", It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        git.Setup(g => g.GetRemoteTrackingCommitAsync("ws", "main", It.IsAny<CancellationToken>())).ReturnsAsync("main-tip");
        git.SetupSequence(g => g.GetRemoteTrackingCommitAsync("ws", "feature", It.IsAny<CancellationToken>()))
            .ReturnsAsync("old")
            .ReturnsAsync("rewritten");
        git.Setup(g => g.RevParseAsync("ws", "feature", It.IsAny<CancellationToken>())).ReturnsAsync(localTip);
        git.Setup(g => g.IsAncestorAsync("ws", localTip, "rewritten", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        git.Setup(g => g.FetchAsync("ws", true, It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await new WorkspaceVersioningService("ws", git.Object).SyncWithRemoteAsync();

        git.Verify(g => g.ForceUpdateBranchRefAsync("ws", "feature", "rewritten", It.IsAny<CancellationToken>()), expectReset ? Times.Once : Times.Never);
    }

    [Fact]
    public async Task FastForwardAsync_CheckoutOfTargetBlocked_FailsWithoutMerging()
    {
        Mock<IGitService> git = new();
        git.Setup(g => g.GetCurrentBranchAsync("ws", It.IsAny<CancellationToken>())).ReturnsAsync("feature");
        git.Setup(g => g.IsAncestorAsync("ws", "main", "feature", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        bool merged = await new WorkspaceVersioningService("ws", git.Object).FastForwardAsync("main", "feature");

        Assert.False(merged);
        git.Verify(g => g.FastForwardMergeAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RebaseAsync_CheckoutOfBranchBlocked_FailsWithoutRebasingTheWrongBranch()
    {
        Mock<IGitService> git = new();
        git.Setup(g => g.GetCurrentBranchAsync("ws", It.IsAny<CancellationToken>())).ReturnsAsync("main");
        git.Setup(g => g.IsAncestorAsync("ws", "main", "feature", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        GitOperationOutcome outcome = await new WorkspaceVersioningService("ws", git.Object).RebaseAsync("feature", "main", squashMessage: null);

        Assert.Equal(GitOperationOutcome.Failed, outcome);
        git.Verify(g => g.RebaseOntoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
