using System.Diagnostics;

namespace AutoDev.Tests.Integration;

/// <summary>Real-git coverage of ref handling that plain git output gets wrong without care: a branch sharing its name with a folder, an unresolvable revision, a name git would read as an option, and a squash whose commit fails.</summary>
public sealed class GitRefEdgeCaseTests : IDisposable
{
    private readonly string repoPath = Directory.CreateTempSubdirectory("autodev-git-ref-tests-").FullName;
    private readonly GitService git = new();

    public GitRefEdgeCaseTests()
    {
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "Test");
        RunGit("config", "user.email", "test@example.com");
        RunGit("commit", "-q", "--allow-empty", "-m", "root");
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

    private void CommitFile(string relativePath, string content, string message)
    {
        string fullPath = Path.Combine(repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit("add", "-A");
        RunGit("commit", "-q", "-m", message);
    }

    [Fact]
    public async Task LogAsync_BranchNamedLikeAFolder_StillReturnsItsHistory()
    {
        CommitFile("Docs/readme.md", "x", "docs");
        RunGit("branch", "Docs");

        IReadOnlyList<GitCommit> commits = await git.LogAsync(repoPath, "Docs");

        Assert.Equal(["root", "docs"], commits.Select(c => c.Subject));
        Assert.Equal("docs", await git.GetCommitSubjectAsync(repoPath, "Docs"));
    }

    [Fact]
    public async Task RevParseAsync_UnresolvableRevision_ReturnsEmpty()
    {
        string root = await git.RevParseAsync(repoPath, "HEAD");

        Assert.Equal(40, root.Length);
        Assert.Equal("", await git.RevParseAsync(repoPath, $"{root}^"));
    }

    [Theory]
    [InlineData("-D")]
    [InlineData("my branch")]
    public async Task CreateBranchAsync_NameGitRejects_ReturnsFalseAndIsNeverReadAsAnOption(string name)
    {
        RunGit("branch", "keep");

        Assert.False(await git.CreateBranchAsync(repoPath, name, "HEAD"));
        Assert.True(await git.BranchExistsAsync(repoPath, "keep"));
    }

    [Fact]
    public async Task CreateAnnotatedTagAsync_NameStartingWithADash_ReturnsFalse()
    {
        Assert.False(await git.CreateAnnotatedTagAsync(repoPath, "-d", "HEAD"));
        Assert.True(await git.CreateAnnotatedTagAsync(repoPath, "v1", "HEAD"));
    }

    [Fact]
    public async Task SquashSinceAsync_CommitFails_LeavesTheBranchWhereItWas()
    {
        CommitFile("a.txt", "1", "one");
        string head = await git.RevParseAsync(repoPath, "HEAD");

        // Squashing onto HEAD itself leaves nothing to commit, so the commit step fails.
        Assert.False(await git.SquashSinceAsync(repoPath, head, "squashed"));
        Assert.Equal(head, await git.RevParseAsync(repoPath, "HEAD"));
    }
}
