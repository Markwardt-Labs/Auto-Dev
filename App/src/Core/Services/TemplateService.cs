using AutoDev.Core.Models;

namespace AutoDev.Core.Services;

public sealed class TemplateService : ITemplateService
{
    public IReadOnlyList<WorkspaceTemplate> GetTemplates() =>
        [.. Directory.EnumerateFiles(GetTemplatesDirectory(), "*.md")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => new WorkspaceTemplate { Path = path })];

    public string GetTemplatesDirectory()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "AutoDev", "Templates");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
