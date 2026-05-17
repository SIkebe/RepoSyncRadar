namespace RepoSyncRadar.App.Components;

public sealed record CommitSelectionChange(IReadOnlyList<string> Shas, bool Selected);