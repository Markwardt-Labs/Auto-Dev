using System.Text.Json;
using AutoDev.Core.Models;
using AutoDev.Core.Serialization;

namespace AutoDev.Core.Services;

public sealed class JsonSettingsService : ISettingsService
{
    private readonly string settingsFilePath;
    private readonly AtomicJsonFile atomicJsonFile = new();

    public JsonSettingsService()
    {
        string appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
            "AutoDev");
        Directory.CreateDirectory(appDataDir);
        settingsFilePath = Path.Combine(appDataDir, "settings.json");
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsFilePath))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(settingsFilePath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, AppJson.Options, cancellationToken)
                   ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Atomic (see AtomicJsonFile) - settings.json is shared by every running AutoDev instance, and one reading a half-written file would fall back to defaults and then save those over everything.</summary>
    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        await atomicJsonFile.WriteAsync(settingsFilePath, settings, cancellationToken);
}
