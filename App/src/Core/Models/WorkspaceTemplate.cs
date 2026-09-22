namespace AutoDev.Core.Models;

/// <summary>A scaffolding template - a `.md` file directly inside ITemplateService.GetTemplatesDirectory(), which is itself the whole registry (see GetTemplatesAsync). Its content is deliberately never carried on this record - the AI reads the file itself, from Path, at the moment a template is applied (see VersionSectionViewModel.BuildTemplateInstruction), rather than having it pasted into the prompt.</summary>
public sealed record WorkspaceTemplate
{
    public required string Path { get; init; }

    /// <summary>Display name - just the file name without its `.md` extension, since nothing else about a template exists separately from its file.</summary>
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>A template the user just applied from the Templates dialog - its name (for the Generate request card's own display text) alongside its file path (for the AI to read directly - see WorkspaceTemplate's own doc comment).</summary>
public sealed record AppliedTemplate
{
    public required string Name { get; init; }

    public required string Path { get; init; }
}
