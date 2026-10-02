using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public enum BranchCreationOutcome
{
    Created,
    IdAlreadyExists,

    /// <summary>git rejected the name itself (e.g. it contains a space or starts with "-").</summary>
    InvalidName,
}

public enum TagCreationOutcome
{
    Created,
    IdAlreadyExists,

    /// <summary>git rejected the name itself (e.g. it contains a space or starts with "-").</summary>
    InvalidName,
}

/// <summary>The outcome of IWorkspaceVersioningService.SquashAsync.</summary>
public enum SquashOutcome
{
    Succeeded,

    /// <summary>The squash commit itself failed - the branch was left exactly where it was, nothing pushed.</summary>
    SquashFailed,

    /// <summary>Squashed locally, but the force-push to the remote failed.</summary>
    PushFailed,
}

/// <summary>The outcome of IWorkspaceVersioningService.PullCurrentBranchWithStashAsync.</summary>
public enum PullWithStashOutcome
{
    /// <summary>No branch checked out, no remote-tracking counterpart, already up to date, or the current branch has diverged from it (not a simple fast-forward) - nothing this flow handles; the last case needs an explicit Rebase/Merge from the user, not a silent auto-pull.</summary>
    NothingToDo,

    /// <summary>Pulled cleanly - either there were no pending changes to begin with, or there were and the stash popped back on top with no conflicts (and was auto-dropped by git itself).</summary>
    Succeeded,

    /// <summary>Popping the stash after a clean pull produced merge conflicts - the working tree is left exactly as `git stash pop` leaves it (conflict markers in place, the stash entry itself NOT dropped - see IGitService.StashPopAsync) for the caller to resolve, typically via VersionSectionViewModel.ResolveConflictsAsync followed by IGitService.StashDropAsync once resolved.</summary>
    Conflicts,

    /// <summary>The stash push/pop or the pull itself failed for a reason other than conflicts - rare (e.g. an in-progress merge/rebase git refuses to stash over). Left as-is for the user to investigate; a pending stash from a failed push is never silently discarded.</summary>
    Failed,
}

/// <summary>Result of PullCurrentBranchWithStashAsync - OriginalCommitHash is the current branch's own HEAD commit right before this call did anything (null only for PullWithStashOutcome.NothingToDo's "no branch checked out" case), used to tell an AI conflict-resolution instruction exactly which commits are newly pulled in (everything since this hash) versus the user's own stashed changes.</summary>
public sealed record PullWithStashResult(PullWithStashOutcome Outcome, string? OriginalCommitHash);

/// <summary>
/// Business logic for the git-backed branch/tag workflow, built directly on IGitService with no naming
/// convention or invented semantics layered on top - a branch or tag's only identity is its own literal git
/// ref name. Deliberately has no knowledge of Claude or any ViewModel - same separation as
/// IWorkspaceScriptRunner knowing nothing about the Generate tab.
/// </summary>
public interface IWorkspaceVersioningService
{
    /// <summary>False both for a plain folder with no .git yet AND for an existing git work tree with no commits (e.g. a fresh `git clone` of an empty remote) - either way, EnsureRepoAsync routes to InitializeRepoAsync so both end up with the same initial "main" branch.</summary>
    Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>True for an already-initialized workspace that's still completely empty (no tracked files at HEAD, nothing else in the working tree) and whose template offer hasn't been made yet - e.g. a fresh clone of a repo whose only commit is an empty "Initial commit", which IsRepoInitializedAsync alone can't tell apart from a real project. See MarkTemplateOfferedAsync.</summary>
    Task<bool> ShouldOfferTemplateAsync(CancellationToken cancellationToken = default);

    /// <summary>Records (in `.autodev/local/`, so per clone) that the empty-workspace template offer has been made, so a declined offer isn't repeated on every open.</summary>
    Task MarkTemplateOfferedAsync(CancellationToken cancellationToken = default);

