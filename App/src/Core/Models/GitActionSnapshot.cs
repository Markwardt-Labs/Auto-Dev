namespace AutoDev.Core.Models;

/// <summary>The checked-out branch (if any), HEAD's own commit hash, and whether the working tree had pending changes, right before a mutating git action starts - see IWorkspaceVersioningService.CaptureSnapshotAsync/RevertToSnapshotAsync, which the busy overlay's Cancel button uses to undo whatever the action had done so far.</summary>
public sealed record GitActionSnapshot(string? Branch, string CommitHash, bool HadPendingChanges);
