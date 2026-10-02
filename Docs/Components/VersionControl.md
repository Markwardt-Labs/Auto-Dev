# Version Control

AutoDev drives plain git directly, with no naming convention or invented identity layered on top.
A branch or tag's only identity is its own literal git ref name - the repo stays a perfectly normal
git repo usable with any other tool, and everything AutoDev shows or does maps to an ordinary git
command.

## Layers

- **`src/Core/Services/IGitService`/`GitService`** - thin, safe wrapper around the `git` CLI (via
  `CliWrap`). `IsInstalled` (`GitCliLocator`, checking `PATH` exactly like `ClaudeCliLocator`/
  `CodexCliLocator` do for their own CLIs) is checked once at launch by `AuthGateViewModel`, which
  refuses to start the app at all without it - every other member here would otherwise throw the
  moment it tried to launch a `git` process that doesn't exist. Every call goes through one
  `RunAsync` helper that sets `GIT_EDITOR`/`GIT_SEQUENCE_EDITOR=true` (so nothing can ever block on
  an interactive editor) and two more things aimed at the same goal - a remote operation with no way
  to get credentials fails fast with a real error instead of hanging forever: `GIT_TERMINAL_PROMPT=0`
  (git's own username/password terminal prompt) and `GIT_SSH_COMMAND` with `BatchMode=yes` (the
  SSH-transport equivalent) plus `StrictHostKeyChecking=accept-new` (so that doesn't also block a
  legitimate first-time clone from a never-before-seen host). Deliberately does **not** also disable
  the user's own configured credential helper (an earlier `-c credential.helper=` override did, and
  broke push/fetch/clone entirely - "could not read Username ... terminal prompts disabled" - for
  anyone whose credentials come from one, despite working fine in a terminal) - a helper answering
  non-interactively (a cached token, a system keychain, ...) or via its own working GUI flow behaves
  exactly as it would outside AutoDev either way. No business logic lives here - just running plain
  git commands and parsing output.
- **`src/Core/Services/IWorkspaceVersioningService`/`WorkspaceVersioningService`** - the actual
  branch/tag workflow (Checkout, Branch, Tag, Delete, Reset, Rebase, Merge, Squash, Commit), built
  directly on the above.
- **`ViewModels/Sidebar/VersionSectionViewModel`** - a passive display of the current target plus
  the shared busy/lock state every action runs through, the AI-assisted conflict-resolution loop for
  the History tab's Rebase/Merge (and its own stash-pop conflicts), and the stash-aware auto-pull itself
  (`PullWithStashIfNeededAsync`).
- **`ViewModels/Content/HistoryTabViewModel`** - a read-only branch/timeline browser over the same
  service, and the home of every action itself (see below) as a right-click context menu.

## Auto-initializing a repo

`WorkspaceViewModel.InitializeAsync()` calls `VersionSectionViewModel.EnsureRepoAsync()` on
every workspace open:

```csharp
public async Task EnsureRepoAsync()
{
    if (!await versioningService.IsRepoInitializedAsync())
    {
        await RunBusyAsync(() => versioningService.InitializeRepoAsync());
    }
    else
    {
        await RefreshAsync();
        await versioningService.EnsureLocalGitExcludeAsync();
    }

    periodicSyncTimer.Start();
}
```

`IsRepoInitializedAsync` is deliberately stricter than "does `.git` exist":

```csharp
public async Task<bool> IsRepoInitializedAsync(CancellationToken cancellationToken = default)
{
    if (!await git.IsRepoAsync(workspacePath, cancellationToken))
    {
        return false;
    }

    return await git.HasCommitsAsync(workspacePath, cancellationToken);
}
```

