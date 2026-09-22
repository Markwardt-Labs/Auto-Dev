using System.Collections.ObjectModel;
using AutoDev.Core.Models;
using AutoDev.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AutoDev.ViewModels.Dialogs;

/// <summary>Lists every template in ITemplateService.GetTemplatesDirectory(), with Apply per row while CanApply (scaffold the currently open workspace), plus an "Open Templates" button that opens that folder in the OS file manager - adding/removing a template is just adding/removing a file there directly, not a separate step in this dialog. See ITemplateService for why a template's content is never read here at all.</summary>
public sealed partial class TemplatesDialogViewModel : ViewModelBase
{
    private readonly ITemplateService templateService;
    private readonly IExternalOpenService externalOpenService;

    public TemplatesDialogViewModel(ITemplateService templateService, IExternalOpenService externalOpenService, bool canApply)
    {
        this.templateService = templateService;
        this.externalOpenService = externalOpenService;
        CanApply = canApply;
        Refresh();
    }

    public ObservableCollection<WorkspaceTemplate> Templates { get; } = [];

    /// <summary>False when no workspace is currently open - Apply has nothing to scaffold then, so it's hidden entirely (see TemplatesDialogWindow.axaml) rather than shown disabled.</summary>
    public bool CanApply { get; }

    /// <summary>Non-null argument is the template that was applied (name plus its file path); null means the dialog was closed (Close button, or the window's own chrome) without applying one.</summary>
    public event Action<AppliedTemplate?>? RequestClose;

    private void Refresh()
    {
        Templates.Clear();
        foreach (WorkspaceTemplate template in templateService.GetTemplates())
        {
            Templates.Add(template);
        }
    }

    /// <summary>Opens the templates folder in the OS file manager - the only way to add/remove a template, since the folder's own contents are the whole registry (see ITemplateService). Reopen this dialog afterward to see a template added/removed there.</summary>
    [RelayCommand]
    private void OpenTemplatesFolder() => externalOpenService.OpenFolder(templateService.GetTemplatesDirectory());

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply(WorkspaceTemplate template) => RequestClose?.Invoke(new AppliedTemplate { Name = template.Name, Path = template.Path });

    [RelayCommand]
    private void Close() => RequestClose?.Invoke(null);
}
