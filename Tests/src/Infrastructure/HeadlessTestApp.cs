using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;

namespace AutoDev.Tests.Infrastructure;

/// <summary>Minimal Avalonia Application for headless View-level tests (see ScriptTabViewTests) - just enough (FluentTheme) for a real control's own default template to build, without the real App's own DI-composed OnFrameworkInitializationCompleted.</summary>
public sealed class HeadlessTestApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
}

/// <summary>
/// Runs every headless View test's actual Avalonia work on one dedicated background thread, set up exactly once
/// per test process - plain Avalonia.Headless rather than the Avalonia.Headless.XUnit glue package, which pulls
/// in xunit.v3.core and conflicts with this project's xunit v2 test suite. Avalonia's dispatcher is a single
/// static instance owned by whichever one OS thread first sets it up; xUnit's own thread-pool-based test
/// execution has no guarantee any two test methods (running in different classes, or even the same one) land on
/// the same thread, so touching a control directly from a test method's own thread intermittently throws "the
/// calling thread cannot access this object because a different thread owns it". A dedicated thread actually
/// running Dispatcher.UIThread.MainLoop (not just SetupWithoutStarting with nothing pumping it) is required too,
/// not merely a shared thread on which Setup happened to run once - RunOnUiThread's own Dispatcher.UIThread.
/// Invoke call, made from an xUnit worker thread, would otherwise block forever with nothing consuming its queue.
/// </summary>
public static class TestAppBuilder
{
    private static readonly Lazy<object> uiThread = new(StartUiThread);

    private static object StartUiThread()
    {
        using ManualResetEventSlim ready = new();
        Thread thread = new(() =>
        {
            AppBuilder.Configure<HeadlessTestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
            ready.Set();
            Dispatcher.UIThread.MainLoop(CancellationToken.None);
        })
        {
            IsBackground = true,
            Name = "AvaloniaHeadlessTestUiThread",
        };
        thread.Start();
        ready.Wait();
        return thread;
    }

    /// <summary>Marshals test onto the dedicated Avalonia UI thread (starting it first if this is the very first call) and blocks until it completes - the one safe way for a test to touch any Avalonia control, regardless of which thread xUnit itself invoked the test method on.</summary>
    public static void RunOnUiThread(Action test)
    {
        _ = uiThread.Value;
        Dispatcher.UIThread.Invoke(test);
    }

    /// <summary>
    /// Same as RunOnUiThread, for a test body that itself needs to await something - e.g. a ViewModel method
    /// whose own continuation is posted back to Dispatcher.UIThread (EditTabViewModel's off-thread Mermaid
    /// render is one real example). Calling .GetAwaiter().GetResult() on an async test body from INSIDE
    /// RunOnUiThread's own Dispatcher.UIThread.Invoke would deadlock - that continuation needs the UI thread's
    /// own message loop to actually run in order to be posted back and complete, which a synchronous block
    /// from within that very loop prevents forever. InvokeAsync avoids this: called from the xUnit worker
    /// thread (never itself inside an Invoke), it lets the dedicated UI thread's MainLoop keep pumping while
    /// this awaits the result.
    /// </summary>
    public static void RunOnUiThread(Func<Task> test)
    {
        _ = uiThread.Value;
        Dispatcher.UIThread.InvokeAsync(test).GetAwaiter().GetResult();
    }
}