`HasCommitsAsync` checks the *exit code* of `git rev-parse --verify --quiet HEAD` rather than
inspecting its output - `git rev-parse HEAD` on an unborn branch prints the literal text `"HEAD"`
back to stdout instead of nothing, so a naive "is the output non-empty" check silently reports
"has commits" even when it doesn't. This distinction is what makes **cloning an empty remote
repository** behave identically to **opening a brand-new folder with no git repo at all**: a fresh
clone of an empty remote is a real git work tree (so `IsRepoAsync` alone would say "yes,
initialized") but has zero commits, so `EnsureRepoAsync` still routes it through
`InitializeRepoAsync`.

```csharp
public async Task InitializeRepoAsync(CancellationToken cancellationToken = default)
{
    await git.InitAsync(workspacePath, cancellationToken);           // git init - safe no-op if .git exists
    await EnsureLocalGitExcludeAsync(cancellationToken);              // .git/info/exclude += .autodev/local/

    await git.CommitEmptyAsync(workspacePath, "Initial commit", cancellationToken);
    await git.RenameCurrentBranchAsync(workspacePath, "main", cancellationToken);

    // No-op if there's no "origin" yet (a plain new folder); for a repo that got here via cloning
    // an empty remote, "origin" is already configured, so this is what actually lands the new
    // main branch on the remote instead of leaving it sitting local-only.
    await git.PushAsync(workspacePath, "main", setUpstream: true, cancellationToken: cancellationToken);
}
```

So: open an empty folder, or clone an empty remote repo, and AutoDev transparently leaves you with
a plain `main` branch whose single "Initial commit" is genuinely empty (`git commit --allow-empty`,
no `git add` first - `IGitService.CommitEmptyAsync`) - ready to use immediately, with that empty
state pushed back to the remote if one exists. Anything already sitting in the folder (a non-empty
new folder, or a clone that already had local-only files) is left as pending, uncommitted content
instead of being silently swept into that first commit - the user commits it explicitly afterward,
same as any other edit.

The "scaffold from a template?" offer follows that same initialization, and is also made once for an
already-initialized workspace that is still completely empty - typically a clone of a remote whose only
commit is exactly that empty "Initial commit" (`IWorkspaceVersioningService.ShouldOfferTemplateAsync`: no
tracked files at HEAD, no working-tree changes, and no `.autodev/local/template-offered` marker, which
`MarkTemplateOfferedAsync` writes so a declined offer isn't repeated on every open).

## The Version sidebar (`VersionSectionViewModel`)

A passive display plus one click-to-open menu. It shows whatever `GitTarget Target` currently is
(`BranchName`/`TagName` where applicable, plus HEAD's own `CommitMessage`/`CommitHash`, always
present), all centered - see `VersionSectionView`. Pending changes (`HasPendingChanges`) show as a
small asterisk in the section's top-right corner and a highlighted background over the whole
section, rather than a separate badge.

Clicking anywhere on the section opens a `MenuFlyout` with every action that targets the currently
checked-out branch directly, rather than some other row in the History tab:

| Menu item | What it does | Shown |
|---|---|---|
| Commit | Prompts for a message, commits pending changes, pushes | always |
| Reset | Confirms, then discards pending changes (`git reset --hard` + `clean -fd`) | always |
| Branch | Prompts for a name, creates a branch at the current target (`HEAD`), checks it out | always |
| Tag | Prompts for a name, creates an annotated tag (blank message) at the current target | always |
| Remote | Prompts for and configures the `origin` URL | always |

Squash, Rebase, and Merge aren't here - they're relative to *another* branch, so they live on the
History tab's branch context menu (see "Actions" below). A failed action (a branch/tag name
collision, an unresolvable rebase, a merge that isn't actually fast-forwardable) calls `VersionSectionViewModel.MarkFailed` rather than a persistent
inline label - see "The busy overlay" below for what that actually does.

It also owns the shared busy/lock machinery every action (Commit/Reset/Branch/Tag/Remote here,
everything else triggered from the History tab - see below) runs through:

- `IsBusy`/`IsAiWorking`/`HasRunningScripts` combined into `IsInteractionBlocked`, which locks the
  sidebar, Edit tab, and History tab's action commands while true. `HasRunningScripts` is set from
  `FilesSectionViewModel.HasRunningScripts` - a running
  `.cs` file locks Commit/Merge/etc. and every History tab action exactly like a busy version
  action or an in-flight AI turn does, so manual editing, script running, and AI working never race
  the same working tree.
- `GitOutputLog` - the current action's own live git command log (each command line plus its
  output), shown in the busy overlay - see "The busy overlay" below.
- `RunBusyAsync(action)` - checks/prompts for a git identity first (see "Git identity prompt"
  below), then sets `IsBusy`, clears `GitOutputLog`/`IsBusyFailed`, captures a pre-action
  `GitActionSnapshot` (`CaptureSnapshotAsync`), flushes any pending Edit-tab autosave via
  `FlushPendingEditBeforeMutation`, then runs `action` with a fresh `CancellationToken` (see below).
  Always refreshes (`RefreshAsync`) afterward, whether `action` succeeded, failed, or was cancelled -
  but only actually closes the overlay immediately on success; see "The busy overlay" for what a
  failure does differently.
- `MarkFailed(message)` - what an `action` lambda calls instead of popping up its own dialog when it
  finds a normal, expected failure (a branch/tag name collision, a rejected push, a merge that isn't
  fast-forwardable, ...): appends `message` to `GitOutputLog` and sets `IsBusyFailed`, which is what
  actually keeps the overlay open afterward. Public (not just used by this class's own actions) -
  `HistoryTabViewModel`'s own Squash/Rebase/Merge actions call `version.MarkFailed`
  the same way.
- `ResolveConflictsAsync(outcome, continueAction, cancellationToken)` - the AI-assisted
  conflict-resolution loop shared by the History tab's Rebase/Merge (below) and the stash-aware
  pull.
- A background `periodicSyncTimer` (60s) posts a refresh (fetch/prune + reset any non-current local
  branch to match its remote counterpart, and detach+delete the checked-out branch itself if *its own*
  remote counterpart is what got pruned - see `SyncWithRemoteAsync` below) whenever the section isn't
  currently locked.

`SyncWithRemoteAsync` (`IWorkspaceVersioningService`, wrapping `IGitService.FetchAsync(prune: true)`)
is the one place all of this fetch/prune/resync logic actually lives - `RefreshAsync` above,
`HistoryTabViewModel.RefreshFromRemoteAsync`, and the periodic timer all just call it:

- Every local branch *other* than the checked-out one is hard-reset to match its own
  `origin/{branch}` wherever they differ - the checked-out branch is deliberately never touched this
  way, so local work in progress on it is never silently overwritten by someone else's push. A branch
  is only reset if it had nothing unpushed: it matched `origin/{branch}` before the fetch, or it's an
  ancestor of the new remote tip. One carrying its own commits (e.g. a commit whose push failed while
  offline, before switching away) is left alone rather than orphaning them.
- If the checked-out branch's *own* remote counterpart is what the prune just removed (e.g. someone
  else - or this app's own post-merge cleanup, see "Actions" below - deleted it on the remote while
  it was still checked out here), it's detached at exactly the commit it was already on (a no-op
  checkout content-wise, so any pending changes are left completely untouched) and then deleted
  locally too, rather than left pointing at a now-nonexistent remote branch forever.

