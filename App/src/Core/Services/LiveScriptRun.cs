using System.ComponentModel;
using System.Text;

namespace AutoDev.Core.Services;

/// <summary>
/// One .cs file's currently in-flight run, updated as `dotnet run --file`'s stdout/stderr streams in - the live
/// counterpart to a persisted ScriptRunRecord, bound to directly by a viewer (the Script tab) rather than the
/// viewer re-buffering its output itself. Built and mutated exclusively by WorkspaceScriptRunnerService.
/// </summary>
public sealed class LiveScriptRun : INotifyPropertyChanged
{
    /// <summary>
    /// How often AppendText raises OutputText's own PropertyChanged at most, coalescing any faster-arriving
    /// chunks in between into the next one. A chatty script's stdout/stderr can each independently produce a
    /// new chunk (see WorkspaceScriptRunnerService.PumpAsync, one instance per stream) far faster than a UI
    /// bound straight to this string can keep up with once its output is a few thousand lines long: replacing
    /// the whole bound Text re-lays out the *entire* accumulated document from scratch (Avalonia's TextBlock/
    /// SelectableTextBlock has no incremental reflow), so notifying once per raw chunk makes each individual
    /// update's cost scale with the total output so far - the more a script has already printed, the slower
    /// every subsequent line becomes, which is exactly the "gets slow with long output" symptom this exists to
    /// avoid. Throttling to a fixed wall-clock rate bounds the number of full relayouts to roughly the run's
    /// own duration divided by this interval, regardless of how many or how small the underlying chunks are.
    /// </summary>
    private static readonly TimeSpan notifyInterval = TimeSpan.FromMilliseconds(100);

    private readonly StringBuilder output = new();
    private readonly Lock gate = new();
    private Stream? standardInput;
    private DateTime nextNotifyAtUtc = DateTime.MinValue;
    private bool notifyScheduled;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>False once `dotnet run` has exited (or been killed) - see WorkspaceScriptRunnerService.RunAndTrackAsync.</summary>
    public bool IsRunning { get; private set; } = true;

    public string OutputText
    {
        get
        {
            lock (gate)
            {
                return output.ToString();
            }
        }
    }

    /// <summary>
    /// Appends text exactly as given - no added newline, no line-boundary assumption of any kind. Called with
    /// whatever raw chunk of decoded text `dotnet run`'s stdout/stderr streams produced next (see
    /// WorkspaceScriptRunnerService.PumpAsync - stdout and stderr each pump independently, so two calls can
    /// race here), which may be a partial line with no trailing newline at all (e.g. an interactive prompt
    /// written via Console.Write, deliberately left unterminated so the read it's prompting for appears on the
    /// same line) - appending only ever on a newline boundary would leave a prompt like that invisible until
    /// some later line happened to flush it into view, well after it was actually written and the script was
    /// already sitting there blocked waiting to read it. The text itself is always appended immediately; only
    /// the resulting PropertyChanged notification is throttled - see notifyInterval.
    /// </summary>
    internal void AppendText(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        bool notifyNow;
        TimeSpan delay = default;
        lock (gate)
        {
            output.Append(text);

            DateTime now = DateTime.UtcNow;
            notifyNow = now >= nextNotifyAtUtc;
            if (notifyNow)
            {
                nextNotifyAtUtc = now + notifyInterval;
            }
            else if (!notifyScheduled)
            {
                notifyScheduled = true;
                delay = nextNotifyAtUtc - now;
            }
        }

        if (notifyNow)
        {
            RaiseOutputTextChanged();
        }
        else if (delay > TimeSpan.Zero)
        {
            // Guarantees a trailing flush even if output then goes quiet mid-window - otherwise whatever was
            // appended right after the last immediate notify would stay invisible until some later chunk
            // happened to arrive, which might be much later (or, for a script that's about to go silent for a
            // while, effectively never until it produces more output).
            _ = FlushAfterDelayAsync(delay);
        }
    }

    private async Task FlushAfterDelayAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        lock (gate)
        {
            notifyScheduled = false;
            nextNotifyAtUtc = DateTime.UtcNow + notifyInterval;
        }

        RaiseOutputTextChanged();
    }

    private void RaiseOutputTextChanged() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OutputText)));

    /// <summary>Captures the process's own real stdin stream once CliWrap's PipeSource hands it over - see WorkspaceScriptRunnerService.RunAndTrackAsync. Null until then, so SendInputAsync is a no-op for the brief window before the process has actually started.</summary>
    internal void AttachStandardInput(Stream stream) => standardInput = stream;

    internal void MarkFinished()
    {
        // Only ever called once every pump loop has already fully drained (CliWrap's ExecuteAsync doesn't
        // complete until both pipe delegates do), so this is always the true final text - flushed immediately
        // rather than possibly sitting behind a still-open throttle window that nothing will ever tick again
        // now that output has genuinely stopped for good.
        RaiseOutputTextChanged();
        IsRunning = false;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
    }

    /// <summary>
    /// Writes a line of text straight to the running process's own stdin - for a script that calls
    /// Console.ReadLine() (or reads stdin directly) and would otherwise hang forever, since nothing else in
    /// AutoDev ever supplies input to it. Echoed into OutputText first (only once the write itself succeeds) -
    /// piping directly to a subprocess's stdin, unlike a real terminal, never echoes what was typed on its
    /// own, so this stands in for the terminal's own local echo: the typed text appended exactly as given,
    /// right where the cursor already was (immediately after an unterminated prompt, on the very same line,
    /// with no prefix of any kind), followed by a newline for the Enter that submitted it - indistinguishable
    /// from what actually typing it into a real terminal would have shown. A silent no-op if the process has
    /// already exited or its stdin pipe is otherwise unusable - there is no other output to report that
    /// against.
    /// </summary>
    public async Task SendInputAsync(string text)
    {
        if (standardInput is null)
        {
            return;
        }

        try
        {
            await standardInput.WriteAsync(Encoding.UTF8.GetBytes(text + Environment.NewLine));
            await standardInput.FlushAsync();
            AppendText(text + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
