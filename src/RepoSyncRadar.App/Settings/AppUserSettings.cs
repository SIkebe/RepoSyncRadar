namespace RepoSyncRadar.App.Settings;

public sealed record AppUserSettings
{
    public static AppUserSettings Default { get; } = new();

    public DocsThemeMode DefaultDocsTheme { get; init; } = DocsThemeMode.Dark;

    public string DisplayCulture { get; init; } = AppDisplayCulture.DefaultCultureName;
}

public interface IAppUserSettingsStore
{
    AppUserSettings Current { get; }

    event Action<AppUserSettings>? SettingsChanged;

    Task<AppUserSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveDefaultDocsThemeAsync(DocsThemeMode theme, CancellationToken cancellationToken = default);

    Task SaveDisplayCultureAsync(string cultureName, CancellationToken cancellationToken = default);
}