### Git identity prompt

Every `RunBusyAsync` call checks `IWorkspaceVersioningService.HasUserIdentityConfiguredAsync` first
(`git var GIT_AUTHOR_IDENT` - git's own author-identity resolution, exit code alone tells whether it
succeeded) *before* doing anything else - not just for actions that obviously create a commit, since
maintaining a list of exactly which ones might need one isn't worth it when the check itself is one
cheap git call away from certain either way. Deliberately not two separate `git config user.name`/
`user.email` reads: those use `git config <key>`'s implicit single-value `--get`, which fails with a
"multiple values" error if either key is set more than once even though it's genuinely configured,
and neither one recognizes an identity that resolves purely from `GIT_AUTHOR_NAME`/`GIT_AUTHOR_EMAIL`
env vars rather than `git config` at all - both of which `git var GIT_AUTHOR_IDENT` (the exact
resolution a real `git commit` itself performs) handles correctly. If no identity resolves at all,
`GitIdentityDialogViewModel`/`GitIdentityDialogWindow` prompts for a name and email and
`SetGlobalUserIdentityAsync` writes them via `git config --global` (never scoped to just the one
workspace) before the actual action ever runs; cancelling the prompt abandons the whole action with
nothing having happened yet, the same as any other pre-condition dialog in this file (e.g. Squash's
"No other branch to squash against"). This is what stops git's own "Please tell me who you are"
failure from ever actually happening in the first place, on a machine where git is installed but was
never configured.

### The busy overlay: live git output log, Cancel, and Confirm