    /// <summary>See IGitService.HasUserIdentityConfiguredAsync - checked by VersionSectionViewModel.RunBusyAsync before every action, since any of them might need to create a commit.</summary>
    Task<bool> HasUserIdentityConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>See IGitService.SetGlobalUserIdentityAsync.</summary>
    Task SetGlobalUserIdentityAsync(string name, string email, CancellationToken cancellationToken = default);

    /// <summary>Silent, no prompts: git init (a safe no-op if .git already exists, e.g. a cloned repo), ensures AutoDev's own local-only bookkeeping (.autodev/local/) is excluded via .git/info/exclude, makes a genuinely empty "Initial commit" (nothing staged, even if the folder already has content - see IGitService.CommitEmptyAsync), renames the resulting branch to "main", then pushes it upstream if "origin" is already configured (a no-op otherwise).</summary>
    Task InitializeRepoAsync(CancellationToken cancellationToken = default);

    /// <summary>Derived fresh from git state every call - see GitTarget. Null if the repo isn't initialized yet.</summary>
    Task<GitTarget?> GetCurrentTargetAsync(CancellationToken cancellationToken = default);

    /// <summary>Configures (or repoints) the "origin" remote - callable any time, not just at repo creation.</summary>
    Task ConfigureRemoteAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>The current "origin" remote URL, or null if none is configured.</summary>
    Task<string?> GetRemoteUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>Idempotently ensures ".autodev/local/" is listed in .git/info/exclude - see the old doc comment this carries forward: a per-clone, local-only ignore rule that keeps AutoDev's own bookkeeping out of git status without the user ever seeing it.</summary>
    Task EnsureLocalGitExcludeAsync(CancellationToken cancellationToken = default);

    Task<bool> HasUncommittedChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches (+prunes deleted remote branches), then hard-resets every local branch OTHER than the currently checked-out one to match its remote-tracking counterpart wherever they differ - so local work in progress on the checked-out branch is never silently overwritten this way. If the checked-out branch's own remote counterpart is what got pruned (deleted on the remote, e.g. by this app's own post-merge cleanup - see HistoryTabViewModel's Merge actions), that branch is detached (checked out by commit hash, so pending changes are untouched) and then deleted locally too, rather than left pointing at nothing. Best-effort - a missing/unreachable remote is silently ignored, same tolerance as every other remote call here.</summary>
    Task SyncWithRemoteAsync(CancellationToken cancellationToken = default);

    /// <summary>The checked-out branch (if any), HEAD's own commit hash, and whether there were pending changes, right before a mutating busy action starts - see RevertToSnapshotAsync, which the busy overlay's Cancel button uses to undo whatever the action had done so far.</summary>
    Task<GitActionSnapshot> CaptureSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Best-effort undo back to `snapshot`: aborts an in-progress rebase/merge if there is one, checks out `snapshot.Branch` again if a different branch ended up checked out, then moves it back to `snapshot.CommitHash`. Pending changes that existed before the action are never discarded: only a snapshot taken with a clean working tree is hard-reset (removing anything the action itself left behind); otherwise the branch is moved with a mixed reset that leaves every file as it is. Doesn't know or care which specific action it's undoing - every mutating action's own effects reduce to "the checked-out branch moved and/or its tip advanced", which this reverses generically.</summary>
    Task RevertToSnapshotAsync(GitActionSnapshot snapshot, CancellationToken cancellationToken = default);

    // --- History tab actions ---

    /// <summary>Creates a new branch named `name` starting at `fromRef` (a branch/tag/commit ref) and checks it out.</summary>
    Task<BranchCreationOutcome> CreateBranchAsync(string name, string fromRef, CancellationToken cancellationToken = default);

    /// <summary>Creates an annotated tag named `name` at `atRef` (always annotated, with a deliberately blank message - see IGitService.CreateAnnotatedTagAsync) and pushes it.</summary>
    Task<TagCreationOutcome> CreateTagAsync(string name, string atRef, CancellationToken cancellationToken = default);

