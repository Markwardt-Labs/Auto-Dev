using AutoDev.ViewModels.Dialogs;

namespace AutoDev.Tests.ViewModels.Dialogs;

/// <summary>Covers TemplatesDialogViewModel's own logic now that a template's whole identity comes from ITemplateService.GetTemplates() (the templates directory itself is the registry) rather than a separate register/unregister step.</summary>
public sealed class TemplatesDialogViewModelTests
{
    private readonly Mock<ITemplateService> templateService = new();
    private readonly Mock<IExternalOpenService> externalOpenService = new();

    private TemplatesDialogViewModel CreateViewModel(bool canApply = true) =>
        new(templateService.Object, externalOpenService.Object, canApply);

    [Fact]
    public void Constructor_PopulatesTemplatesFromTemplateService()
    {
        templateService.Setup(service => service.GetTemplates()).Returns([
            new WorkspaceTemplate { Path = "/templates/App.md" },
            new WorkspaceTemplate { Path = "/templates/Library.md" },
        ]);

        TemplatesDialogViewModel viewModel = CreateViewModel();

        Assert.Equal(2, viewModel.Templates.Count);
        Assert.Equal("App", viewModel.Templates[0].Name);
        Assert.Equal("Library", viewModel.Templates[1].Name);
    }

    [Fact]
    public void ApplyCommand_RaisesRequestCloseWithNameAndPath()
    {
        templateService.Setup(service => service.GetTemplates()).Returns([]);
        TemplatesDialogViewModel viewModel = CreateViewModel(canApply: true);
        AppliedTemplate? applied = null;
        viewModel.RequestClose += result => applied = result;

        WorkspaceTemplate template = new() { Path = "/templates/Library.md" };
        viewModel.ApplyCommand.Execute(template);

        Assert.NotNull(applied);
        Assert.Equal("Library", applied!.Name);
        Assert.Equal("/templates/Library.md", applied.Path);
    }

    [Fact]
    public void ApplyCommand_CanExecuteFalse_WhenCanApplyIsFalse()
    {
        templateService.Setup(service => service.GetTemplates()).Returns([]);
        TemplatesDialogViewModel viewModel = CreateViewModel(canApply: false);

        Assert.False(viewModel.ApplyCommand.CanExecute(new WorkspaceTemplate { Path = "/templates/Library.md" }));
    }

    [Fact]
    public void OpenTemplatesFolderCommand_OpensTheTemplatesDirectory()
    {
        templateService.Setup(service => service.GetTemplates()).Returns([]);
        templateService.Setup(service => service.GetTemplatesDirectory()).Returns("/templates");
        TemplatesDialogViewModel viewModel = CreateViewModel();

        viewModel.OpenTemplatesFolderCommand.Execute(null);

        externalOpenService.Verify(service => service.OpenFolder("/templates"), Times.Once);
    }

    [Fact]
    public void CloseCommand_RaisesRequestCloseWithNull()
    {
        templateService.Setup(service => service.GetTemplates()).Returns([]);
        TemplatesDialogViewModel viewModel = CreateViewModel();
        bool raised = false;
        AppliedTemplate? applied = new() { Name = "sentinel", Path = "sentinel" };
        viewModel.RequestClose += result =>
        {
            raised = true;
            applied = result;
        };

        viewModel.CloseCommand.Execute(null);

        Assert.True(raised);
        Assert.Null(applied);
    }
}
