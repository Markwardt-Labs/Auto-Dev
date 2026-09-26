namespace AutoDev.Tests.Integration;

/// <summary>Covers WorkspaceMetadataStore's shared per-workspace Generate files under concurrent saves - several are fired without awaiting, and each rewrites the whole file, so an unguarded overlap could read a half-written file as empty and wipe every other branch's history.</summary>
public sealed class GenerateHistoryPersistenceTests : IDisposable
{
    private readonly string workspacePath = Directory.CreateTempSubdirectory("autodev-generate-history-tests-").FullName;
    private readonly WorkspaceMetadataStore store = new();

    public void Dispose() => Directory.Delete(workspacePath, recursive: true);

    [Fact]
    public async Task SaveGenerateRequestsAsync_ConcurrentSavesForDifferentBranches_KeepsEveryBranch()
    {
        string[] branches = [.. Enumerable.Range(0, 40).Select(i => $"branch-{i}")];

        await Task.WhenAll(branches.Select(branch => Task.Run(() => store.SaveGenerateRequestsAsync(workspacePath, branch,
            [new GenerateRequest { Id = branch, Input = $"input for {branch}", CreatedAt = DateTimeOffset.UtcNow }]))));

        foreach (string branch in branches)
        {
            List<GenerateRequest> loaded = await store.LoadGenerateRequestsAsync(workspacePath, branch);
            Assert.Equal($"input for {branch}", Assert.Single(loaded).Input);
        }
    }

    [Fact]
    public async Task SaveGenerateDraftAsync_ConcurrentSavesForDifferentBranches_KeepsEveryDraft()
    {
        string[] branches = [.. Enumerable.Range(0, 40).Select(i => $"branch-{i}")];

        await Task.WhenAll(branches.Select(branch => Task.Run(() => store.SaveGenerateDraftAsync(workspacePath, branch, $"draft for {branch}"))));

        foreach (string branch in branches)
        {
            Assert.Equal($"draft for {branch}", await store.LoadGenerateDraftAsync(workspacePath, branch));
        }
    }

    [Fact]
    public async Task SaveGenerateRequestsAsync_LeavesNoTempFilesBehind()
    {
        await store.SaveGenerateRequestsAsync(workspacePath, "main", [new GenerateRequest { Id = "1", Input = "x", CreatedAt = DateTimeOffset.UtcNow }]);

        Assert.Empty(Directory.EnumerateFiles(Path.Combine(workspacePath, ".autodev", "local"), "*.tmp"));
    }
}