    /// <summary>`git branch -D` - callers confirm with the user first.</summary>
    Task DeleteBranchAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes `name` locally, then on the remote too if one's configured - used to clean up a branch once its work has been merged elsewhere (see HistoryTabViewModel's Merge actions). Local deletion always runs regardless; true unless a configured remote's own deletion push actually fails (no remote at all is not a failure - nothing to clean up there).</summary>
    Task<bool> DeleteBranchEverywhereAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>`git tag -d` - callers confirm with the user first.</summary>
    Task DeleteTagAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Deletes `name` locally, then on the remote too if one's configured - CreateTagAsync always pushes a tag it creates, so deletion is symmetric rather than leaving the remote copy behind for HistoryTabViewModel.DeleteTagAsync to silently resurrect on the next fetch. Local deletion always runs regardless; true unless a configured remote's own deletion push actually fails (no remote at all is not a failure - nothing to clean up there).</summary>
    Task<bool> DeleteTagEverywhereAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Discards all pending changes (`git reset --hard` + `git clean -fd`). Destructive; callers confirm with the user first.</summary>
    Task ResetAsync(CancellationToken cancellationToken = default);

    Task<GitOperationOutcome> ContinueRebaseAsync(CancellationToken cancellationToken = default);

    Task AbortRebaseAsync(CancellationToken cancellationToken = default);

    Task<bool> HasConflictsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetConflictedFilesAsync(CancellationToken cancellationToken = default);

    Task CommitAsync(string message, CancellationToken cancellationToken = default);

    /// <summary>Pushes `branchName` to the remote (`force` rewrites its remote history, needed after a squash/rebase) - used once a Squash/Rebase/Merge's own local work is done (CreateBranchAsync/CreateTagAsync/CommitAsync push inline themselves, since they're single synchronous operations with no external continuation step in between). True if there's no remote configured at all (nothing to push to) or the push succeeded; false only on an actual push failure, so the caller can surface it (see VersionSectionViewModel.MarkFailed) instead of silently treating an unpushed local change as if everything succeeded.</summary>
    Task<bool> PushBranchAsync(string branchName, bool force, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fast-forwards the currently checked-out branch onto `origin/{that branch}` - assumes the caller already
    /// fetched (see HistoryTabViewModel.RefreshFromRemoteAsync, the only caller, which always runs
    /// SyncWithRemoteAsync first), so this never fetches on its own the way IGitService.FastForwardPullAsync
    /// does. Transparently stashes (`git stash push -u`, so untracked files are grabbed too) and pops pending
    /// changes around the pull instead of refusing to run while any exist. See PullWithStashOutcome for what
    /// each result means; a Conflicts result leaves the popped stash's conflict markers in place, uncommitted,
    /// for the caller to resolve (typically via VersionSectionViewModel.ResolveConflictsAsync) before dropping
    /// the stash entry itself (IGitService.StashDropAsync) - a clean pop already drops it automatically.
    /// </summary>
    Task<PullWithStashResult> PullCurrentBranchWithStashAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops the most recent stash entry - used once a PullWithStashOutcome.Conflicts result's conflicts have been resolved and staged (a clean pop needs no such call - see PullWithStashOutcome.Succeeded's own doc comment).</summary>
    Task DropStashAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks out a branch, tag, or arbitrary commit - attaches HEAD to it if it's a branch name, otherwise detaches at that exact spot. Caller is responsible for confirming discard of pending changes first.</summary>
    Task CheckoutRefAsync(string refName, CancellationToken cancellationToken = default);

    /// <summary>The subject of `branchName`'s tip commit - the default commit message offered when squashing that branch.</summary>
    Task<string> GetDefaultSquashMessageAsync(string branchName, CancellationToken cancellationToken = default);

    /// <summary>How many commits `branchName` has that `baseBranch` doesn't (`git log baseBranch..branchName`) - what Squash collapses into one, and what Rebase/Merge use to decide whether a squash is needed first.</summary>
    Task<int> CountUniqueCommitsAsync(string branchName, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>True if `baseBranch`'s tip is already part of `branchName`'s history (`branchName` is built on top of it) - nothing left to rebase, and `baseBranch` can be fast-forwarded to `branchName`.</summary>
    Task<bool> IsBasedOnAsync(string branchName, string baseBranch, CancellationToken cancellationToken = default);

    /// <summary>Collapses every commit `branchName` has that `baseBranch` doesn't into one commit with `message` (see IGitService.SquashBranchAsync - works whether or not `branchName` is checked out), then force-pushes `branchName` - see SquashOutcome. Never pushes if the squash itself failed.</summary>
    Task<SquashOutcome> SquashAsync(string branchName, string baseBranch, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebases `branchName` onto `ontoBranch`'s tip - first squashing its own commits since diverging into one
    /// with `squashMessage` if that's non-null (so only a single commit ever gets replayed - AI conflict
    /// resolution, see VersionSectionViewModel.ResolveConflictsAsync, only gets one shot at the whole diff, not
    /// one per original commit). Succeeds without rebasing if `branchName` is already built on `ontoBranch`.
    /// When `branchName` isn't the checked-out branch it's checked out first (git can only rebase in the
    /// working tree), and left checked out afterward - callers return to whichever branch they started on, and
    /// need a clean working tree to begin with. Doesn't push - see PushBranchAsync.
    /// </summary>
    Task<GitOperationOutcome> RebaseAsync(string branchName, string ontoBranch, string? squashMessage, CancellationToken cancellationToken = default);

    /// <summary>Fast-forwards `targetBranch` to `sourceBranch`'s tip, leaving `targetBranch` checked out (checked out first if it wasn't - the original branch is checked back out if that fails). False, with nothing moved, unless `targetBranch` is already part of `sourceBranch`'s history (the fast-forward precondition) and, if `targetBranch` wasn't checked out, the checkout actually took (pending changes can block it). Doesn't push - see PushBranchAsync.</summary>
    Task<bool> FastForwardAsync(string targetBranch, string sourceBranch, CancellationToken cancellationToken = default);

    // --- History tab ---

    /// <summary>Every branch, current branch first then alphabetical - a branch existing only as a remote-tracking ref gets a local branch materialized so it's listed too.</summary>
    Task<IReadOnlyList<BranchSummary>> ListAllBranchesAsync(CancellationToken cancellationToken = default);

    /// <summary>One 100-entries-at-a-time page of `branchName`'s own commit/tag history (newest first), for the History tab's up/down pager - see BranchTimelinePage. Null if `branchName` doesn't exist.</summary>
    Task<BranchTimelinePage?> GetBranchTimelinePageAsync(string branchName, int pageIndex, int pageSize = 100, CancellationToken cancellationToken = default);

    /// <summary>Every file `commitHash` changed - populates a timeline commit's expanded changes tree (see IGitService.GetCommitChangesAsync).</summary>
    Task<IReadOnlyList<GitChange>> GetCommitChangesAsync(string commitHash, CancellationToken cancellationToken = default);

    /// <summary>`relativePath`'s content immediately before and after `commitHash` - powers the History tab's "open this change" read-only diff view (see FileDiffContent).</summary>
    Task<FileDiffContent> GetFileDiffAsync(string commitHash, string relativePath, CancellationToken cancellationToken = default);

    // --- Files section Changes Mode ---

    /// <summary>Every path with a pending change right now, workspace-wide (see IGitService.GetWorkingTreeChangesAsync) - populates the Files section's Changes Mode tree.</summary>
    Task<IReadOnlyList<GitChange>> GetWorkingTreeChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>`relativePath`'s content at HEAD versus right now on disk - the working-tree equivalent of GetFileDiffAsync, for a change that hasn't been committed yet. Before is null for a file that's new (untracked or freshly staged); After is null for one that's been deleted from disk.</summary>
    Task<FileDiffContent> GetWorkingTreeFileDiffAsync(string relativePath, CancellationToken cancellationToken = default);
}

public interface IVersioningServiceFactory
{
    IWorkspaceVersioningService Create(string workspacePath);
}
