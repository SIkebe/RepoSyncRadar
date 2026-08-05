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
    IReadOnlyList<int> AfterChangedIndexes,
    IReadOnlyList<PreviewDiffChange> Changes);

internal sealed record PreviewDiffChange(
    IReadOnlyList<int> BeforeIndexes,
    IReadOnlyList<int> AfterIndexes);

internal sealed record PreviewDiffNavigationTarget(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("navigationIndex")] int NavigationIndex);

internal readonly record struct PreviewDiffNavigationResult(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("ratio")] double Ratio);

internal static class PreviewDiffHighlighter
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan _extractionRetryDelay = TimeSpan.FromMilliseconds(250);

    private const int _maxExtractionAttempts = 6;

    internal const string ExtractBlocksScriptForTests = """
(() => {
  const mediaSelector = 'img,video,audio,iframe,object,embed';
  const blockSelector = 'h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,td,th,.ghd-markdown-alert';
  const leafSelector = `${blockSelector},${mediaSelector}`;
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
  const canonicalizeMediaSource = (value) =>
    normalize(value).replace(
      /\/markdown-assets\/(?:before|after)(?=\/)/,
      '/markdown-assets/shared');
  const describe = (element) => {
    if (element.matches(mediaSelector)) {
      const source =
        element.currentSrc ||
        element.getAttribute('src') ||
        element.getAttribute('poster') ||
        element.getAttribute('alt') ||
        element.outerHTML;
      return `media:${element.tagName.toLowerCase()}:${canonicalizeMediaSource(source)}`;
    }
    return normalize(element.innerText || element.textContent);
  };
  const isVisibleMedia = (element) => {
    if (!element.matches(mediaSelector)) {
      return false;
    }
    const textContainer = element.closest(blockSelector);
    if (textContainer && normalize(textContainer.innerText || textContainer.textContent).length >= 2) {
      return false;
    }
    const style = window.getComputedStyle(element);
    return style.display !== 'none' &&
      style.visibility !== 'hidden' &&
      element.getClientRects().length > 0;
  };
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
      if (element.matches(mediaSelector)) {
        return isVisibleMedia(element);
      }
      const text = describe(element);
      if (text.length < 2) {
        return false;
      }
      return element.classList.contains('ghd-markdown-alert') || !element.querySelector(blockSelector);
    });

  return elements.map((element, index) => {
    element.setAttribute('data-rsr-diff-index', String(index));
    return { index, text: describe(element) };
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
            var beforeIndexes = beforeBlocks.Select(static block => block.Index).ToArray();
            var afterIndexes = afterBlocks.Select(static block => block.Index).ToArray();
            var emptySideChanges = beforeIndexes.Length == 0 && afterIndexes.Length == 0
                ? Array.Empty<PreviewDiffChange>()
                : [new PreviewDiffChange(beforeIndexes, afterIndexes)];
            return new PreviewDiffPlan(
                beforeIndexes,
                afterIndexes,
                emptySideChanges);
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
        var changes = new List<PreviewDiffChange>();
        var currentBeforeIndexes = new List<int>();
        var currentAfterIndexes = new List<int>();
        void FlushChange()
        {
            if (currentBeforeIndexes.Count == 0 && currentAfterIndexes.Count == 0)
            {
                return;
            }

            changes.Add(new PreviewDiffChange(
                currentBeforeIndexes.ToArray(),
                currentAfterIndexes.ToArray()));
            currentBeforeIndexes.Clear();
            currentAfterIndexes.Clear();
        }

        var beforeCursor = 0;
        var afterCursor = 0;
        while (beforeCursor < beforeTexts.Length && afterCursor < afterTexts.Length)
        {
            if (string.Equals(beforeTexts[beforeCursor], afterTexts[afterCursor], StringComparison.Ordinal))
            {
                FlushChange();
                beforeCursor++;
                afterCursor++;
                continue;
            }

            if (lengths[beforeCursor + 1, afterCursor] >= lengths[beforeCursor, afterCursor + 1])
            {
                beforeChanged.Add(beforeBlocks[beforeCursor].Index);
                currentBeforeIndexes.Add(beforeBlocks[beforeCursor].Index);
                beforeCursor++;
            }
            else
            {
                afterChanged.Add(afterBlocks[afterCursor].Index);
                currentAfterIndexes.Add(afterBlocks[afterCursor].Index);
                afterCursor++;
            }
        }

        while (beforeCursor < beforeBlocks.Count)
        {
            beforeChanged.Add(beforeBlocks[beforeCursor].Index);
            currentBeforeIndexes.Add(beforeBlocks[beforeCursor].Index);
            beforeCursor++;
        }

        while (afterCursor < afterBlocks.Count)
        {
            afterChanged.Add(afterBlocks[afterCursor].Index);
            currentAfterIndexes.Add(afterBlocks[afterCursor].Index);
            afterCursor++;
        }
        FlushChange();

        return new PreviewDiffPlan(beforeChanged, afterChanged, changes);
    }

    internal static async Task ApplyPlanAsync(
        WebView2CompositionControl view,
        IReadOnlyList<int> changedIndexes,
        PreviewDiffPane pane,
        IReadOnlyList<PreviewDiffNavigationTarget>? navigationTargets = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(changedIndexes);
        if (view.CoreWebView2 is null)
        {
            return;
        }

        var indexesJson = JsonSerializer.Serialize(changedIndexes, _jsonOptions);
        var paneJson = JsonSerializer.Serialize(pane == PreviewDiffPane.Before ? "before" : "after", _jsonOptions);
        var navigationTargetsJson = JsonSerializer.Serialize(
            navigationTargets ?? Array.Empty<PreviewDiffNavigationTarget>(),
            _jsonOptions);
        var script = BuildApplyPlanScript(indexesJson, paneJson, navigationTargetsJson);

        await view.ExecuteScriptAsync(script);
    }

    internal static string BuildApplyPlanScript(
        string indexesJson,
        string paneJson,
        string navigationTargetsJson = "[]")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(paneJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(navigationTargetsJson);

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

  document.querySelectorAll('.rsr-preview-diff-block,[data-rsr-diff-navigation-index]').forEach((element) => {
    element.classList.remove('rsr-preview-diff-block', 'rsr-preview-diff-before', 'rsr-preview-diff-after');
    element.removeAttribute('data-rsr-diff-navigation-index');
  });

  const changedIndexes = new Set({{indexesJson}});
  const navigationIndexes = new Map(
    {{navigationTargetsJson}}.map((target) => [target.index, target.navigationIndex]));
  const pane = {{paneJson}};
  changedIndexes.forEach((index) => {
    const element = document.querySelector(`[data-rsr-diff-index="${index}"]`);
    if (!element) {
      return;
    }
    element.classList.add(
      'rsr-preview-diff-block',
      pane === 'before' ? 'rsr-preview-diff-before' : 'rsr-preview-diff-after');
    if (navigationIndexes.has(index)) {
      element.setAttribute(
        'data-rsr-diff-navigation-index',
        String(navigationIndexes.get(index)));
    }
  });

  const markerRootId = 'rsr-preview-diff-scrollbar';
  document.getElementById(markerRootId)?.remove();
  if (changedIndexes.size > 0) {
    const root = document.scrollingElement || document.documentElement || document.body;
    const maxScrollTop = Math.max(1, root.scrollHeight - window.innerHeight);
    const rail = document.createElement('div');
    rail.id = markerRootId;
    rail.className = 'rsr-preview-diff-scrollbar';
    const markerGroups = new Map();
    changedIndexes.forEach((index) => {
      const element = document.querySelector(`[data-rsr-diff-index="${index}"]`);
      if (!element) {
        return;
      }
      const navigationIndex = element.getAttribute('data-rsr-diff-navigation-index');
      const groupKey = navigationIndex === null ? `block-${index}` : `hunk-${navigationIndex}`;
      if (!markerGroups.has(groupKey)) {
        markerGroups.set(groupKey, []);
      }
      markerGroups.get(groupKey).push(element);
    });
    markerGroups.forEach((elements) => {
      const rects = elements
        .map((element) => element.getBoundingClientRect())
        .filter((rect) => rect.width > 0 && rect.height > 0);
      if (rects.length === 0) {
        return;
      }
      const marker = document.createElement('div');
      marker.className = `rsr-preview-diff-scrollbar-marker ${pane === 'before' ? 'rsr-preview-diff-scrollbar-marker-before' : 'rsr-preview-diff-scrollbar-marker-after'}`;
      const absoluteTop = Math.min(...rects.map((rect) => rect.top)) + window.scrollY;
      const absoluteBottom = Math.max(...rects.map((rect) => rect.bottom)) + window.scrollY;
      const top = Math.max(0, Math.min(1, absoluteTop / maxScrollTop));
      const height = Math.max(4, Math.min(window.innerHeight, ((absoluteBottom - absoluteTop) / maxScrollTop) * window.innerHeight));
      const markerTop = Math.max(0, Math.min(window.innerHeight - height, top * window.innerHeight));
      marker.style.top = `${markerTop.toFixed(1)}px`;
      marker.style.height = `${height.toFixed(1)}px`;
      rail.appendChild(marker);
    });
    document.body.appendChild(rail);
  }

  return changedIndexes.size;
})();
""";
    }

    internal static async Task ApplyRenderedNavigationPlanAsync(
        WebView2CompositionControl view,
        IReadOnlyList<PreviewDiffNavigationTarget> navigationTargets)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(navigationTargets);
        if (view.CoreWebView2 is null)
        {
            return;
        }

        var targetsJson = JsonSerializer.Serialize(navigationTargets, _jsonOptions);
        await view.ExecuteScriptAsync(BuildApplyRenderedNavigationPlanScript(targetsJson));
    }

    internal static string BuildApplyRenderedNavigationPlanScript(string navigationTargetsJson)
        => $$"""
(() => {
  const navigationTargets = {{navigationTargetsJson}};
  const navigationIndexes = new Map(
    navigationTargets.map((target) => [target.index, target.navigationIndex]));
  document.querySelectorAll('[data-rsr-diff-navigation-index]').forEach((element) => {
    element.removeAttribute('data-rsr-diff-navigation-index');
  });
  document.querySelectorAll('[data-rsr-diff-index]').forEach((element) => {
    const index = Number(element.getAttribute('data-rsr-diff-index'));
    if (navigationIndexes.has(index)) {
      element.setAttribute(
        'data-rsr-diff-navigation-index',
        String(navigationIndexes.get(index)));
    }
  });
  window.__repoSyncRadarDiffScrollbar?.scheduleBuild?.();
})();
""";

    internal static string BuildNavigateToDiffScript(int navigationIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(navigationIndex);
        return $$"""
(() => {
  const styleId = 'rsr-preview-diff-navigation-style';
  if (!document.getElementById(styleId)) {
    const style = document.createElement('style');
    style.id = styleId;
    style.textContent = `
:root {
  --rsr-preview-diff-outline: #0969da;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-color-mode="light"]) {
    --rsr-preview-diff-outline: #58a6ff;
  }
}
:root[data-color-mode="dark"] {
  --rsr-preview-diff-outline: #58a6ff;
}
:root[data-color-mode="light"] {
  --rsr-preview-diff-outline: #0969da;
}
.rsr-preview-diff-active-overlay {
  background: rgba(88, 166, 255, 0.035) !important;
  border: 2px solid var(--rsr-preview-diff-outline) !important;
  border-radius: 6px !important;
  box-shadow: 0 0 0 2px rgba(88, 166, 255, 0.16) !important;
  box-sizing: border-box !important;
  pointer-events: none !important;
  position: absolute !important;
  z-index: 2147483646 !important;
}
`;
    document.head.appendChild(style);
  }

  document.querySelectorAll('.rsr-preview-diff-active').forEach((element) => {
    element.classList.remove('rsr-preview-diff-active');
  });
  const overlayId = 'rsr-preview-diff-active-overlay';
  const existingOverlay = document.getElementById(overlayId);
  if (existingOverlay?.__resizeObserver) {
    existingOverlay.__resizeObserver.disconnect();
  }
  if (existingOverlay?.__positionHandler) {
    window.removeEventListener('resize', existingOverlay.__positionHandler);
  }
  existingOverlay?.remove();

  const targets = Array.from(
    document.querySelectorAll('[data-rsr-diff-navigation-index="{{navigationIndex}}"]'));
  if (targets.length === 0) {
    return { found: false, ratio: 0 };
  }
  const root = document.scrollingElement || document.documentElement || document.body;
  const maxScrollTop = Math.max(1, (root?.scrollHeight || 0) - window.innerHeight);
  const targetRect = targets[0].getBoundingClientRect();
  const targetTop = targetRect.top + window.scrollY;
  const centeredScrollTop = Math.max(
    0,
    Math.min(maxScrollTop, targetTop - (window.innerHeight - targetRect.height) / 2));
  const ratio = Math.max(0, Math.min(1, centeredScrollTop / maxScrollTop));
  const overlay = document.createElement('div');
  overlay.id = overlayId;
  overlay.className = 'rsr-preview-diff-active-overlay';
  overlay.setAttribute('aria-hidden', 'true');
  const positionOverlay = () => {
    const rects = targets
      .map((target) => target.getBoundingClientRect())
      .filter((rect) => rect.width > 0 && rect.height > 0);
    if (rects.length === 0) {
      overlay.hidden = true;
      return;
    }
    overlay.hidden = false;
    const padding = 6;
    const left = Math.min(...rects.map((rect) => rect.left)) + window.scrollX - padding;
    const top = Math.min(...rects.map((rect) => rect.top)) + window.scrollY - padding;
    const right = Math.max(...rects.map((rect) => rect.right)) + window.scrollX + padding;
    const bottom = Math.max(...rects.map((rect) => rect.bottom)) + window.scrollY + padding;
    overlay.style.left = `${left.toFixed(1)}px`;
    overlay.style.top = `${top.toFixed(1)}px`;
    overlay.style.width = `${Math.max(0, right - left).toFixed(1)}px`;
    overlay.style.height = `${Math.max(0, bottom - top).toFixed(1)}px`;
  };
  overlay.__positionHandler = positionOverlay;
  document.body.appendChild(overlay);
  positionOverlay();
  window.addEventListener('resize', positionOverlay, { passive: true });
  if (typeof ResizeObserver === 'function') {
    const resizeObserver = new ResizeObserver(positionOverlay);
    targets.forEach((target) => resizeObserver.observe(target));
    overlay.__resizeObserver = resizeObserver;
  }
  const scrollSyncState = window.__repoSyncRadarPreviewScrollSync;
  if (scrollSyncState) {
    scrollSyncState.suppressUntil = Date.now() + 1000;
  }
  window.scrollTo({ top: centeredScrollTop, behavior: 'auto' });
  return { found: true, ratio };
})();
""";
    }

    internal static PreviewDiffNavigationResult ParseNavigateResult(string? scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult)
            || string.Equals(scriptResult, "null", StringComparison.Ordinal))
        {
            return default;
        }

        try
        {
            var result = JsonSerializer.Deserialize<PreviewDiffNavigationResult>(
                scriptResult,
                _jsonOptions);
            return result with { Ratio = Math.Clamp(result.Ratio, 0, 1) };
        }
        catch (JsonException)
        {
            return default;
        }
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