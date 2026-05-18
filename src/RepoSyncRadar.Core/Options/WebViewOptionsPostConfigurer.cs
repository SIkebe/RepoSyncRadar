using Microsoft.Extensions.Options;

namespace RepoSyncRadar.Core.Options;

/// <summary>Normalizes WebView host allow-list entries for ordinal host matching.</summary>
internal sealed class WebViewOptionsPostConfigurer : IPostConfigureOptions<WebViewOptions>
{
    public void PostConfigure(string? name, WebViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AllowedUrlHosts = CopilotOptionsPostConfigurer.Normalize(
            options.AllowedUrlHosts,
            lowercase: true);
    }
}