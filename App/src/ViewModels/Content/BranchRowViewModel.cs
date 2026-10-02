using AutoDev.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoDev.ViewModels.Content;

/// <summary>
/// One row in the History tab's flat branch list - a branch's own name plus whether it's the currently
/// checked-out one, with this row's own selection state (which branch's timeline the right pane is currently
/// showing). Also carries the context menu's Squash/Rebase/Merge labels for this row, which name both branches
/// so the direction of each of the two options per action is unambiguous.
/// </summary>
public sealed partial class BranchRowViewModel(BranchSummary branch, string? currentBranchName) : ViewModelBase
{
    [ObservableProperty]
    private BranchSummary branch = branch;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>False while HEAD is detached - the branch-relative Squash/Rebase/Merge options need a current branch to be relative to, so the menu hides them.</summary>
    public bool HasCurrentBranch => currentBranchName is not null;

    public string SquashCurrentHeader => $"Squash {Current} since {Selected}";

    public string SquashSelectedHeader => $"Squash {Selected} since {Current}";

    public string RebaseCurrentHeader => $"Rebase {Current} onto {Selected}";

    public string RebaseSelectedHeader => $"Rebase {Selected} onto {Current}";

    public string MergeCurrentHeader => $"Merge {Current} into {Selected}";

    public string MergeSelectedHeader => $"Merge {Selected} into {Current}";

    private string Current => Quote($"{currentBranchName}", " (current)");

    private string Selected => Quote(Branch.Name, "");

    /// <summary>A menu item's Header treats an underscore as an access-key marker (and drops it) - doubled, it shows literally, which a branch name like `feature_x` needs.</summary>
    private string Quote(string name, string suffix) => $"'{name.Replace("_", "__")}'{suffix}";
}
