using AutoDev.Core.Models;
using AutoDev.ViewModels.Dialogs;

namespace AutoDev.ViewModels.Infrastructure;

/// <summary>Seam for anything that needs a native window (folder picker, modal dialogs) so ViewModels stay Avalonia-free.</summary>
public interface IDialogService
{
    /// <summary>startDirectory (if non-null and it still exists) is where the picker opens instead of its own platform default - see HeaderViewModel/IWorkspaceService.GetLastParentFolderAsync.</summary>
    Task<string?> PickFolderAsync(string? startDirectory = null);
    /// <summary>requireValue removes the Cancel button and blocks every other way of dismissing the window (native close button, Escape) - the only way out is confirming OK with a non-blank value. Used where skipping isn't a valid option.</summary>
    Task<string?> ShowInputDialogAsync(string title, string label, string initialValue = "", bool requireValue = false);
    /// <summary>confirmLabel/isDestructive default to the delete-confirmation look (red "Delete" button) that most existing callers want; pass a non-destructive action's own verb (e.g. "Publish") and isDestructive: false for those.</summary>
    Task<bool> ShowConfirmDialogAsync(string title, string message, string confirmLabel = "Delete", bool isDestructive = true);
    /// <summary>A single-button ("OK") informational popup - used for a failed git action's error message instead of a persistent inline label. See MessageDialogViewModel.</summary>
    Task ShowMessageDialogAsync(string title, string message);
    /// <summary>Prompts for the git user.name/user.email to configure globally when neither is set yet - null if cancelled. See GitIdentityDialogViewModel.</summary>
    Task<GitIdentityDialogResult?> ShowGitIdentityDialogAsync();
    /// <summary>Opens the template popup (lists every template in ITemplateService.GetTemplatesDirectory(), and - while canApply is true - applies one to the currently open workspace) - see TemplatesDialogViewModel. Returns whichever template the user applied (name plus path, for the AI to read itself), or null if the dialog was closed without applying one.</summary>
    Task<AppliedTemplate?> ShowTemplatesDialogAsync(bool canApply);
}
