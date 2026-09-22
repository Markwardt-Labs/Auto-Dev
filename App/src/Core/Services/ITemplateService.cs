using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

/// <summary>Scaffolding templates - plain `.md` files, each describing how a workspace should be organized/configured, that live directly inside GetTemplatesDirectory() (see TemplatesDialogViewModel). The directory itself is the whole registry: adding or removing a template is just adding or removing a file there (e.g. via the Templates dialog's "Open Templates" button), not a separate register/unregister step. A template's own content is never read by this app at all - the AI reads it itself, from its own Path, at the moment it's applied (see VersionSectionViewModel.BuildTemplateInstruction), so editing the file takes effect immediately with nothing to go stale.</summary>
public interface ITemplateService
{
    /// <summary>Every `.md` file directly inside GetTemplatesDirectory(), alphabetical by name - plain directory enumeration, same as IFileTreeService.GetChildren, so no Task wrapper for what's never really asynchronous work.</summary>
    IReadOnlyList<WorkspaceTemplate> GetTemplates();

    /// <summary>Where every template lives - `Templates` under AutoDev's own app data folder, created if it doesn't exist yet.</summary>
    string GetTemplatesDirectory();
}
