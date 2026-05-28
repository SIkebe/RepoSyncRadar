using System.Globalization;
using System.Runtime.CompilerServices;

namespace RepoSyncRadar.App.Tests;

/// <summary>
/// Locks the test process to <c>ja-JP</c> at module load so bUnit renders of
/// components that use <see cref="Microsoft.Extensions.Localization.IStringLocalizer{T}"/>
/// resolve the Japanese <c>SharedResource.resx</c> (the app's neutral culture)
/// regardless of the host OS culture. Without this, en-US CI runners fall back to
/// <c>SharedResource.en.resx</c> and tests asserting Japanese substrings fail.
/// </summary>
internal static class TestCultureInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var ja = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentCulture = ja;
        CultureInfo.DefaultThreadCurrentUICulture = ja;
        CultureInfo.CurrentCulture = ja;
        CultureInfo.CurrentUICulture = ja;
    }
}
