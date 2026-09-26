using System.Text.Json;
using AutoDev.Core.Models;
using AutoDev.Core.Serialization;

namespace AutoDev.Core.Services;

public sealed class WorkspaceMetadataStore : IWorkspaceMetadataStore
{
    private static readonly string metadataDirName = ".autodev";
    private static readonly string localDirName = "local";
    private static readonly string generateSessionsFileName = "generate-sessions.json";
    private static readonly string generateDraftsFileName = "generate-drafts.json";
    private static readonly string generateRequestsFileName = "generate-requests.json";
    private static readonly string scriptRunsDirName = "script-runs";
    private static readonly string taskRunsDirName = "task-runs";

    private readonly AtomicJsonFile atomicJsonFile = new();

    /// <summary>Serializes every load-modify-save of the shared per-workspace dictionary files (sessions, drafts, requests) - several of those saves are fired without awaiting (see GenerateTabViewModel), and two interleaving would otherwise silently drop one of their updates.</summary>
    private readonly SemaphoreSlim dictionaryFileLock = new(1, 1);

    public void EnsureInitialized(string workspacePath)
    {
        Directory.CreateDirectory(MetadataDir(workspacePath));
        Directory.CreateDirectory(LocalDir(workspacePath));
    }

    public async Task AppendScriptRunAsync(string workspacePath, ScriptRunRecord record, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(LocalDir(workspacePath), scriptRunsDirName, SanitizeScriptFolder(record.FilePath));
        Directory.CreateDirectory(dir);
        await atomicJsonFile.WriteAsync(Path.Combine(dir, $"{record.Id}.json"), record, cancellationToken);
    }

    public async Task<List<ScriptRunRecord>> LoadScriptRunsAsync(string workspacePath, string scriptPath, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(LocalDir(workspacePath), scriptRunsDirName, SanitizeScriptFolder(scriptPath));
        List<ScriptRunRecord> records = await LoadRecordsInFolderAsync<ScriptRunRecord>(dir, cancellationToken);
        // Filters by exact FilePath match as a safeguard against a (practically impossible) sanitize collision
        // between two different paths landing in the same folder.
        return [.. records.Where(r => r.FilePath == scriptPath).OrderByDescending(r => r.StartedAt)];
    }

    public async Task<IReadOnlyList<ScriptRef>> LoadRunScriptRefsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(LocalDir(workspacePath), scriptRunsDirName);
        if (!Directory.Exists(root))
        {
            return [];
        }

