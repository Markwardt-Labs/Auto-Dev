using System.Diagnostics;

namespace AutoDev.Tests.Integration;

/// <summary>Real-git coverage of undoing a cancelled busy action (WorkspaceVersioningService.CaptureSnapshotAsync/RevertToSnapshotAsync) - it must undo what the action did without ever discarding work that was already pending before it started.</summary>
public sealed class CancelledGitActionTests : IDisposable
{
    private readonly string repoPath = Directory.CreateTempSubdirectory("autodev-cancel-tests-").FullName;
    private readonly GitService git = new();

    public CancelledGitActionTests()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "Test");
        RunGit("config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(repoPath, "tracked.txt"), "original");
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", "initial");
    }

    public void Dispose() => Directory.Delete(repoPath, recursive: true);

    private void RunGit(params string[] args)
    {
        ProcessStartInfo startInfo = new("git") { WorkingDirectory = repoPath, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();
    }

    [Fact]
    public async Task Revert_WithPendingChanges_UndoesTheCommitAndKeepsEveryPendingFile()
    {
        File.WriteAllText(Path.Combine(repoPath, "tracked.txt"), "edited");
        File.WriteAllText(Path.Combine(repoPath, "new.txt"), "brand new");
        WorkspaceVersioningService service = new(repoPath, git);
        GitActionSnapshot snapshot = await service.CaptureSnapshotAsync();

        await git.CommitAsync(repoPath, "the action being cancelled");
        await service.RevertToSnapshotAsync(snapshot);

        Assert.Equal(snapshot.CommitHash, await git.RevParseAsync(repoPath, "HEAD"));
        Assert.Equal("edited", File.ReadAllText(Path.Combine(repoPath, "tracked.txt")));
        Assert.Equal("brand new", File.ReadAllText(Path.Combine(repoPath, "new.txt")));
    }

    [Fact]
    public async Task Revert_WithPendingChanges_ReturnsToTheOriginalBranchKeepingThem()
    {
        File.WriteAllText(Path.Combine(repoPath, "new.txt"), "brand new");
        WorkspaceVersioningService service = new(repoPath, git);
        GitActionSnapshot snapshot = await service.CaptureSnapshotAsync();

        await git.CreateBranchAsync(repoPath, "feature", "HEAD");
        await git.CheckoutAsync(repoPath, "feature");
        await service.RevertToSnapshotAsync(snapshot);

        Assert.Equal("main", await git.GetCurrentBranchAsync(repoPath));
        Assert.Equal("brand new", File.ReadAllText(Path.Combine(repoPath, "new.txt")));
    }

    [Fact]
    public async Task Revert_FromACleanTree_StillRemovesWhatTheActionLeftBehind()
    {
        WorkspaceVersioningService service = new(repoPath, git);
        GitActionSnapshot snapshot = await service.CaptureSnapshotAsync();

        File.WriteAllText(Path.Combine(repoPath, "leftover.txt"), "from the action");
        await git.CommitAsync(repoPath, "the action being cancelled");
        File.WriteAllText(Path.Combine(repoPath, "stray.txt"), "untracked");
        await service.RevertToSnapshotAsync(snapshot);

        Assert.Equal(snapshot.CommitHash, await git.RevParseAsync(repoPath, "HEAD"));
        Assert.False(File.Exists(Path.Combine(repoPath, "leftover.txt")));
        Assert.False(File.Exists(Path.Combine(repoPath, "stray.txt")));
    }
}
