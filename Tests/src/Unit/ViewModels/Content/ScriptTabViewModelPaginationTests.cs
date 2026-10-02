using AutoDev.ViewModels.Infrastructure;

namespace AutoDev.Tests.ViewModels.Content;

/// <summary>Covers ScriptTabViewModel's output pagination - DisplayedPageText/PageIndex/PageCount/PageLabel/HasMultiplePages and the Previous/Next page commands, including "follow the newest page while output keeps growing" and its interaction with switching entries and starting a new run. Uses clean multiples of outputPageSize (20,000 chars) so expected page counts/lengths stay simple.</summary>
public sealed class ScriptTabViewModelPaginationTests
{
    private readonly Mock<IWorkspaceMetadataStore> metadataStore = new();
    private readonly Mock<IWorkspaceScriptRunner> scriptRunner = new();
    private readonly Mock<IClipboardService> clipboardService = new();
    private readonly Mock<IUiDispatcher> dispatcher = new();
    private readonly ScriptTabViewModel viewModel;

    public ScriptTabViewModelPaginationTests()
    {
        dispatcher.Setup(d => d.Post(It.IsAny<Action>())).Callback<Action>(action => action());
        metadataStore.Setup(s => s.LoadScriptRunsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        viewModel = new ScriptTabViewModel("/workspace", metadataStore.Object, scriptRunner.Object, clipboardService.Object, dispatcher.Object);
    }

    [Fact]
    public void ShortOutput_IsAllOnOnePageWithNoPager()
    {
        viewModel.OutputText = "short output";

        Assert.Equal(1, viewModel.PageCount);
        Assert.Equal(0, viewModel.PageIndex);
        Assert.Equal("short output", viewModel.DisplayedPageText);
        Assert.False(viewModel.HasMultiplePages);
        Assert.False(viewModel.PreviousPageCommand.CanExecute(null));
        Assert.False(viewModel.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public void LongOutput_SplitsIntoPagesAndFollowsTheNewestByDefault()
    {
        viewModel.OutputText = new string('a', 45_000); // 2 full 20,000-char pages + a 5,000-char remainder

        Assert.Equal(3, viewModel.PageCount);
        Assert.Equal(2, viewModel.PageIndex);
        Assert.Equal(5_000, viewModel.DisplayedPageText.Length);
        Assert.True(viewModel.HasMultiplePages);
        Assert.Equal("Page 3 of 3", viewModel.PageLabel);
    }

    [Fact]
    public void PreviousPage_StopsFollowingAndStaysPutAsMoreOutputArrives()
    {
        viewModel.OutputText = new string('a', 45_000);

        viewModel.PreviousPageCommand.Execute(null);

        Assert.Equal(1, viewModel.PageIndex);
        Assert.Equal(20_000, viewModel.DisplayedPageText.Length);
        Assert.True(viewModel.NextPageCommand.CanExecute(null));
        Assert.True(viewModel.PreviousPageCommand.CanExecute(null));

        viewModel.OutputText += new string('b', 10_000);

        Assert.Equal(1, viewModel.PageIndex);
    }

    [Fact]
    public void NextPage_ReachingTheNewestPage_ResumesFollowingIt()
    {
        viewModel.OutputText = new string('a', 45_000);
        viewModel.PreviousPageCommand.Execute(null);
        viewModel.PreviousPageCommand.Execute(null);

        viewModel.NextPageCommand.Execute(null);
        viewModel.NextPageCommand.Execute(null);

        Assert.Equal(2, viewModel.PageIndex);
        Assert.False(viewModel.NextPageCommand.CanExecute(null));

        viewModel.OutputText += new string('b', 20_000);

        Assert.Equal(3, viewModel.PageIndex);
    }

    [Fact]
    public void SwitchingToAnotherScript_ResetsToFollowingItsOwnNewestPage()
    {
        viewModel.OutputText = new string('a', 45_000);
        viewModel.PreviousPageCommand.Execute(null);
        Assert.Equal(1, viewModel.PageIndex);

        viewModel.SelectScript("Scripts/Other.cs", "Other");
        viewModel.OutputText = new string('c', 25_000);

        Assert.Equal(1, viewModel.PageIndex); // page 2 of 2 for the new, unrelated output - not carried over from the old one
        Assert.Equal("Page 2 of 2", viewModel.PageLabel);
    }

    [Fact]
    public void NewRunStarting_ResetsToFollowingTheFreshOutput()
    {
        viewModel.SelectScript("Scripts/Test.cs", "Test");
        viewModel.OutputText = new string('a', 45_000);
        viewModel.PreviousPageCommand.Execute(null);
        Assert.Equal(1, viewModel.PageIndex);

        scriptRunner.Raise(r => r.ScriptRunStarted += null, new ScriptRef("Scripts/Test.cs", "Test"));
        viewModel.OutputText = new string('b', 25_000);

        Assert.Equal(1, viewModel.PageIndex); // page 2 of 2 for the fresh run - not stuck on the previous run's earlier page
        Assert.Equal("Page 2 of 2", viewModel.PageLabel);
    }
}
