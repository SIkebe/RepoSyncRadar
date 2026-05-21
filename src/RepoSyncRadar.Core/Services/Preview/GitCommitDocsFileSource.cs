namespace RepoSyncRadar.Core.Services.Preview;

internal sealed class GitCommitDocsFileSource : IDocsFileSource
{
    private readonly DocsWorktreeManager _worktree;
    private readonly string _commitSha;

    public GitCommitDocsFileSource(DocsWorktreeManager worktree, string commitSha)
    {
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);
        _worktree = worktree;
        _commitSha = commitSha;
    }

    public async Task<string?> ReadTextAsync(string repoPath, CancellationToken cancellationToken)
        => await _worktree.ReadFileTextAsync(_commitSha, repoPath, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> EnumerateFilesAsync(
        string repoDirectory,
        string extension,
        CancellationToken cancellationToken)
        => await _worktree.ListFilesAsync(_commitSha, repoDirectory, extension, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> FindFilesContainingAsync(
        string repoDirectory,
        string text,
        string extension,
        CancellationToken cancellationToken)
        => await _worktree.FindFilesContainingAsync(_commitSha, repoDirectory, text, extension, cancellationToken).ConfigureAwait(false);
}
