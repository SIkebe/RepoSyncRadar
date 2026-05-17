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
            _scoredShas.RemoveWhere(sha => !_positions.ContainsKey(sha));
            var completed = _scoredShas.Count;

            _progress?.Report(_total == 0
                ? "未読コミットはありません。スコアリング対象 0 / 0 件。"
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"未読コミットのスコアリング対象: 全 {_total} 件 ({completed} / {_total})"));
        }

        public void ReportScoreSaved(string sha)
        {
            if (_disposed)
            {
                return;
            }

            if (!_scoredShas.Add(sha))
            {
                return;
            }

            var completed = _scoredShas.Count;
            var totalText = _total is int total
                ? total.ToString(CultureInfo.InvariantCulture)
                : "?";
            var shortSha = sha.Length <= 8 ? sha : sha[..8];
            var prefix = _total is int knownTotal && completed >= knownTotal
                ? "未読コミットのスコアリング完了"
                : "未読コミットをスコアリング中";

            _progress?.Report(string.Create(
                CultureInfo.InvariantCulture,
                $"{prefix}: {completed} / {totalText} 件目 ({shortSha})"));
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