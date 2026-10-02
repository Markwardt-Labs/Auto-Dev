using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public sealed class WorkspaceVersioningService(string workspacePath, IGitService git) : IWorkspaceVersioningService
{
    private static readonly string localExcludePattern = ".autodev/local/";

    private string TemplateOfferedMarkerPath => Path.Combine(workspacePath, ".autodev", "local", "template-offered");

    public async Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (!await git.IsRepoAsync(workspacePath, cancellationToken))
        {
            return false;
        }

        // A freshly cloned-from-empty-remote repo is still a real git work tree (IsRepoAsync above already
        // returns true for it) but has no commits yet - HEAD is an "unborn" branch. Treating that the same as
        // "no repo yet" routes it through InitializeRepoAsync below just like a brand new plain folder.
        return await git.HasCommitsAsync(workspacePath, cancellationToken);
    }

    public async Task<bool> ShouldOfferTemplateAsync(CancellationToken cancellationToken = default) =>
        !File.Exists(TemplateOfferedMarkerPath)
        && await git.HasCommitsAsync(workspacePath, cancellationToken)
        && !await git.HasTrackedFilesAsync(workspacePath, cancellationToken)
        && (await git.GetWorkingTreeChangesAsync(workspacePath, cancellationToken)).Count == 0;

    public async Task MarkTemplateOfferedAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TemplateOfferedMarkerPath)!);
        await File.WriteAllTextAsync(TemplateOfferedMarkerPath, "", cancellationToken);
    }

    public async Task<bool> HasUserIdentityConfiguredAsync(CancellationToken cancellationToken = default) =>
        await git.HasUserIdentityConfiguredAsync(workspacePath, cancellationToken);

    public async Task SetGlobalUserIdentityAsync(string name, string email, CancellationToken cancellationToken = default) =>
        await git.SetGlobalUserIdentityAsync(workspacePath, name, email, cancellationToken);

    public async Task InitializeRepoAsync(CancellationToken cancellationToken = default)
    {
        await git.InitAsync(workspacePath, cancellationToken);
        await EnsureLocalGitExcludeAsync(cancellationToken);

        // Deliberately empty - anything already in the folder is left as pending, uncommitted content the
        // user commits explicitly afterward, same as any other edit, rather than silently folded into a
        // commit they never asked for.
        await git.CommitEmptyAsync(workspacePath, "Initial commit", cancellationToken);
        await git.RenameCurrentBranchAsync(workspacePath, "main", cancellationToken);

        // A no-op for a plain new folder (no "origin" yet - PushAsync itself checks and just returns false).
        // For a folder that reached here via a clone of an empty remote, "origin" is already configured from
        // the clone, so this is what actually lands the new main branch on the remote instead of leaving it
        // sitting local-only next to an otherwise-still-empty remote.
        await git.PushAsync(workspacePath, "main", setUpstream: true, cancellationToken: cancellationToken);
    }

    public async Task EnsureLocalGitExcludeAsync(CancellationToken cancellationToken = default)
    {
        string excludePath = Path.Combine(workspacePath, ".git", "info", "exclude");
        if (!Directory.Exists(Path.GetDirectoryName(excludePath)))
        {
            return; // not a git repo (.git/info missing) - nothing to do
        }

        string existing = File.Exists(excludePath) ? await File.ReadAllTextAsync(excludePath, cancellationToken) : "";
        if (existing.Split('\n').Any(line => line.Trim() == localExcludePattern))
        {
            return;
        }

        string separator = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
        await File.AppendAllTextAsync(excludePath, $"{separator}{localExcludePattern}\n", cancellationToken);
    }

    public async Task<GitTarget?> GetCurrentTargetAsync(CancellationToken cancellationToken = default)
    {
        if (!await git.IsRepoAsync(workspacePath, cancellationToken))
        {
            return null;
        }

        string hash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        string shortHash = hash.Length > 7 ? hash[..7] : hash;
        string message = await git.GetCommitSubjectAsync(workspacePath, "HEAD", cancellationToken);

        string? branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is not null)
        {
            return new GitTarget(GitTargetKind.Branch, branch, null, shortHash, message);
        }

        string? tag = await git.GetExactTagAsync(workspacePath, cancellationToken);
        return new GitTarget(tag is not null ? GitTargetKind.Tag : GitTargetKind.Commit, null, tag, shortHash, message);
    }

    public async Task ConfigureRemoteAsync(string url, CancellationToken cancellationToken = default) =>
        await git.SetRemoteAsync(workspacePath, url, cancellationToken);

    public async Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default) =>
        await git.GetRemoteUrlAsync(workspacePath, cancellationToken);

    public async Task<bool> HasUncommittedChangesAsync(CancellationToken cancellationToken = default) =>
        await git.HasUncommittedChangesAsync(workspacePath, cancellationToken);

    public async Task SyncWithRemoteAsync(CancellationToken cancellationToken = default)
    {
        string? current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);

        // Captured before the fetch/prune below, which is exactly what could remove it - the only way to
        // tell "current never tracked a remote branch at all" (leave it alone) apart from "it did, and that
        // remote branch is now gone" (see the current-specific handling at the bottom) is to know whether it
        // was there beforehand.
        bool currentHadRemoteTrackingBranch = current is not null
            && await git.GetRemoteTrackingCommitAsync(workspacePath, current, cancellationToken) is not null;

        // Which non-current local branches had nothing unpushed before this fetch - also captured up front,
        // since the fetch below moves the remote-tracking refs this compares against.
        HashSet<string> inSyncBeforeFetch = [];
        foreach (string branch in await git.ListBranchesAsync(workspacePath, "", cancellationToken))
        {
            if (branch != current
                && await git.BranchExistsAsync(workspacePath, branch, cancellationToken)
                && await git.GetRemoteTrackingCommitAsync(workspacePath, branch, cancellationToken) is { } priorRemoteTip
                && priorRemoteTip == await git.RevParseAsync(workspacePath, branch, cancellationToken))
            {
                inSyncBeforeFetch.Add(branch);
            }
        }

        if (!await git.FetchAsync(workspacePath, prune: true, cancellationToken))
        {
            return; // no remote, or unreachable - nothing to sync
        }

        IReadOnlyList<string> candidates = await git.ListBranchesAsync(workspacePath, "", cancellationToken);

        foreach (string branch in candidates)
        {
            if (branch == current || !await git.BranchExistsAsync(workspacePath, branch, cancellationToken))
            {
                continue; // the checked-out branch is handled separately below; skip remote-only names with no local ref
            }

            string? remoteTip = await git.GetRemoteTrackingCommitAsync(workspacePath, branch, cancellationToken);
            if (remoteTip is null)
            {
                continue;
            }

            string localTip = await git.RevParseAsync(workspacePath, branch, cancellationToken);
            if (remoteTip == localTip)
            {
                continue;
            }

            // Mirroring the remote (including a history rewrite pushed from elsewhere) is only safe when the
            // local branch had nothing of its own - e.g. a commit whose push failed while offline, before the
            // user switched away. Resetting that branch would silently orphan those commits.
            if (inSyncBeforeFetch.Contains(branch) || await git.IsAncestorAsync(workspacePath, localTip, remoteTip, cancellationToken))
            {
                await git.ForceUpdateBranchRefAsync(workspacePath, branch, remoteTip, cancellationToken);
            }
        }

        if (current is not null && currentHadRemoteTrackingBranch
            && await git.GetRemoteTrackingCommitAsync(workspacePath, current, cancellationToken) is null)
        {
            // The branch actually checked out just got pruned - its own remote counterpart is gone (e.g.
            // deleted by someone else's own post-merge cleanup elsewhere - see
            // VersionSectionViewModel.MergeAsync/HistoryTabViewModel.MergeIntoCurrentAsync, which do the same
            // thing this app itself just did). Detach HEAD at exactly the commit it was already on - a no-op
            // checkout content-wise (same commit, so it can never conflict), leaving any pending changes in
            // the working tree completely untouched - before deleting the now-orphaned local branch, since
            // git refuses to delete whichever branch is currently checked out.
            string currentTip = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
            await git.CheckoutAsync(workspacePath, currentTip, cancellationToken);
            await git.DeleteBranchAsync(workspacePath, current, cancellationToken);
        }
    }

    public async Task<GitActionSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default)
    {
        string? branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        string hash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        bool hadPendingChanges = await git.HasUncommittedChangesAsync(workspacePath, cancellationToken);
        IReadOnlyDictionary<string, string> branchTips = await git.GetLocalBranchTipsAsync(workspacePath, cancellationToken);
        return new GitActionSnapshot(branch, hash, hadPendingChanges, branchTips);
    }

    public async Task RevertToSnapshotAsync(GitActionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        if (await git.HasConflictsAsync(workspacePath, cancellationToken))
        {
            await git.RebaseAbortAsync(workspacePath, cancellationToken);
            await git.MergeAbortAsync(workspacePath, cancellationToken);
        }

        if (snapshot.Branch is not null)
        {
            string? currentBranch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
            if (currentBranch != snapshot.Branch)
            {
                // A plain checkout carries pending changes across - only safe to discard first when there
                // were none of the user's own to lose.
                if (!snapshot.HadPendingChanges)
                {
                    await git.DiscardChangesAsync(workspacePath, cancellationToken);
                }

                await git.CheckoutAsync(workspacePath, snapshot.Branch, cancellationToken);
            }
        }

        // Cancel (or an unexpected error) must only undo what the action did, never the work that was already
        // pending when it started - a hard reset + clean would permanently delete untracked files too.
        if (snapshot.HadPendingChanges)
        {
            await git.ResetMixedAsync(workspacePath, snapshot.CommitHash, cancellationToken);
        }
        else
        {
            await git.ResetHardAsync(workspacePath, snapshot.CommitHash, cancellationToken);
        }

        // Every other branch the action may have rewritten (squash/rebase of a branch that was never checked
        // out) or deleted (merge cleanup) goes back to where it was - the checked-out one was just reset above.
        foreach ((string branch, string tip) in snapshot.BranchTips)
        {
            if (branch == snapshot.Branch)
            {
                continue;
            }

            if (!await git.BranchExistsAsync(workspacePath, branch, cancellationToken))
            {
                await git.CreateBranchAsync(workspacePath, branch, tip, cancellationToken);
            }
            else if (await git.RevParseAsync(workspacePath, $"refs/heads/{branch}", cancellationToken) != tip)
            {
                await git.ForceUpdateBranchRefAsync(workspacePath, branch, tip, cancellationToken);
            }
        }
    }

    public async Task<BranchCreationOutcome> CreateBranchAsync(string name, string fromRef, CancellationToken cancellationToken = default)
    {
        if (await git.BranchExistsAsync(workspacePath, name, cancellationToken))
        {
            return BranchCreationOutcome.IdAlreadyExists;
        }

        if (!await git.CreateBranchAsync(workspacePath, name, fromRef, cancellationToken))
        {
            return BranchCreationOutcome.InvalidName;
        }

        await git.CheckoutAsync(workspacePath, name, cancellationToken);
        await git.PushAsync(workspacePath, name, force: false, setUpstream: true, cancellationToken: cancellationToken);
        return BranchCreationOutcome.Created;
    }

    public async Task<TagCreationOutcome> CreateTagAsync(string name, string atRef, CancellationToken cancellationToken = default)
    {
        if (await git.TagExistsAsync(workspacePath, name, cancellationToken))
        {
            return TagCreationOutcome.IdAlreadyExists;
        }

        if (!await git.CreateAnnotatedTagAsync(workspacePath, name, atRef, cancellationToken))
        {
            return TagCreationOutcome.InvalidName;
        }

        await git.PushAsync(workspacePath, name, force: false, setUpstream: false, cancellationToken: cancellationToken);
        return TagCreationOutcome.Created;
    }

    public async Task DeleteBranchAsync(string name, CancellationToken cancellationToken = default) =>
        await git.DeleteBranchAsync(workspacePath, name, cancellationToken);

    public async Task<bool> DeleteBranchEverywhereAsync(string name, CancellationToken cancellationToken = default)
    {
        await git.DeleteBranchAsync(workspacePath, name, cancellationToken);
        return await git.GetRemoteUrlAsync(workspacePath, cancellationToken) is null
            || await git.DeleteRemoteBranchAsync(workspacePath, name, cancellationToken);
    }

    public async Task DeleteTagAsync(string name, CancellationToken cancellationToken = default) =>
        await git.DeleteTagAsync(workspacePath, name, cancellationToken);

    public async Task<bool> DeleteTagEverywhereAsync(string name, CancellationToken cancellationToken = default)
    {
        await git.DeleteTagAsync(workspacePath, name, cancellationToken);
        return await git.GetRemoteUrlAsync(workspacePath, cancellationToken) is null
            || await git.DeleteRemoteTagAsync(workspacePath, name, cancellationToken);
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default) =>
        await git.DiscardChangesAsync(workspacePath, cancellationToken);

    public async Task<GitOperationOutcome> ContinueRebaseAsync(CancellationToken cancellationToken = default) =>
        await git.RebaseContinueAsync(workspacePath, cancellationToken);

    public async Task AbortRebaseAsync(CancellationToken cancellationToken = default) =>
        await git.RebaseAbortAsync(workspacePath, cancellationToken);

    public async Task<bool> HasConflictsAsync(CancellationToken cancellationToken = default) =>
        await git.HasConflictsAsync(workspacePath, cancellationToken);

    public async Task<IReadOnlyList<string>> GetConflictedFilesAsync(CancellationToken cancellationToken = default) =>
        await git.GetConflictedFilesAsync(workspacePath, cancellationToken);

    public async Task CommitAsync(string message, CancellationToken cancellationToken = default)
    {
        await git.CommitAsync(workspacePath, message, cancellationToken);
        string? branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is not null)
        {
            await git.PushAsync(workspacePath, branch, cancellationToken: cancellationToken);
        }
    }

    public async Task<bool> PushBranchAsync(string branchName, bool force, CancellationToken cancellationToken = default) =>
        await git.GetRemoteUrlAsync(workspacePath, cancellationToken) is null
        || await git.PushAsync(workspacePath, branchName, force: force, cancellationToken: cancellationToken);

    public async Task<PullWithStashResult> PullCurrentBranchWithStashAsync(CancellationToken cancellationToken = default)
    {
        string? branch = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        if (branch is null)
        {
            return new PullWithStashResult(PullWithStashOutcome.NothingToDo, null);
        }

        // Saved before touching anything - not just the pull's own precondition check below, but also what a
        // caller building an AI conflict-resolution instruction needs to describe "every commit pulled in
        // since" (see HistoryTabViewModel/VersionSectionViewModel).
        string originalCommitHash = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);

        string? remoteTip = await git.GetRemoteTrackingCommitAsync(workspacePath, branch, cancellationToken);
        if (remoteTip is null || remoteTip == originalCommitHash
            || !await git.IsAncestorAsync(workspacePath, originalCommitHash, remoteTip, cancellationToken))
        {
            // No remote-tracking branch, already up to date, or the current branch has diverged from it (its
            // own local commits aren't all on the remote yet either) - not a simple fast-forward, so nothing
            // this flow silently auto-pulls; a real divergence needs an explicit Rebase/Merge from the user.
            return new PullWithStashResult(PullWithStashOutcome.NothingToDo, originalCommitHash);
        }

        bool hadPendingChanges = await git.HasUncommittedChangesAsync(workspacePath, cancellationToken);
        if (hadPendingChanges && !await git.StashPushAsync(workspacePath, cancellationToken))
        {
            return new PullWithStashResult(PullWithStashOutcome.Failed, originalCommitHash);
        }

        if (!await git.FastForwardMergeAsync(workspacePath, $"origin/{branch}", cancellationToken))
        {
            if (hadPendingChanges)
            {
                // Best-effort restore of exactly what was pending before this call - the pull itself never
                // touched the working tree (fast-forward failed outright), so the pop it's undone by should
                // always be clean.
                await git.StashPopAsync(workspacePath, cancellationToken);
            }

            return new PullWithStashResult(PullWithStashOutcome.Failed, originalCommitHash);
        }

        if (!hadPendingChanges)
        {
            return new PullWithStashResult(PullWithStashOutcome.Succeeded, originalCommitHash);
        }

        GitOperationOutcome popOutcome = await git.StashPopAsync(workspacePath, cancellationToken);
        return popOutcome switch
        {
            GitOperationOutcome.Succeeded => new PullWithStashResult(PullWithStashOutcome.Succeeded, originalCommitHash),
            GitOperationOutcome.Conflicts => new PullWithStashResult(PullWithStashOutcome.Conflicts, originalCommitHash),
            _ => new PullWithStashResult(PullWithStashOutcome.Failed, originalCommitHash),
        };
    }

    public async Task DropStashAsync(CancellationToken cancellationToken = default) =>
        await git.StashDropAsync(workspacePath, cancellationToken);

    public async Task CheckoutRefAsync(string refName, CancellationToken cancellationToken = default) =>
        await git.CheckoutAsync(workspacePath, refName, cancellationToken);

    public async Task<string> GetDefaultSquashMessageAsync(string branchName, CancellationToken cancellationToken = default) =>
        await git.GetCommitSubjectAsync(workspacePath, branchName, cancellationToken);

    public async Task<int> CountUniqueCommitsAsync(string branchName, string baseBranch, CancellationToken cancellationToken = default) =>
        (await git.GetCommitsSinceAsync(workspacePath, baseBranch, branchName, cancellationToken)).Count;

    public async Task<bool> IsBasedOnAsync(string branchName, string baseBranch, CancellationToken cancellationToken = default) =>
        await git.IsAncestorAsync(workspacePath, baseBranch, branchName, cancellationToken);

    public async Task<SquashOutcome> SquashAsync(string branchName, string baseBranch, string message, CancellationToken cancellationToken = default)
    {
        if (!await SquashUniqueCommitsAsync(branchName, baseBranch, message, cancellationToken))
        {
            return SquashOutcome.SquashFailed;
        }

        return await PushBranchAsync(branchName, force: true, cancellationToken) ? SquashOutcome.Succeeded : SquashOutcome.PushFailed;
    }

    public async Task<GitOperationOutcome> RebaseAsync(string branchName, string ontoBranch, string? squashMessage, CancellationToken cancellationToken = default)
    {
        if (squashMessage is not null && !await SquashUniqueCommitsAsync(branchName, ontoBranch, squashMessage, cancellationToken))
        {
            return GitOperationOutcome.Failed;
        }

        if (await git.IsAncestorAsync(workspacePath, ontoBranch, branchName, cancellationToken))
        {
            return GitOperationOutcome.Succeeded; // already built on ontoBranch's tip - nothing to replay
        }

        if (!await CheckoutBranchAsync(branchName, cancellationToken))
        {
            return GitOperationOutcome.Failed; // rebasing whichever branch is still checked out instead would rewrite the wrong one
        }

        return await git.RebaseOntoAsync(workspacePath, ontoBranch, cancellationToken);
    }

    public async Task<bool> FastForwardAsync(string targetBranch, string sourceBranch, CancellationToken cancellationToken = default)
    {
        if (!await git.IsAncestorAsync(workspacePath, targetBranch, sourceBranch, cancellationToken))
        {
            return false; // sourceBranch isn't built on targetBranch's own head - can't fast-forward
        }

        string? original = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);

        // A checkout blocked by pending changes leaves the original branch checked out - "fast-forwarding" it
        // would then merge into the wrong branch, and the caller would go on to delete sourceBranch.
        if (!await CheckoutBranchAsync(targetBranch, cancellationToken))
        {
            return false;
        }

        if (await git.FastForwardMergeAsync(workspacePath, sourceBranch, cancellationToken))
        {
            return true;
        }

        if (original is not null && original != targetBranch)
        {
            await git.CheckoutAsync(workspacePath, original, cancellationToken);
        }

        return false;
    }

    private async Task<bool> CheckoutBranchAsync(string branchName, CancellationToken cancellationToken)
    {
        if (await git.GetCurrentBranchAsync(workspacePath, cancellationToken) != branchName)
        {
            await git.CheckoutAsync(workspacePath, branchName, cancellationToken);
        }

        return await git.GetCurrentBranchAsync(workspacePath, cancellationToken) == branchName;
    }

    private async Task<bool> SquashUniqueCommitsAsync(string branchName, string baseBranch, string message, CancellationToken cancellationToken)
    {
        string mergeBase = await git.MergeBaseAsync(workspacePath, baseBranch, branchName, cancellationToken);
        return mergeBase.Length > 0 && await git.SquashBranchAsync(workspacePath, branchName, mergeBase, message, cancellationToken);
    }

    public async Task<IReadOnlyList<BranchSummary>> ListAllBranchesAsync(CancellationToken cancellationToken = default)
    {
        string? current = await git.GetCurrentBranchAsync(workspacePath, cancellationToken);
        IReadOnlyList<string> names = await git.ListBranchesAsync(workspacePath, "", cancellationToken);

        List<BranchSummary> results = new List<BranchSummary>();
        foreach (string name in names)
        {
            if (!await git.BranchExistsAsync(workspacePath, name, cancellationToken))
            {
                await git.EnsureLocalBranchAsync(workspacePath, name, cancellationToken); // remote-only: materialize so it's listable/selectable like any other branch
            }

            results.Add(new BranchSummary(name, IsCurrent: name == current));
        }

        return [.. results.OrderByDescending(b => b.IsCurrent).ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<BranchTimelinePage?> GetBranchTimelinePageAsync(string branchName, int pageIndex, int pageSize = 100, CancellationToken cancellationToken = default)
    {
        if (!await git.BranchExistsAsync(workspacePath, branchName, cancellationToken))
        {
            return null;
        }

        IReadOnlyList<GitCommit> allCommits = await git.LogAsync(workspacePath, branchName, cancellationToken); // oldest-first
        List<GitCommit> workCommits = allCommits.Reverse().ToList(); // newest first

        int pageCount = Math.Max(1, (int)Math.Ceiling(workCommits.Count / (double)pageSize));
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        List<GitCommit> pageCommits = workCommits.Skip(pageIndex * pageSize).Take(pageSize).ToList();

        string head = await git.RevParseAsync(workspacePath, "HEAD", cancellationToken);
        IReadOnlyDictionary<string, IReadOnlyList<GitTag>> tagsByCommit = await git.GetTagsByCommitAsync(workspacePath, cancellationToken);

        List<BranchTimelineEntry> entries = new List<BranchTimelineEntry>();
        foreach (GitCommit commit in pageCommits)
        {
            // Just the commit hash, regardless of whether HEAD is attached to a branch or detached at a tag/
            // commit (previously also required the *viewed* branch to be the checked-out one, which meant a
            // detached HEAD - browsing a tag or an arbitrary commit - never showed a current-commit indicator
            // anywhere at all) - a commit matching HEAD's hash genuinely is the current commit no matter what
            // ref got you there, or which branch's own timeline happens to be on screen.
            bool isCurrentCommit = commit.Hash == head;

            // Each tag gets its own node immediately above the commit it points at, rather than riding along
            // on the commit's own row - see BranchTimelineEntryKind.Tag. IsCurrentCommit carries over too, so
            // the row's own "Checkout" menu item isn't offered as a no-op from a tag pointing at HEAD.
            foreach (GitTag? tag in tagsByCommit.GetValueOrDefault(commit.Hash, []))
            {
                entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.Tag, tag.DisplayName, commit.Date, commit.Hash, isCurrentCommit, tag.Name));
            }

            entries.Add(new BranchTimelineEntry(BranchTimelineEntryKind.Commit, commit.Subject, commit.Date, commit.Hash, isCurrentCommit));
        }

        return new BranchTimelinePage(branchName, entries, pageIndex, pageCount);
    }

    public async Task<IReadOnlyList<GitChange>> GetCommitChangesAsync(string commitHash, CancellationToken cancellationToken = default) =>
        await git.GetCommitChangesAsync(workspacePath, commitHash, cancellationToken);

    public async Task<FileDiffContent> GetFileDiffAsync(string commitHash, string relativePath, CancellationToken cancellationToken = default)
    {
        // Empty (rather than a resolved hash) is git's own way of saying `{commitHash}^` doesn't exist -
        // commitHash is this branch's root commit, with nothing before it to compare against.
        string parentHash = await git.RevParseAsync(workspacePath, $"{commitHash}^", cancellationToken);
        string? before = parentHash.Length > 0
            ? await git.GetFileContentAtCommitAsync(workspacePath, parentHash, relativePath, cancellationToken)
            : null;
        string? after = await git.GetFileContentAtCommitAsync(workspacePath, commitHash, relativePath, cancellationToken);
        return new FileDiffContent(before, after);
    }

    public async Task<IReadOnlyList<GitChange>> GetWorkingTreeChangesAsync(CancellationToken cancellationToken = default) =>
        await git.GetWorkingTreeChangesAsync(workspacePath, cancellationToken);

    public async Task<FileDiffContent> GetWorkingTreeFileDiffAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        string? before = await git.GetFileContentAtCommitAsync(workspacePath, "HEAD", relativePath, cancellationToken);

        string fullPath = Path.Combine(workspacePath, relativePath);
        string? after = File.Exists(fullPath) ? await File.ReadAllTextAsync(fullPath, cancellationToken) : null;

        return new FileDiffContent(before, after);
    }
}

public sealed class VersioningServiceFactory(IGitService git) : IVersioningServiceFactory
{
    public IWorkspaceVersioningService Create(string workspacePath) => new WorkspaceVersioningService(workspacePath, git);
}
