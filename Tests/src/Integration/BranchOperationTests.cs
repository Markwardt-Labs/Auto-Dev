using System.Diagnostics;

namespace AutoDev.Tests.Integration;

/// <summary>Real-git coverage of the History tab's Squash/Rebase/Merge building blocks (WorkspaceVersioningService.SquashAsync/RebaseAsync/FastForwardAsync) in both directions - acting on the checked-out branch, or on another branch that's never checked out unless git needs it.</summary>
public sealed class BranchOperationTests : IDisposable
{
    private readonly string repoPath = Directory.CreateTempSubdirectory("autodev-branch-op-tests-").FullName;
    private readonly GitService git = new();
    private readonly WorkspaceVersioningService service;

    public BranchOperationTests()
    {
        service = new WorkspaceVersioningService(repoPath, git);
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "Test");
        RunGit("config", "user.email", "test@example.com");
        Commit("base.txt", "base", "base");
        RunGit("checkout", "-q", "-b", "feature");
        Commit("one.txt", "1", "feature one");
        Commit("two.txt", "2", "feature two");
    }

    public void Dispose() => Directory.Delete(repoPath, recursive: true);

    private string RunGit(params string[] args)
    {
        ProcessStartInfo startInfo = new("git") { WorkingDirectory = repoPath, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private void Commit(string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(repoPath, file), content);
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", message);
    }

    private string Tip(string branch) => RunGit("rev-parse", $"refs/heads/{branch}");

    private string CurrentBranch => RunGit("branch", "--show-current");

    [Fact]
    public async Task Squash_CurrentBranch_CollapsesItsCommitsSinceTheBaseIntoOne()
    {
        string treeBefore = RunGit("rev-parse", "HEAD^{tree}");

        SquashOutcome outcome = await service.SquashAsync("feature", "main", "squashed feature");

        Assert.Equal(SquashOutcome.Succeeded, outcome);
        Assert.Equal(1, await service.CountUniqueCommitsAsync("feature", "main"));
        Assert.Equal("squashed feature", await service.GetDefaultSquashMessageAsync("feature"));
        Assert.Equal(Tip("main"), RunGit("rev-parse", "feature^"));
        Assert.Equal(treeBefore, RunGit("rev-parse", "feature^{tree}"));
        Assert.Equal("feature", CurrentBranch);
    }

    [Fact]
    public async Task Squash_OtherBranch_LeavesTheCheckedOutBranchAndWorkingTreeAlone()
    {
        RunGit("checkout", "-q", "main");
        File.WriteAllText(Path.Combine(repoPath, "pending.txt"), "pending");

        SquashOutcome outcome = await service.SquashAsync("feature", "main", "squashed feature");

        Assert.Equal(SquashOutcome.Succeeded, outcome);
        Assert.Equal(1, await service.CountUniqueCommitsAsync("feature", "main"));
        Assert.Equal("main", CurrentBranch);
        Assert.Equal("pending", File.ReadAllText(Path.Combine(repoPath, "pending.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Rebase_SquashesThenReplaysOntoTheOtherBranchsNewTip(bool featureIsCheckedOut)
    {
        RunGit("checkout", "-q", "main");
        Commit("main-only.txt", "m", "main moved on");
        string mainTip = Tip("main");
        if (featureIsCheckedOut)
        {
            RunGit("checkout", "-q", "feature");
        }

        GitOperationOutcome outcome = await service.RebaseAsync("feature", "main", "squashed feature");

        Assert.Equal(GitOperationOutcome.Succeeded, outcome);
        Assert.Equal("feature", CurrentBranch);
        Assert.True(await service.IsBasedOnAsync("feature", "main"));
        Assert.Equal(1, await service.CountUniqueCommitsAsync("feature", "main"));
        Assert.Equal(mainTip, RunGit("rev-parse", "feature^"));
        Assert.True(File.Exists(Path.Combine(repoPath, "main-only.txt")));
        Assert.True(File.Exists(Path.Combine(repoPath, "two.txt")));
    }

    [Fact]
    public async Task Rebase_AlreadyBasedOnTheOtherBranch_DoesNothingAndDoesNotCheckAnythingOut()
    {
        RunGit("checkout", "-q", "main");
        string featureTip = Tip("feature");

        GitOperationOutcome outcome = await service.RebaseAsync("feature", "main", squashMessage: null);

        Assert.Equal(GitOperationOutcome.Succeeded, outcome);
        Assert.Equal("main", CurrentBranch);
        Assert.Equal(featureTip, Tip("feature"));
    }

    [Fact]
    public async Task Rebase_ConflictingChange_ReportsConflictsAndCanBeAborted()
    {
        Commit("clash.txt", "feature side", "feature clash");
        RunGit("checkout", "-q", "main");
        Commit("clash.txt", "main side", "main clash");

        GitOperationOutcome outcome = await service.RebaseAsync("feature", "main", squashMessage: null);

        Assert.Equal(GitOperationOutcome.Conflicts, outcome);
        Assert.True(await service.HasConflictsAsync());

        await service.AbortRebaseAsync();
        Assert.False(await service.HasConflictsAsync());
    }

    [Fact]
    public async Task FastForward_ToACheckedOutTarget_MovesItAndLeavesItCheckedOut()
    {
        RunGit("checkout", "-q", "main");

        bool merged = await service.FastForwardAsync("main", "feature");

        Assert.True(merged);
        Assert.Equal("main", CurrentBranch);
        Assert.Equal(Tip("feature"), Tip("main"));
        Assert.True(File.Exists(Path.Combine(repoPath, "two.txt")));
    }

    [Fact]
    public async Task FastForward_FromTheCheckedOutBranchToAnother_ChecksTheTargetOutAndMovesIt()
    {
        bool merged = await service.FastForwardAsync("main", "feature");

        Assert.True(merged);
        Assert.Equal("main", CurrentBranch);
        Assert.Equal(Tip("feature"), Tip("main"));
    }

    [Fact]
    public async Task FastForward_WhenTargetHasMovedOn_FailsAndChangesNothing()
    {
        RunGit("checkout", "-q", "main");
        Commit("main-only.txt", "m", "main moved on");
        string mainTip = Tip("main");

        bool merged = await service.FastForwardAsync("main", "feature");

        Assert.False(merged);
        Assert.Equal("main", CurrentBranch);
        Assert.Equal(mainTip, Tip("main"));
    }

    [Fact]
    public async Task Revert_PutsBackABranchTheActionRewrotAndOneItDeleted()
    {
        RunGit("checkout", "-q", "main");
        RunGit("branch", "spare", "feature");
        string featureTip = Tip("feature");
        string spareTip = Tip("spare");
        GitActionSnapshot snapshot = await service.CaptureSnapshotAsync();

        await service.SquashAsync("feature", "main", "squashed feature");
        await service.DeleteBranchAsync("spare");
        Assert.NotEqual(featureTip, Tip("feature"));

        await service.RevertToSnapshotAsync(snapshot);

        Assert.Equal(featureTip, Tip("feature"));
        Assert.Equal(spareTip, Tip("spare"));
        Assert.Equal("main", CurrentBranch);
    }
}
