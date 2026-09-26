using System.Text.Json;

namespace AutoDev.Core.Serialization;

/// <summary>
/// Writes JSON to a sibling temp file and renames it over the target, so a reader - in this process or another
/// AutoDev instance sharing the same file - never sees a half-written file, and a crash mid-write leaves the
/// previous version intact rather than a truncated one that later loads as empty.
/// </summary>
internal sealed class AtomicJsonFile
{
    private readonly int maxMoveAttempts = 5;
    private readonly TimeSpan moveRetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Serializes `value` with <see cref="AppJson.Options"/> and atomically replaces `path` with it.</summary>
    /// <typeparam name="T">The serialized value's type.</typeparam>
    /// <param name="path">The file to replace; its directory must already exist.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="cancellationToken">Cancels the write; the target is left untouched if cancelled.</param>
    /// <returns>A task that completes once `path` holds the new content.</returns>
    public async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, AppJson.Options, cancellationToken);
            }

            await MoveOverAsync(tempPath, path, cancellationToken);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>Windows refuses to rename over a file another process currently has open for reading, so a brief retry covers a concurrent reader; the rename itself is atomic on every platform.</summary>
    private async Task MoveOverAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(sourcePath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < maxMoveAttempts)
            {
                await Task.Delay(moveRetryDelay, cancellationToken);
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
