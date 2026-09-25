namespace AutoDev.Tests.Core.Services;

/// <summary>Covers WorkspaceVersioningService.ListAllBranchesAsync's handling of branches that exist only as remote-tracking refs.</summary>
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
}
