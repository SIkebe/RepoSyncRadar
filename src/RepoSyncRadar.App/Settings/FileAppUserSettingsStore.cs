using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoSyncRadar.App.Settings;

public sealed class FileAppUserSettingsStore : IAppUserSettingsStore, IDisposable
{
    internal const string UserSettingsPathEnv = "REPOSYNCRADAR_USER_SETTINGS_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<DocsThemeMode>() },
    };

    private readonly string _settingsPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FileAppUserSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        Current = LoadFromDiskOrDefault(_settingsPath);
    }

    public AppUserSettings Current { get; private set; }

    public event Action<AppUserSettings>? SettingsChanged;

    public static FileAppUserSettingsStore CreateDefault()
        => new(ResolveDefaultSettingsPath());

    public static string ResolveDefaultSettingsPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable(UserSettingsPathEnv);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RepoSyncRadar",
            "settings.json");
    }

    public Task<AppUserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Current);
    }

    public async Task SaveDefaultDocsThemeAsync(DocsThemeMode theme, CancellationToken cancellationToken = default)
    {
        var normalizedTheme = NormalizeTheme(theme);
        AppUserSettings next;
        bool changed;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            next = Current with { DefaultDocsTheme = normalizedTheme };
            changed = !EqualityComparer<AppUserSettings>.Default.Equals(Current, next);
            await SaveToDiskAsync(_settingsPath, next, cancellationToken).ConfigureAwait(false);
            Current = next;
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            SettingsChanged?.Invoke(next);
        }
    }

    public void Dispose()
        => _gate.Dispose();

    private static AppUserSettings LoadFromDiskOrDefault(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return AppUserSettings.Default;
            }

            var json = File.ReadAllText(settingsPath);
            var settings = JsonSerializer.Deserialize<AppUserSettings>(json, JsonOptions);
            return Normalize(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppUserSettings.Default;
        }
    }

    private static async Task SaveToDiskAsync(
        string settingsPath,
        AppUserSettings settings,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        await File.WriteAllTextAsync(settingsPath, json, cancellationToken).ConfigureAwait(false);
    }

    private static AppUserSettings Normalize(AppUserSettings? settings)
        => settings is null
            ? AppUserSettings.Default
            : settings with { DefaultDocsTheme = NormalizeTheme(settings.DefaultDocsTheme) };

    private static DocsThemeMode NormalizeTheme(DocsThemeMode theme)
        => Enum.IsDefined(theme) ? theme : DocsThemeMode.Dark;
}