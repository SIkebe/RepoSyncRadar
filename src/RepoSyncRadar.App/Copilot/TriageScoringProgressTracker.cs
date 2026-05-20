using System.Globalization;

namespace RepoSyncRadar.App.Copilot;

/// <summary>
/// Bridges Copilot tool calls back to the Morning Triage progress UI.
/// </summary>
public sealed class TriageScoringProgressTracker
{
    private readonly object _gate = new();
    private ActiveScope? _activeScope;

    public IDisposable Begin(IProgress<string>? progress)
    {
        var scope = new ActiveScope(this, progress);
        lock (_gate)
        {
            _activeScope = scope;
        }

        return scope;
    }

    internal void ReportCommitList(IReadOnlyList<string> shas)
    {
        ArgumentNullException.ThrowIfNull(shas);
        lock (_gate)
        {
            _activeScope?.ReportCommitList(shas);
        }
    }

    internal void ReportScoreSaved(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return;
        }

        lock (_gate)
        {
            _activeScope?.ReportScoreSaved(sha.Trim());
        }
    }

    internal void ReportAnalysisStarted(string sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return;
        }

        lock (_gate)
        {
            _activeScope?.ReportAnalysisStarted(sha.Trim());
        }
    }

    private void End(ActiveScope scope)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeScope, scope))
            {
                _activeScope = null;
            }
        }
    }

    private sealed class ActiveScope : IDisposable
    {
        private readonly TriageScoringProgressTracker _owner;
        private readonly IProgress<string>? _progress;
        private readonly HashSet<string> _analysisStartedShas = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _scoredShas = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int> _positions = new(StringComparer.OrdinalIgnoreCase);
        private int? _total;
        private bool _disposed;

        public ActiveScope(TriageScoringProgressTracker owner, IProgress<string>? progress)
        {
            _owner = owner;
            _progress = progress;
        }

        public void ReportCommitList(IReadOnlyList<string> shas)
        {
            if (_disposed)
            {
                return;
            }

            if (_total is not null)
            {
                return;
            }

            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var sha in shas)
            {
                if (string.IsNullOrWhiteSpace(sha) || positions.ContainsKey(sha))
                {
                    continue;
                }

                positions[sha] = positions.Count + 1;
            }

            _positions = positions;
            _total = positions.Count;
            _analysisStartedShas.RemoveWhere(sha => !_positions.ContainsKey(sha));
            _scoredShas.RemoveWhere(sha => !_positions.ContainsKey(sha));

            _progress?.Report(_total == 0
                ? "今回の未スコア未確認コミットはありません。スコアリング対象 0 / 0 件。"
                : BuildProgressMessage("今回の未スコア未確認コミットをスコアリング中"));
        }

        public void ReportAnalysisStarted(string sha)
        {
            if (_disposed)
            {
                return;
            }

            if (!_analysisStartedShas.Add(sha))
            {
                return;
            }

            _progress?.Report(BuildProgressMessage("今回の未スコア未確認コミットを分析中", sha));
        }

        public void ReportScoreSaved(string sha)
        {
            if (_disposed)
            {
                return;
            }

            _analysisStartedShas.Add(sha);
            if (!_scoredShas.Add(sha))
            {
                return;
            }

            var prefix = _total is int targetTotal && ProgressCount(_scoredShas.Count) >= targetTotal
                ? "今回の未スコア未確認コミットのスコアリング完了"
                : "今回の未スコア未確認コミットをスコアリング中";

            _progress?.Report(BuildProgressMessage(prefix, sha));
        }

        private string BuildProgressMessage(string prefix, string? sha = null)
        {
            var analysisStarted = ProgressCount(_analysisStartedShas.Count);
            var scoreSaved = ProgressCount(_scoredShas.Count);
            var totalText = _total is int total
                ? total.ToString(CultureInfo.InvariantCulture)
                : "?";
            var suffix = string.IsNullOrWhiteSpace(sha)
                ? string.Empty
                : string.Create(CultureInfo.InvariantCulture, $" ({ShortSha(sha)})");

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}: 対象 {totalText} 件 / 分析 {analysisStarted} / {totalText} 件 / スコア保存 {scoreSaved} / {totalText} 件{suffix}");
        }

        private int ProgressCount(int count)
        {
            return _total is int total
                ? Math.Min(count, total)
                : count;
        }

        private static string ShortSha(string sha)
        {
            return sha.Length <= 8 ? sha : sha[..8];
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.End(this);
        }
    }
}