        Dictionary<string, ScriptRef> refs = new Dictionary<string, ScriptRef>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            List<ScriptRunRecord> records = await LoadRecordsInFolderAsync<ScriptRunRecord>(folder, cancellationToken);
            // Only a standalone (TaskId null) run counts - a script that has only ever run as part of a task
            // is reached exclusively through that task's own dropdown entry, never a top-level one of its own.
            ScriptRunRecord? newest = records.Where(r => r.TaskId is null).OrderByDescending(r => r.StartedAt).FirstOrDefault();
            if (newest is not null)
            {
                refs[newest.FilePath] = new ScriptRef(newest.FilePath, newest.FileName);
            }
        }

        return [.. refs.Values];
    }

    public void DeleteScriptRuns(string workspacePath, string scriptPath)
    {
        string dir = Path.Combine(LocalDir(workspacePath), scriptRunsDirName, SanitizeScriptFolder(scriptPath));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public async Task AppendTaskRunAsync(string workspacePath, TaskRunRecord record, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(LocalDir(workspacePath), taskRunsDirName, SanitizeScriptFolder(record.FilePath));
        Directory.CreateDirectory(dir);
        await atomicJsonFile.WriteAsync(Path.Combine(dir, $"{record.Id}.json"), record, cancellationToken);
    }

    public async Task<List<TaskRunRecord>> LoadTaskRunsAsync(string workspacePath, string taskPath, CancellationToken cancellationToken = default)
    {
        string dir = Path.Combine(LocalDir(workspacePath), taskRunsDirName, SanitizeScriptFolder(taskPath));
        List<TaskRunRecord> records = await LoadRecordsInFolderAsync<TaskRunRecord>(dir, cancellationToken);
        return [.. records.Where(r => r.FilePath == taskPath).OrderByDescending(r => r.StartedAt)];
    }

    public async Task<IReadOnlyList<TaskRunRecord>> LoadRunTaskRefsAsync(string workspacePath, CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(LocalDir(workspacePath), taskRunsDirName);
        if (!Directory.Exists(root))
        {
            return [];
        }

        Dictionary<string, TaskRunRecord> refs = new Dictionary<string, TaskRunRecord>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            List<TaskRunRecord> records = await LoadRecordsInFolderAsync<TaskRunRecord>(folder, cancellationToken);
            TaskRunRecord? newest = records.OrderByDescending(r => r.StartedAt).FirstOrDefault();
            if (newest is not null)
            {
                refs[newest.FilePath] = newest;
            }
        }

        return [.. refs.Values];
    }

    public void DeleteTaskRuns(string workspacePath, string taskPath)
    {
        string dir = Path.Combine(LocalDir(workspacePath), taskRunsDirName, SanitizeScriptFolder(taskPath));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task<List<TRecord>> LoadRecordsInFolderAsync<TRecord>(string dir, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dir))
        {
            return [];
        }

        List<TRecord> records = new List<TRecord>();
        foreach (string file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                await using FileStream stream = File.OpenRead(file);
                TRecord? record = await JsonSerializer.DeserializeAsync<TRecord>(stream, AppJson.Options, cancellationToken);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // skip a corrupt or unreadable run file
            }
        }

        return records;
    }

    /// <summary>Turns a .cs or .task file's workspace-relative path into a filesystem-safe directory name for its run-history folder - replaces path separators and anything outside [A-Za-z0-9._-] with '_'. Opaque but deterministic; the original path is still recorded inside each record, so nothing depends on reversing this.</summary>
    private static string SanitizeScriptFolder(string scriptPath)
    {
        IEnumerable<char> chars = scriptPath.Select(c => c is '/' or '\\' ? '_' : (char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_'));
        return new string(chars.ToArray());
    }

    public async Task<string?> LoadGenerateSessionIdAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> sessions = await LoadStringDictAsync(GenerateSessionsFile(workspacePath), cancellationToken);
        return sessions.TryGetValue(sessionKey, out string? sessionId) ? sessionId : null;
    }

    public async Task SaveGenerateSessionIdAsync(string workspacePath, string sessionKey, string sessionId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(workspacePath);
        await dictionaryFileLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, string> sessions = await LoadStringDictAsync(GenerateSessionsFile(workspacePath), cancellationToken);
            sessions[sessionKey] = sessionId;
            await atomicJsonFile.WriteAsync(GenerateSessionsFile(workspacePath), sessions, cancellationToken);
        }
        finally
        {
            dictionaryFileLock.Release();
        }
    }

    public async Task<string?> LoadGenerateDraftAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, string> drafts = await LoadStringDictAsync(GenerateDraftsFile(workspacePath), cancellationToken);
        return drafts.TryGetValue(sessionKey, out string? draft) ? draft : null;
    }

    public async Task SaveGenerateDraftAsync(string workspacePath, string sessionKey, string draftText, CancellationToken cancellationToken = default)
    {
        await dictionaryFileLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, string> drafts = await LoadStringDictAsync(GenerateDraftsFile(workspacePath), cancellationToken);
            bool changed = string.IsNullOrEmpty(draftText) ? drafts.Remove(sessionKey) : UpdateAndReportChanged(drafts, sessionKey, draftText);
            if (!changed)
            {
                return;
            }

            EnsureInitialized(workspacePath);
            await atomicJsonFile.WriteAsync(GenerateDraftsFile(workspacePath), drafts, cancellationToken);
        }
        finally
        {
            dictionaryFileLock.Release();
        }
    }

    public async Task<List<GenerateRequest>> LoadGenerateRequestsAsync(string workspacePath, string sessionKey, CancellationToken cancellationToken = default)
    {
        Dictionary<string, List<GenerateRequest>> all = await LoadGenerateRequestsDictAsync(workspacePath, cancellationToken);
        return all.TryGetValue(sessionKey, out List<GenerateRequest>? requests) ? requests : [];
    }

    public async Task SaveGenerateRequestsAsync(string workspacePath, string sessionKey, List<GenerateRequest> requests, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(workspacePath);
        await dictionaryFileLock.WaitAsync(cancellationToken);
        try
        {
            Dictionary<string, List<GenerateRequest>> all = await LoadGenerateRequestsDictAsync(workspacePath, cancellationToken);
            all[sessionKey] = requests;
            await atomicJsonFile.WriteAsync(GenerateRequestsFile(workspacePath), all, cancellationToken);
        }
        finally
        {
            dictionaryFileLock.Release();
        }
    }

    private static async Task<Dictionary<string, List<GenerateRequest>>> LoadGenerateRequestsDictAsync(string workspacePath, CancellationToken cancellationToken)
    {
        string path = GenerateRequestsFile(workspacePath);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, List<GenerateRequest>>>(stream, AppJson.Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool UpdateAndReportChanged(Dictionary<string, string> dict, string key, string value)
    {
        if (dict.TryGetValue(key, out string? existing) && existing == value)
        {
            return false;
        }

        dict[key] = value;
        return true;
    }

    private static async Task<Dictionary<string, string>> LoadStringDictAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, AppJson.Options, cancellationToken) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string MetadataDir(string workspacePath) => Path.Combine(workspacePath, metadataDirName);
    private static string LocalDir(string workspacePath) => Path.Combine(MetadataDir(workspacePath), localDirName);
    private static string GenerateSessionsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), generateSessionsFileName);
    private static string GenerateDraftsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), generateDraftsFileName);
    private static string GenerateRequestsFile(string workspacePath) => Path.Combine(LocalDir(workspacePath), generateRequestsFileName);
}
