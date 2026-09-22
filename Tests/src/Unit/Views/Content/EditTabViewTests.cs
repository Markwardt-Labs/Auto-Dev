using Avalonia.Controls;
using AvaloniaEdit;
using AutoDev.Tests.Infrastructure;
using AutoDev.Views.Content;

namespace AutoDev.Tests.Views.Content;

/// <summary>
/// Covers EditTabView's own EnableTextDragDrop = false setup on its main TextEditor - dragging an
/// already-selected block of text past the editor/window's own bounds starts a native OS drag-and-drop
/// (AvaloniaEdit's built-in "drag selected text to move it" gesture) that hits a known Avalonia X11
/// pointer-capture bug where the drag session never resolves, permanently freezing the whole app since the
/// await blocks the UI thread. Disabling the gesture entirely removes the freeze risk with it.
/// </summary>
public sealed class EditTabViewTests
{
    [Fact]
    public void Editor_TextDragDropIsDisabled() => TestAppBuilder.RunOnUiThread(() =>
    {
        EditTabView view = new();

        TextEditor editor = view.FindControl<TextEditor>("Editor")!;

        Assert.False(editor.Options.EnableTextDragDrop);
    });
}