`WorkspaceView`'s busy overlay (bound to `Version.IsBusy`) shows, alongside the usual
indeterminate progress bar: a scrolling, auto-following log of every git command the current action
runs (command line plus stdout/stderr - see `GitCommandLogSink` below). While the action is still
running, that's paired with a Cancel button (`Version.CancelBusyCommand`); once it's failed
(`Version.IsBusyFailed` - see `MarkFailed` above), the progress bar/Cancel are replaced by a red
"Failed" label and a Confirm button (`Version.ConfirmBusyCommand`) instead, and the overlay stops
auto-closing - `RunBusyAsync` awaits a `TaskCompletionSource` that only `ConfirmBusyCommand`
completes, so a failure (and the git log explaining it) always stays on screen until the user has
actually seen it and dismissed it themselves, rather than the log disappearing the instant the
action itself finishes. A *successful* action never sets `IsBusyFailed` at all, so the overlay still
auto-closes exactly as before the moment it's done - nothing about the ordinary path changed.

`GitCommandLogSink` is a small `AsyncLocal<Action<string>?>`-backed static class, not a persistent
event subscription on `IGitService` (a shared, app-wide singleton) - `RunBusyAsync` sets
`GitCommandLogSink.Current` right before calling `action`, and `GitService`'s own `RunAsync` helper
reports to whatever `Current` is (if anything) after every command. Being `AsyncLocal` means it
flows automatically into everything that single action call awaits, and needs no unsubscription
when the open workspace closes - the alternative (an event on the shared `IGitService`) would leak
a handler referencing a dead `VersionSectionViewModel` every time a workspace closed and reopened
over the app's lifetime.

