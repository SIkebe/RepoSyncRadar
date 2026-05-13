using Microsoft.Extensions.Options;

namespace RepoSyncRadar.Core.Options;

/// <summary>
/// Validates that <see cref="DocsApiOptions.BaseAddress"/> is an absolute HTTPS URI.
/// DataAnnotations <c>[Url]</c> does not apply to <see cref="Uri"/> properties, so this
/// covers the scheme check that cannot be expressed declaratively.
/// </summary>
internal sealed class DocsApiOptionsValidator : IValidateOptions<DocsApiOptions>
{
    public ValidateOptionsResult Validate(string? name, DocsApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var builder = new ValidateOptionsResultBuilder();

        if (options.BaseAddress is null)
        {
            builder.AddError("BaseAddress is required.", nameof(options.BaseAddress));
        }
        else
        {
            if (!options.BaseAddress.IsAbsoluteUri)
            {
                builder.AddError(
                    "BaseAddress must be an absolute URI.",
                    nameof(options.BaseAddress));
            }
            else if (!string.Equals(options.BaseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                builder.AddError(
                    "BaseAddress must use the https scheme.",
                    nameof(options.BaseAddress));
            }
        }

        return builder.Build();
    }
}
