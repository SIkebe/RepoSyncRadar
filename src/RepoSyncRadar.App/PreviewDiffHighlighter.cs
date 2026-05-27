using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Web.WebView2.Wpf;

namespace RepoSyncRadar.App;

internal enum PreviewDiffPane
{
    Before,
    After,
}

internal sealed record PreviewDiffBlock(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("text")] string Text);

internal sealed record PreviewDiffPlan(
    IReadOnlyList<int> BeforeChangedIndexes,
    IReadOnlyList<int> AfterChangedIndexes);

internal static class PreviewDiffHighlighter
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan _extractionRetryDelay = TimeSpan.FromMilliseconds(250);

    private const int _maxExtractionAttempts = 6;

    internal const string ExtractBlocksScriptForTests = """
(() => {
  const leafSelector = 'h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,td,th,.ghd-markdown-alert';
  const blockedAncestorSelector = 'nav,header,footer,aside,[role="navigation"]';
  const root =
    document.querySelector('main article') ||
    document.querySelector('article') ||
    document.querySelector('[data-testid="article-body"]') ||
    document.querySelector('main') ||
    document.body;
  if (!root) {
    return [];
  }

  document.querySelectorAll('[data-rsr-diff-index]').forEach((element) => {
    element.removeAttribute('data-rsr-diff-index');
  });

  const normalize = (value) => (value || '').replace(/\s+/g, ' ').trim();
  const isNavigationOrChrome = (element) => {
    if (element.closest(blockedAncestorSelector)) {
      return true;
    }
    const ariaLabel = (element.getAttribute('aria-label') || '').toLowerCase();
    return ariaLabel.includes('breadcrumb') || ariaLabel.includes('table of contents');
  };

  const elements = Array.from(root.querySelectorAll(leafSelector))
    .filter((element) => !isNavigationOrChrome(element))
    .filter((element) => {
      const text = normalize(element.innerText || element.textContent);
      if (text.length < 2) {
        return false;
      }
      return element.classList.contains('ghd-markdown-alert') || !element.querySelector(leafSelector);
    });

  return elements.map((element, index) => {
    element.setAttribute('data-rsr-diff-index', String(index));
    return { index, text: normalize(element.innerText || element.textContent) };
  });
})();
""";

    internal static async Task<IReadOnlyList<PreviewDiffBlock>> ExtractBlocksAsync(WebView2CompositionControl view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.CoreWebView2 is null)
        {
            return Array.Empty<PreviewDiffBlock>();
        }

        for (var attempt = 0; attempt < _maxExtractionAttempts; attempt++)
        {
          var scriptResult = await view.ExecuteScriptAsync(ExtractBlocksScriptForTests);
            var blocks = DeserializeBlocks(scriptResult);
          if (blocks.Count > 0 || attempt == _maxExtractionAttempts - 1)
            {
                return blocks;
            }

          await Task.Delay(_extractionRetryDelay);
        }

        return Array.Empty<PreviewDiffBlock>();
    }

    internal static PreviewDiffPlan BuildPlan(
        IReadOnlyList<PreviewDiffBlock> beforeBlocks,
        IReadOnlyList<PreviewDiffBlock> afterBlocks)
    {
        ArgumentNullException.ThrowIfNull(beforeBlocks);
        ArgumentNullException.ThrowIfNull(afterBlocks);

        if (beforeBlocks.Count == 0 || afterBlocks.Count == 0)
        {
            return new PreviewDiffPlan(
                beforeBlocks.Select(static block => block.Index).ToArray(),
                afterBlocks.Select(static block => block.Index).ToArray());
        }

        var beforeTexts = beforeBlocks.Select(block => NormalizeText(block.Text)).ToArray();
        var afterTexts = afterBlocks.Select(block => NormalizeText(block.Text)).ToArray();
        var lengths = new int[beforeTexts.Length + 1, afterTexts.Length + 1];

        for (var beforeIndex = beforeTexts.Length - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterTexts.Length - 1; afterIndex >= 0; afterIndex--)
            {
                lengths[beforeIndex, afterIndex] = string.Equals(
                    beforeTexts[beforeIndex],
                    afterTexts[afterIndex],
                    StringComparison.Ordinal)
                        ? lengths[beforeIndex + 1, afterIndex + 1] + 1
                        : Math.Max(lengths[beforeIndex + 1, afterIndex], lengths[beforeIndex, afterIndex + 1]);
            }
        }

        var beforeChanged = new List<int>();
        var afterChanged = new List<int>();
        var beforeCursor = 0;
        var afterCursor = 0;
        while (beforeCursor < beforeTexts.Length && afterCursor < afterTexts.Length)
        {
            if (string.Equals(beforeTexts[beforeCursor], afterTexts[afterCursor], StringComparison.Ordinal))
            {
                beforeCursor++;
                afterCursor++;
                continue;
            }

            if (lengths[beforeCursor + 1, afterCursor] >= lengths[beforeCursor, afterCursor + 1])
            {
                beforeChanged.Add(beforeBlocks[beforeCursor].Index);
                beforeCursor++;
            }
            else
            {
                afterChanged.Add(afterBlocks[afterCursor].Index);
                afterCursor++;
            }
        }

        while (beforeCursor < beforeBlocks.Count)
        {
            beforeChanged.Add(beforeBlocks[beforeCursor].Index);
            beforeCursor++;
        }

        while (afterCursor < afterBlocks.Count)
        {
            afterChanged.Add(afterBlocks[afterCursor].Index);
            afterCursor++;
        }

        return new PreviewDiffPlan(beforeChanged, afterChanged);
    }

    internal static async Task ApplyPlanAsync(
        WebView2CompositionControl view,
        IReadOnlyList<int> changedIndexes,
        PreviewDiffPane pane)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(changedIndexes);
        if (view.CoreWebView2 is null)
        {
            return;
        }

        var indexesJson = JsonSerializer.Serialize(changedIndexes, _jsonOptions);
        var paneJson = JsonSerializer.Serialize(pane == PreviewDiffPane.Before ? "before" : "after", _jsonOptions);
        var script = BuildApplyPlanScript(indexesJson, paneJson);

        await view.ExecuteScriptAsync(script);
    }

    internal static string BuildApplyPlanScript(string indexesJson, string paneJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(paneJson);

        return $$"""
(() => {
  const styleId = 'rsr-preview-diff-style';
  if (!document.getElementById(styleId)) {
    const style = document.createElement('style');
    style.id = styleId;
    style.textContent = `
.rsr-preview-diff-block {
  position: relative !important;
  border-radius: 4px !important;
  padding: 0.14rem 0.45rem 0.14rem 0.65rem !important;
  margin-left: -0.65rem !important;
  color: inherit !important;
}
.ghd-markdown-alert.rsr-preview-diff-block {
  display: block !important;
  margin-left: 0 !important;
  padding: 8px 0 8px 14px !important;
}
.rsr-preview-diff-before {
  background-color: rgba(207, 34, 46, 0.18) !important;
  border-left: 4px solid #f85149 !important;
  box-shadow: inset 0 0 0 1px rgba(248, 81, 73, 0.28) !important;
  text-decoration-line: line-through !important;
  text-decoration-color: rgba(248, 81, 73, 0.85) !important;
  text-decoration-thickness: 1.2px !important;
  text-decoration-skip-ink: none !important;
}
.rsr-preview-diff-after {
  background-color: rgba(35, 134, 54, 0.22) !important;
  border-left: 4px solid #3fb950 !important;
  box-shadow: inset 0 0 0 1px rgba(63, 185, 80, 0.28) !important;
}
.rsr-preview-diff-scrollbar {
  bottom: 0 !important;
  pointer-events: none !important;
  position: fixed !important;
  right: 24px !important;
  top: 0 !important;
  width: 10px !important;
  z-index: 2147483647 !important;
}
.rsr-preview-diff-scrollbar-marker {
  border-radius: 999px !important;
  box-shadow: 0 0 0 1px rgba(255,255,255,0.7), 0 1px 3px rgba(0,0,0,0.25) !important;
  min-height: 4px !important;
  position: absolute !important;
  right: 0 !important;
  width: 10px !important;
}
.rsr-preview-diff-scrollbar-marker-before {
  background: #f85149 !important;
}
.rsr-preview-diff-scrollbar-marker-after {
  background: #3fb950 !important;
}
`;
    document.head.appendChild(style);
  }

  document.querySelectorAll('.rsr-preview-diff-block').forEach((element) => {
    element.classList.remove('rsr-preview-diff-block', 'rsr-preview-diff-before', 'rsr-preview-diff-after');
  });

  const changedIndexes = new Set({{indexesJson}});
  const pane = {{paneJson}};
  changedIndexes.forEach((index) => {
    const element = document.querySelector(`[data-rsr-diff-index="${index}"]`);
    if (!element) {
      return;
    }
    element.classList.add(
      'rsr-preview-diff-block',
      pane === 'before' ? 'rsr-preview-diff-before' : 'rsr-preview-diff-after');
  });

  const markerRootId = 'rsr-preview-diff-scrollbar';
  document.getElementById(markerRootId)?.remove();
  if (changedIndexes.size > 0) {
    const root = document.scrollingElement || document.documentElement || document.body;
    const maxScrollTop = Math.max(1, root.scrollHeight - window.innerHeight);
    const rail = document.createElement('div');
    rail.id = markerRootId;
    rail.className = 'rsr-preview-diff-scrollbar';
    changedIndexes.forEach((index) => {
      const element = document.querySelector(`[data-rsr-diff-index="${index}"]`);
      if (!element) {
        return;
      }
      const marker = document.createElement('div');
      marker.className = `rsr-preview-diff-scrollbar-marker ${pane === 'before' ? 'rsr-preview-diff-scrollbar-marker-before' : 'rsr-preview-diff-scrollbar-marker-after'}`;
      const rect = element.getBoundingClientRect();
      const top = Math.max(0, Math.min(1, (rect.top + window.scrollY) / maxScrollTop));
      const height = Math.max(4, Math.min(window.innerHeight, (rect.height / maxScrollTop) * window.innerHeight));
      const markerTop = Math.max(0, Math.min(window.innerHeight - height, top * window.innerHeight));
      marker.style.top = `${markerTop.toFixed(1)}px`;
      marker.style.height = `${height.toFixed(1)}px`;
      rail.appendChild(marker);
    });
    document.body.appendChild(rail);
  }

  const firstChanged = document.querySelector('.rsr-preview-diff-block');
  if (firstChanged) {
    firstChanged.scrollIntoView({ behavior: 'smooth', block: 'center' });
  }

  return changedIndexes.size;
})();
""";
    }

    private static IReadOnlyList<PreviewDiffBlock> DeserializeBlocks(string? scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult)
            || string.Equals(scriptResult, "null", StringComparison.Ordinal))
        {
            return Array.Empty<PreviewDiffBlock>();
        }

        try
        {
          return JsonSerializer.Deserialize<List<PreviewDiffBlock>>(scriptResult, _jsonOptions) is { } blocks
            ? blocks
            : Array.Empty<PreviewDiffBlock>();
        }
        catch (JsonException)
        {
            return Array.Empty<PreviewDiffBlock>();
        }
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasWhitespace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
                continue;
            }

            builder.Append(character);
            previousWasWhitespace = false;
        }

        return builder.ToString().Trim();
    }
}