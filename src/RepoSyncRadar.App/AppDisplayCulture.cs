using System.Globalization;

namespace RepoSyncRadar.App;

internal static class AppDisplayCulture
{
    public const string DefaultCultureName = "ja";

    public static IReadOnlyList<AppDisplayCultureOption> SupportedCultures { get; } =
    [
        new("ja", "日本語"),
        new("en", "English"),
    ];

    public static string NormalizeCultureName(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return DefaultCultureName;
        }

        try
        {
            var requestedCulture = CultureInfo.GetCultureInfo(cultureName.Trim());
            var supported = SupportedCultures.FirstOrDefault(option =>
                string.Equals(option.CultureName, requestedCulture.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option.CultureName, requestedCulture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase));
            return supported?.CultureName ?? DefaultCultureName;
        }
        catch (CultureNotFoundException)
        {
            return DefaultCultureName;
        }
    }

    public static CultureInfo Apply(string? cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(NormalizeCultureName(cultureName));
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        return culture;
    }
}

internal sealed record AppDisplayCultureOption(string CultureName, string NativeLabel);
