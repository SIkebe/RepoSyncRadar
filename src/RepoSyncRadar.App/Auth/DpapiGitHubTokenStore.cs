using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace RepoSyncRadar.App.Auth;

/// <summary>
/// Persists the GitHub user token to <c>%LOCALAPPDATA%\RepoSyncRadar\github-token.bin</c>
/// encrypted with Windows DPAPI (CurrentUser scope). The cipher text can only be
/// decrypted by the same Windows account that wrote it, so copying the file to
/// another machine simply yields a fresh sign-in prompt.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DpapiGitHubTokenStore : IGitHubTokenStore
{
    private const string _defaultFileName = "github-token.bin";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly ILogger<DpapiGitHubTokenStore> _logger;

    public DpapiGitHubTokenStore(ILogger<DpapiGitHubTokenStore> logger)
        : this(ResolveDefaultPath(), logger)
    {
    }

    internal DpapiGitHubTokenStore(string path, ILogger<DpapiGitHubTokenStore> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);

        _path = path;
        _logger = logger;
    }

    public async Task<StoredGitHubToken?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        byte[] cipher;
        try
        {
            cipher = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            LogReadFailed(_logger, _path, ex);
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredGitHubToken>(plain, _jsonOptions);
        }
        catch (CryptographicException ex)
        {
            LogDecryptFailed(_logger, _path, ex);
            return null;
        }
        catch (JsonException ex)
        {
            LogParseFailed(_logger, _path, ex);
            return null;
        }
    }

    public async Task SaveAsync(StoredGitHubToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(token, _jsonOptions);
        var cipher = ProtectedData.Protect(plain, optionalEntropy: null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_path, cipher, cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch (IOException ex)
        {
            LogClearFailed(_logger, _path, ex);
        }

        return Task.CompletedTask;
    }

    private static string ResolveDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RepoSyncRadar",
        _defaultFileName);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Failed to read GitHub token file {Path}; treating as signed out.")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Failed to decrypt GitHub token file {Path} (DPAPI). User must sign in again.")]
    private static partial void LogDecryptFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Failed to parse GitHub token JSON from {Path}.")]
    private static partial void LogParseFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Failed to delete GitHub token file {Path}.")]
    private static partial void LogClearFailed(ILogger logger, string path, Exception exception);
}