Clicking Cancel signals `RunBusyAsync`'s own `CancellationTokenSource`, whose token every action
lambda threads into whichever `IWorkspaceVersioningService` calls it makes (all of them already
accept a trailing `CancellationToken`) - all the way down to `GitService.RunAsync`'s
`ExecuteBufferedAsync(cancellationToken)`, so cancelling can actually kill an in-flight git
subprocess, not just race to be first past a check. `RunBusyAsync` catches the resulting
`OperationCanceledException` and calls `RevertToSnapshotAsync(snapshot)` - a generic, action-agnostic
undo: abort any in-progress rebase/merge, check out the pre-action branch again if a different one
ended up checked out, then move it back to the pre-action commit hash. The snapshot also records every
local branch's tip, so any *other* branch the action rewrote (a squash/rebase of a branch that was never
checked out) or deleted (a merge's cleanup) is put back too. The snapshot also records
whether there were pending changes: only a snapshot taken from a clean tree is hard-reset (with
`git clean`, removing anything the action itself left behind); otherwise a mixed reset moves the branch
while leaving every file as it is, so cancelling never discards work that was pending before the action
started. It doesn't know or care which specific action it's undoing; every mutating action's own
effect reduces to "the checked-out branch moved and/or its tip advanced", which this reverses -
cancelling closes the overlay immediately once reverted, the same as it always has (the user's own
Cancel click already *is* their acknowledgement, so there's nothing for a Confirm step to add here).
A normal git failure (bad credentials, no permission, a rejected push, ...) never reaches an
exception at all - it comes back as an ordinary `false`/`GitOperationOutcome.Failed` result, which
each action's own caller turns into its own `MarkFailed` message (e.g. "Rebasing 'x' onto 'y' failed.").
`RunBusyAsync` also catches any *other* exception as a backstop - reverts the same way, then calls
`MarkFailed` itself, so something truly unexpected (not a normal git failure, which the `RunAsync`
overrides described above mean should never throw in the first place) fails exactly as visibly as
any other action failure rather than crashing the app.

The Cancel/Confirm buttons only matter while `IsBusy` is actually up - during a Rebase/Merge
conflict's own AI-resolution turn (below), `IsBusy` is deliberately dropped so the user can
watch/interact with the Generate tab, which has its own Cancel for that part of the flow.

### Squash, Rebase, and Merge

All three are on a non-current branch's context menu in the History tab, each in **two directions**
(six items in all), with every label naming both branches so the direction is never ambiguous.
With `feature` checked out and `main` right-clicked:

| Menu item | Operates on | Relative to |
|---|---|---|
| Squash 'feature' (current) since 'main' | `feature` | `main` |
| Squash 'main' since 'feature' (current) | `main` | `feature` |
| Rebase 'feature' (current) onto 'main' | `feature` | `main` |
| Rebase 'main' onto 'feature' (current) | `main` | `feature` |
| Merge 'feature' (current) into 'main' | `feature` (source) | `main` (target) |
| Merge 'main' into 'feature' (current) | `main` (source) | `feature` (target) |

They're hidden while HEAD is detached (nothing to be relative to). `HistoryTabViewModel` has one
private implementation of each action, taking the branch being changed and the branch it's relative
to; the six commands just pick which is which (`BranchRowViewModel` builds the labels, doubling any
`_` in a branch name since a menu header would otherwise read it as an access key).

- **Squash** collapses the commits the branch has that the other doesn't (`git log other..branch`) into
  one, asking for the message (defaulting to the branch's last commit message), then force-pushes
  that branch. It's done with `git commit-tree` + `git update-ref` (`IGitService.SquashBranchAsync`),
  so it never touches the working tree - it works on a branch that isn't checked out, and with
  pending changes. Refused with a message if the branch has no commits of its own.
- **Rebase** first squashes the same way *if* the branch has more than one commit of its own (a rebase
  can't offer a meaningful per-commit AI conflict-resolution loop otherwise - each attempt only gets
  one shot at the whole diff), then rebases the branch onto the other's tip, resolving conflicts
  automatically in the Generate tab (see below), then force-pushes the branch. Nothing is replayed if
  the branch is already built on the other's tip. Git can only rebase in the working tree, so a branch
  that isn't checked out is checked out for the duration (`WorkspaceVersioningService.RebaseAsync`
  verifies that checkout actually took before running `git rebase`, so a blocked one can't rewrite
  the wrong branch) and the original branch is checked back out afterward.
- **Merge** does the Rebase steps first when they're needed - the source has more than one commit
  (squashed to one), or isn't built on the target's tip - then fast-forwards the target to the source
  (`FastForwardAsync`: checks the target out, `git merge --ff-only`) and pushes the target with a normal
  push. The now-absorbed source branch is then deleted locally and on the remote
  (`DeleteBranchEverywhereAsync`), which is why the target is always left checked out - git refuses to
  delete the checked-out branch, and a merge started from the source's own checkout would otherwise
  leave it stuck. A branch with nothing the target lacks is refused with a message; the source isn't
  force-pushed along the way, since it's deleted at the end anyway.

Rebase and Merge need a clean working tree (`EnsureCleanWorkingTreeAsync` shows a message otherwise) -
they check branches out and rewrite history under it. No remote at all isn't a failure for any of
the pushes (`PushBranchAsync` treats it as nothing to push). The squash boundary is always
`git merge-base(branch, other)`, so history still shows the branch forking from the true common
ancestor with its own commits collapsed to one.

## Actions - right-click a branch, commit, or tag in the History tab

Every git action that targets some *other* row instead of the current branch itself lives on a
context menu, not a sidebar button - see `HistoryTabViewModel`/`HistoryTabView.axaml`. Each one
runs through `VersionSectionViewModel.RunBusyAsync`, so it locks the same controls the Version
section's own actions do.

**Branch row** (nothing is shown for the current branch's own row - see
`HistoryTabView.OnBranchContextRequested`, which suppresses the popup entirely for it):

| Menu item | What it does |
|---|---|
| Checkout | `git checkout <branch>` (confirms discarding pending changes first) |
| Squash / Rebase / Merge (each in both directions, six items) | see "Squash, Rebase, and Merge" above; only while a branch is checked out |
| Delete | `git branch -D` |

**Commit row:** Checkout (detaches HEAD there).

**Tag row:** Checkout, Delete Tag (`git tag -d`).

A Commit/Tag row's *left*-click expands/collapses its changed-files view in place
(`ToggleExpandedCommand`) - a right-click only opens the context menu above, never also toggling
the changes view (see `HistoryTabView.OnEntryPointerPressed`, which checks
`PointerPressedEventArgs.GetCurrentPoint(null).Properties.IsLeftButtonPressed` before acting).

### AI-assisted conflict resolution

The History tab's Rebase and Merge items (Merge only when it has to rebase first) and
`PullWithStashIfNeededAsync`'s own stash-pop conflicts (see "Fetching" below) all share
`VersionSectionViewModel.ResolveConflictsAsync`. If the initial attempt reports
`GitOperationOutcome.Conflicts`, the loop (up to 3 attempts):

1. Switches the workspace to the Generate tab (`SwitchToGenerateRequested`, handled by
   `WorkspaceViewModel`) so the exchange is never something the user has to go and notice/find
   themselves - it also renders its own panel there instead of a normal request card (see "Generate
   tab display" just below).
2. Lists conflicted files (`GetConflictedFilesAsync`).
3. Builds an instruction and drives it through **the same Claude session the Generate tab uses**,
   via `GenerateTabViewModel.RunAutomatedTurnAsync(instruction)` - not a separate process. The
   default wording is generic ("here are the conflicted files, resolve the markers and `git add`
   the results"); `PullWithStashIfNeededAsync` passes its own instead, since a stashed-changes-vs-
   newly-pulled-commits conflict isn't "two branches" and the AI needs to know exactly which commits
   are newly pulled in (everything since the commit hash captured before the stash/pull began).
4. Checks `HasConflictsAsync()` again; if still conflicted, retries (budget permitting); otherwise
   calls whichever continuation the caller passed in (`ContinueRebaseAsync`, or
   for a stash-pop conflict - which has no git "continue" step of its own - a lambda that just
   confirms success).

`IsBusy` is dropped to `false` for the duration of the actual Claude turn (only `IsAiWorking`
stays set) so the user can watch/interact with the Generate tab while it works, restored to
whatever it was *before the whole loop started* once it ends - `true` for Rebase/Merge (called from
inside `RunBusyAsync`, which keeps its own busy overlay up for whatever git work is still left) and
`false` for `PullWithStashIfNeededAsync` (called with no such overlay at all, so it stays overlay-free
once conflict resolution finishes rather than getting stuck open). On success the rebased branch is
force-pushed by Rebase (`MarkFailed` if that push itself fails - the rebase already succeeded locally by
that point), or carried on into Merge's fast-forward/push/cleanup; on exhausted attempts the rebase is
aborted (`AbortRebaseAsync`) and `MarkFailed` explains it couldn't be resolved automatically, keeping
the busy overlay (and its log) up until the user confirms - see "The busy overlay" above.
`PullWithStashIfNeededAsync` has no such overlay to keep
open on exhausted attempts either - it just leaves the popped stash's conflict markers in place,
uncommitted, and the stash entry itself un-dropped, for the user to sort out by hand. Merge's own
fast-forward never goes through this loop - it either applies cleanly or fails outright, with no
conflict state to resolve; only the rebase before it can conflict.

#### Generate tab display, and why Stop/Cancel are never offered

A conflict-resolution turn is a `GenerateTabViewModel.RunAutomatedTurnAsync(visible: true)` call -
`VisibleAutomatedTurnActive` while it's in flight - which never creates a request card the way a
genuine user-submitted message does. `GenerateTabView`
shows a dedicated panel instead whenever `VisibleAutomatedTurnActive` is true, with its own
"Resolving merge conflicts…"/"Paused" status line (`ConflictResolutionStatusText`) and Claude's
latest reply (`LastAssistantText`), live-updating as it streams in. It offers **only Pause and
Resume** - never Cancel or Stop, which stay entirely absent (not just disabled): forcibly killing a
conflict-resolution turn mid-flight would leave the repository sitting in a half-resolved,
conflicted state with no clean way back, which the whole point of pausing (resumable, including
across an app restart's own subprocess) is designed to avoid. `CanPause`/`CanResume` recognize
`VisibleAutomatedTurnActive` the same way they recognize a genuine paused request; `PauseAsync` kills
the subprocess and captures the session id exactly like pausing a normal turn, but leaves
`RunAutomatedTurnAsync`'s own `TaskCompletionSource` pending rather than resolving it, so whichever
git action is awaiting it (`ResolveConflictsAsync`) simply stays suspended until `ResumeAsync`
eventually leads to a real reply. The Generate tab's own message box is disabled the whole time too
(`CanSend` excludes `VisibleAutomatedTurnActive`) - a stray interjection into this narrow,
strict-reply-format exchange could corrupt it, the same reasoning that already excludes a hidden
turn.

`VersionSectionViewModel` also listens to `GenerateTabViewModel`'s `NormalTurnStarted/Completed`
events directly - so a plain user-submitted Generate turn (nothing to do with conflict resolution)
also locks every History tab action until the user reviews and commits, avoiding a race between
Claude's tool calls and a concurrent git mutation.

## History tab (`HistoryTabViewModel`)

A read-only browser built directly on the same service, plus every action above:

- `ListAllBranchesAsync()` → `BranchSummary(Name, IsCurrent)` per branch (a branch known only as `origin/*` gets a local
  branch materialized first, via `EnsureLocalBranchAsync`), current-first then
  alphabetical - shown as a flat list (`BranchRows`), no parent/child hierarchy. `IsCurrent` (bold
  row - see `HistoryTabView.axaml`'s `currentBranch` style) is only ever true for the actual
  checked-out branch, so nothing is bolded here at all while HEAD is detached at a tag/commit -
  correctly, since no branch really is current then.
- `GetBranchTimelinePageAsync(branchName, pageIndex)` → a `BranchTimelinePage` of that branch's own
  plain commit history (newest first, 100 per page), with a `Tag` entry inserted immediately above
  whichever commit it points at. Every entry's `IsCurrentCommit` (bold text, plus a blue dot for a
  plain commit row - see the `current`/`commitGlyph.current` styles) is just `commit.Hash == HEAD`,
  regardless of whether HEAD is attached to a branch or detached at a tag/commit, and regardless of
  whether `branchName` here is even the checked-out branch - so checking out a tag or an arbitrary
  commit still shows exactly which commit (and any tag pointing at it) is current, on whichever
  branch's timeline happens to contain it, not just when browsing the checked-out branch's own.

Left-clicking a branch row navigates the timeline to it (`SelectBranch`, no git action). The
timeline reloads automatically whenever `VersionSectionViewModel.TargetChanged` fires from
anywhere else in the app.

### Fetching (`RefreshFromRemoteAsync`/`FetchCommand`)

The tab keeps itself in sync with the remote two ways:

- **Automatically, every time it becomes the active tab** -
  `WorkspaceContentViewModel.OnSelectedTabIndexChanged`'s `HistoryTabIndex` case (and
  `WorkspaceViewModel.InitializeAsync`, for the very first view of a freshly opened workspace,
  since History is the default tab and so never actually triggers that same tab-index-changed path)
  call `RefreshFromRemoteAsync`, which runs `Version.RefreshAsync` (fetch/prune/resync - see "The
  Version sidebar" above) then `Version.PullWithStashIfNeededAsync` - transparently pulling the
  current branch the moment its remote counterpart has moved ahead
  (`IWorkspaceVersioningService.PullCurrentBranchWithStashAsync` - `git merge --ff-only
  origin/{branch}`, assuming the fetch that already just happened; a no-op if there's nothing new,
  or the branch has genuinely diverged rather than just being behind). Unlike a plain fast-forward,
  this never refuses to run just because the working tree is dirty: pending changes (tracked and
  untracked alike, `git stash push -u`) are stashed first and popped back on top once the pull
  lands, rather than only pulling while the tree happens to be clean. If popping the stash conflicts
  with what was just pulled in, it's resolved exactly like a Rebase/Merge conflict (see "AI-assisted
  conflict resolution" above) - switching to Generate automatically, Pause/Resume-only controls, the
  whole way - with the stash dropped only once actually resolved (a clean pop already drops its own
  automatically). No busy overlay for the fetch/pull itself - this is an automatic refresh triggered
  by switching tabs, not a user-initiated mutation, the same treatment `periodicSyncTimer` already
  gets - only an actual stash-pop conflict visibly locks the workspace, via the Generate tab
  scenario above.
- **On demand, via the tab's own Fetch button** (`FetchCommand`, top-right of the timeline panel) -
  fetches with prune the same way, through the normal busy overlay like any other action here, but
  never also pulls even with a clean working tree; a deliberate click means "check what's new," not
  "also apply it."
