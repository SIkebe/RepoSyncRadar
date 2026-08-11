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
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("alignmentGroup")] string? AlignmentGroup = null);

internal sealed record PreviewDiffPlan(
    IReadOnlyList<int> BeforeChangedIndexes,
    IReadOnlyList<int> AfterChangedIndexes,
    IReadOnlyList<PreviewDiffChange> Changes);

internal sealed record PreviewDiffChange(
    IReadOnlyList<int> BeforeIndexes,
    IReadOnlyList<int> AfterIndexes,
    int? BeforeAnchorIndex,
    int? AfterAnchorIndex);

internal sealed record PreviewDiffAlignmentGap(
    [property: JsonPropertyName("anchorIndex")] int? AnchorIndex,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("navigationIndex")] int NavigationIndex);

internal sealed record PreviewDiffAlignmentGapPlan(
    IReadOnlyList<PreviewDiffAlignmentGap> Before,
    IReadOnlyList<PreviewDiffAlignmentGap> After);

internal sealed record PreviewDiffAlignmentMeasurement(
    [property: JsonPropertyName("scrollTop")] double ScrollTop,
    [property: JsonPropertyName("offsets")] double?[] Offsets);

internal sealed record PreviewDiffNavigationTarget(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("navigationIndex")] int NavigationIndex);

internal readonly record struct PreviewDiffNavigationResult(
    [property: JsonPropertyName("found")] bool Found);

internal static class PreviewDiffHighlighter
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan _extractionRetryDelay = TimeSpan.FromMilliseconds(250);

    private const int _maxExtractionAttempts = 6;
    private const long _maxGranularPlanCells = 1_000_000;
    private const string _extractCodeLinesToken = "__RSR_EXTRACT_CODE_LINES__";

    private const string _extractBlocksScriptTemplate = """
((extractCodeLines) => {
  const mediaSelector = 'img,video,audio,iframe,object,embed';
  const structuralContainerSelector = '.ghd-markdown-alert,.ghd-alert,.ghd-tool';
  const codeLineSelector = 'pre > code > .rsr-code-line';
  const blockSelector =
    `h1,h2,h3,h4,h5,h6,p,li,pre,blockquote,td,th,.ghd-code-tab-label,${extractCodeLines ? `${codeLineSelector},` : ''}${structuralContainerSelector}`;
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
  const diffSelector = '.rsr-rendered-diff-added,.rsr-rendered-diff-removed';
  const substantiveDiffSelector =
    '.rsr-rendered-diff-added:not(.rsr-rendered-diff-gap),' +
    '.rsr-rendered-diff-removed:not(.rsr-rendered-diff-gap)';
  const canonicalizePaneRoutes = (value) =>
    normalize(value).replace(
      /\/markdown-assets\/(?:before|after)(?=\/)/g,
      '/markdown-assets/shared');
  const fingerprintMediaContent = (element) => {
    if (!(element instanceof HTMLImageElement) ||
        !element.complete ||
        element.naturalWidth <= 0 ||
        element.naturalHeight <= 0) {
      return '';
    }
    try {
      const canvas = document.createElement('canvas');
      canvas.width = Math.min(32, element.naturalWidth);
      canvas.height = Math.min(32, element.naturalHeight);
      const context = canvas.getContext('2d', { willReadFrequently: true });
      if (!context) {
        return '';
      }
      context.drawImage(element, 0, 0, canvas.width, canvas.height);
      const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
      let hash = 2166136261;
      for (let index = 0; index < pixels.length; index++) {
        hash ^= pixels[index];
        hash = Math.imul(hash, 16777619);
      }
      return `${element.naturalWidth}x${element.naturalHeight}:${hash >>> 0}`;
    } catch {
      return '';
    }
  };
  const describeChangedMarkup = (element) => {
    const clone = element.cloneNode(true);
    if (element.matches(codeLineSelector)) {
      clone.querySelectorAll('.rsr-rendered-diff-gap').forEach((marker) => marker.remove());
    }
    const markers = [
      ...(clone.matches(diffSelector) ? [clone] : []),
      ...clone.querySelectorAll(diffSelector),
    ];
    markers.forEach((marker) => {
      marker.classList.remove('rsr-rendered-diff-added', 'rsr-rendered-diff-removed');
      marker.classList.add('rsr-rendered-diff-changed');
    });
    clone.querySelectorAll('[src],[poster]').forEach((media) => {
      ['src', 'poster'].forEach((attribute) => {
        if (media.hasAttribute(attribute)) {
          media.setAttribute(attribute, canonicalizePaneRoutes(media.getAttribute(attribute)));
        }
      });
    });
    clone.querySelectorAll('[data-rsr-diff-index],[data-rsr-diff-navigation-index]')
      .forEach((target) => {
        target.removeAttribute('data-rsr-diff-index');
        target.removeAttribute('data-rsr-diff-navigation-index');
      });
    return normalize(clone.innerHTML);
  };
  const describe = (element) => {
    if (element.matches(mediaSelector)) {
      const source =
        element.currentSrc ||
        element.getAttribute('src') ||
        element.getAttribute('poster') ||
        element.getAttribute('alt') ||
        element.outerHTML;
      return `media:${element.tagName.toLowerCase()}:${canonicalizePaneRoutes(source)}:${fingerprintMediaContent(element)}`;
    }
    if (element.matches(codeLineSelector)) {
      const text = element.textContent || '';
      const hasSubstantiveDiff =
        element.matches(substantiveDiffSelector) ||
        element.querySelector(substantiveDiffSelector);
      return hasSubstantiveDiff
        ? `code:${text}|markup:${describeChangedMarkup(element)}`
        : `code:${text}`;
    }
    const text = normalize(element.innerText || element.textContent);
    return element.matches(diffSelector) || element.querySelector(diffSelector)
      ? `${text}|markup:${describeChangedMarkup(element)}`
      : text;
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
      const structuralContainer = element.closest(structuralContainerSelector);
      if (structuralContainer &&
          !element.matches(structuralContainerSelector) &&
          !(extractCodeLines && structuralContainer.querySelector(codeLineSelector))) {
        return false;
      }
      const text = describe(element);
      const isCodeBlock =
        (extractCodeLines && element.matches(codeLineSelector)) ||
        (!extractCodeLines &&
          element.matches(`pre,${structuralContainerSelector}`) &&
          element.querySelector(codeLineSelector));
      if (!isCodeBlock && text.length < 2) {
        return false;
      }
      if (element.matches(structuralContainerSelector)) {
        return !(extractCodeLines && element.querySelector(codeLineSelector));
      }
      return !element.querySelector(blockSelector);
    });

  const alignmentGroups = new WeakMap();
  Array.from(root.querySelectorAll('table')).forEach((table, tableIndex) => {
    const rows = Array.from(table.rows);
    let groupIndex = -1;
    let groupEndRowIndex = -1;
    rows.forEach((row, rowIndex) => {
      if (rowIndex > groupEndRowIndex) {
        groupIndex++;
        groupEndRowIndex = rowIndex;
      }
      const sectionEndRowIndex = rows.reduce(
        (endIndex, candidate, candidateIndex) =>
          candidate.parentElement === row.parentElement ? candidateIndex : endIndex,
        rowIndex);
      Array.from(row.cells).forEach((cell) => {
        const rowsRemainingInSection = sectionEndRowIndex - rowIndex + 1;
        const effectiveRowSpan = cell.rowSpan === 0
          ? rowsRemainingInSection
          : Math.min(rowsRemainingInSection, Math.max(1, cell.rowSpan));
        groupEndRowIndex = Math.max(
          groupEndRowIndex,
          rowIndex + effectiveRowSpan - 1);
      });
      alignmentGroups.set(row, `table-${tableIndex}-row-group-${groupIndex}`);
    });
  });
  const getAlignmentGroup = (element) => {
    const row = element.closest('tr');
    return row ? alignmentGroups.get(row) || null : null;
  };
  return elements.map((element, index) => {
    element.setAttribute('data-rsr-diff-index', String(index));
    return { index, text: describe(element), alignmentGroup: getAlignmentGroup(element) };
  });
})(__RSR_EXTRACT_CODE_LINES__);
""";

    internal static string ExtractBlocksScriptForTests => BuildExtractBlocksScript(extractCodeLines: true);

    internal static string BuildExtractBlocksScript(bool extractCodeLines)
        => _extractBlocksScriptTemplate.Replace(
            _extractCodeLinesToken,
            extractCodeLines ? "true" : "false",
            StringComparison.Ordinal);

    internal static async Task<IReadOnlyList<PreviewDiffBlock>> ExtractBlocksAsync(
        WebView2CompositionControl view,
        bool extractCodeLines = true)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.CoreWebView2 is null)
        {
            return Array.Empty<PreviewDiffBlock>();
        }

        for (var attempt = 0; attempt < _maxExtractionAttempts; attempt++)
        {
            var scriptResult = await view.ExecuteScriptAsync(BuildExtractBlocksScript(extractCodeLines));
            var blocks = DeserializeBlocks(scriptResult);
            if (blocks.Count > 0 || attempt == _maxExtractionAttempts - 1)
            {
                return blocks;
            }

            await Task.Delay(_extractionRetryDelay);
        }

        return Array.Empty<PreviewDiffBlock>();
    }

    internal static async Task<IReadOnlyList<PreviewDiffBlock>[]> ExtractComparableBlocksAsync(
        WebView2CompositionControl beforeView,
        WebView2CompositionControl afterView)
    {
        ArgumentNullException.ThrowIfNull(beforeView);
        ArgumentNullException.ThrowIfNull(afterView);

        var blocks = await Task.WhenAll(
            ExtractBlocksAsync(beforeView),
            ExtractBlocksAsync(afterView));
        if (!RequiresCoarseCodeBlockExtraction(blocks[0].Count, blocks[1].Count))
        {
            return blocks;
        }

        return await Task.WhenAll(
            ExtractBlocksAsync(beforeView, extractCodeLines: false),
            ExtractBlocksAsync(afterView, extractCodeLines: false));
    }

    internal static bool RequiresCoarseCodeBlockExtraction(int beforeBlockCount, int afterBlockCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(beforeBlockCount);
        ArgumentOutOfRangeException.ThrowIfNegative(afterBlockCount);

        return beforeBlockCount > 0
            && afterBlockCount > 0
            && ExceedsPlanCellBudget(beforeBlockCount, afterBlockCount);
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
                : [new PreviewDiffChange(beforeIndexes, afterIndexes, null, null)];
            return new PreviewDiffPlan(
                beforeIndexes,
                afterIndexes,
                emptySideChanges);
        }

        var beforeTexts = beforeBlocks.Select(block => NormalizeText(block.Text)).ToArray();
        var afterTexts = afterBlocks.Select(block => NormalizeText(block.Text)).ToArray();
        if (ExceedsPlanCellBudget(beforeTexts.Length, afterTexts.Length))
        {
            return BuildBoundedPlan(beforeBlocks, afterBlocks, beforeTexts, afterTexts);
        }

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
        var beforeCursor = 0;
        var afterCursor = 0;
        void FlushChange()
        {
            if (currentBeforeIndexes.Count == 0 && currentAfterIndexes.Count == 0)
            {
                return;
            }

            var advancePastCurrentAlignmentGroup =
                IsAnchorInChangedAlignmentGroup(beforeBlocks, beforeCursor, currentBeforeIndexes)
                || IsAnchorInChangedAlignmentGroup(afterBlocks, afterCursor, currentAfterIndexes);
            changes.Add(new PreviewDiffChange(
                currentBeforeIndexes.ToArray(),
                currentAfterIndexes.ToArray(),
                FindAlignmentAnchorIndex(
                    beforeBlocks,
                    beforeCursor,
                    currentBeforeIndexes,
                    advancePastCurrentAlignmentGroup),
                FindAlignmentAnchorIndex(
                    afterBlocks,
                    afterCursor,
                    currentAfterIndexes,
                    advancePastCurrentAlignmentGroup)));
            currentBeforeIndexes.Clear();
            currentAfterIndexes.Clear();
        }

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

    private static PreviewDiffPlan BuildBoundedPlan(
        IReadOnlyList<PreviewDiffBlock> beforeBlocks,
        IReadOnlyList<PreviewDiffBlock> afterBlocks,
        string[] beforeTexts,
        string[] afterTexts)
    {
        var commonPrefixLength = 0;
        while (commonPrefixLength < beforeTexts.Length
            && commonPrefixLength < afterTexts.Length
            && string.Equals(
                beforeTexts[commonPrefixLength],
                afterTexts[commonPrefixLength],
                StringComparison.Ordinal))
        {
            commonPrefixLength++;
        }

        var beforeEnd = beforeTexts.Length - 1;
        var afterEnd = afterTexts.Length - 1;
        while (beforeEnd >= commonPrefixLength
            && afterEnd >= commonPrefixLength
            && string.Equals(beforeTexts[beforeEnd], afterTexts[afterEnd], StringComparison.Ordinal))
        {
            beforeEnd--;
            afterEnd--;
        }

        var beforeChangedIndexes = beforeBlocks
            .Skip(commonPrefixLength)
            .Take(beforeEnd - commonPrefixLength + 1)
            .Select(static block => block.Index)
            .ToArray();
        var afterChangedIndexes = afterBlocks
            .Skip(commonPrefixLength)
            .Take(afterEnd - commonPrefixLength + 1)
            .Select(static block => block.Index)
            .ToArray();
        if (beforeChangedIndexes.Length == 0 && afterChangedIndexes.Length == 0)
        {
            return new PreviewDiffPlan([], [], []);
        }

        var beforeCursor = beforeEnd + 1;
        var afterCursor = afterEnd + 1;
        var advancePastCurrentAlignmentGroup =
            IsAnchorInChangedAlignmentGroup(beforeBlocks, beforeCursor, beforeChangedIndexes)
            || IsAnchorInChangedAlignmentGroup(afterBlocks, afterCursor, afterChangedIndexes);
        var change = new PreviewDiffChange(
            beforeChangedIndexes,
            afterChangedIndexes,
            FindAlignmentAnchorIndex(
                beforeBlocks,
                beforeCursor,
                beforeChangedIndexes,
                advancePastCurrentAlignmentGroup),
            FindAlignmentAnchorIndex(
                afterBlocks,
                afterCursor,
                afterChangedIndexes,
                advancePastCurrentAlignmentGroup));
        return new PreviewDiffPlan(beforeChangedIndexes, afterChangedIndexes, [change]);
    }

    private static bool ExceedsPlanCellBudget(int beforeBlockCount, int afterBlockCount)
        => ((long)beforeBlockCount + 1) * (afterBlockCount + 1) > _maxGranularPlanCells;

    private static int? FindAlignmentAnchorIndex(
        IReadOnlyList<PreviewDiffBlock> blocks,
        int cursor,
        IReadOnlyCollection<int> changedIndexes,
        bool advancePastCurrentAlignmentGroup)
    {
        if (cursor >= blocks.Count)
        {
            return null;
        }

        var changedIndexSet = changedIndexes.ToHashSet();
        var changedGroups = blocks
            .Where(block => changedIndexSet.Contains(block.Index) && block.AlignmentGroup is not null)
            .Select(static block => block.AlignmentGroup!)
            .ToHashSet(StringComparer.Ordinal);
        if (advancePastCurrentAlignmentGroup && blocks[cursor].AlignmentGroup is { } currentGroup)
        {
            changedGroups.Add(currentGroup);
        }
        while (cursor < blocks.Count
            && blocks[cursor].AlignmentGroup is { } group
            && changedGroups.Contains(group))
        {
            cursor++;
        }

        return cursor < blocks.Count ? blocks[cursor].Index : null;
    }

    private static bool IsAnchorInChangedAlignmentGroup(
        IReadOnlyList<PreviewDiffBlock> blocks,
        int cursor,
        IReadOnlyCollection<int> changedIndexes)
    {
        if (cursor >= blocks.Count || blocks[cursor].AlignmentGroup is not { } anchorGroup)
        {
            return false;
        }

        var changedIndexSet = changedIndexes.ToHashSet();
        return blocks.Any(
            block => changedIndexSet.Contains(block.Index)
                && string.Equals(block.AlignmentGroup, anchorGroup, StringComparison.Ordinal));
    }

    internal static async Task ApplyAlignmentGapsAsync(
        WebView2CompositionControl beforeView,
        WebView2CompositionControl afterView,
        IReadOnlyList<PreviewDiffChange> changes,
        Func<bool>? isCurrent = null)
    {
        ArgumentNullException.ThrowIfNull(beforeView);
        ArgumentNullException.ThrowIfNull(afterView);
        ArgumentNullException.ThrowIfNull(changes);
        if (beforeView.CoreWebView2 is null || afterView.CoreWebView2 is null)
        {
            return;
        }

        var beforeAnchorsJson = JsonSerializer.Serialize(
            changes.Select(static change => change.BeforeAnchorIndex),
            _jsonOptions);
        var afterAnchorsJson = JsonSerializer.Serialize(
            changes.Select(static change => change.AfterAnchorIndex),
            _jsonOptions);
        var measurements = await Task.WhenAll(
            beforeView.ExecuteScriptAsync(BuildMeasureAlignmentAnchorsScript(beforeAnchorsJson)),
            afterView.ExecuteScriptAsync(BuildMeasureAlignmentAnchorsScript(afterAnchorsJson)));
        var beforeMeasurement = DeserializeMeasurement(measurements[0]);
        var afterMeasurement = DeserializeMeasurement(measurements[1]);
        if (isCurrent is not null && !isCurrent())
        {
            return;
        }

        if (beforeMeasurement.Offsets.Length != changes.Count
            || afterMeasurement.Offsets.Length != changes.Count
            || beforeMeasurement.Offsets.Any(static offset => offset is null)
            || afterMeasurement.Offsets.Any(static offset => offset is null))
        {
            return;
        }

        var gapPlan = BuildAlignmentGapPlan(
            changes,
            beforeMeasurement.Offsets.Select(static offset => offset!.Value).ToArray(),
            afterMeasurement.Offsets.Select(static offset => offset!.Value).ToArray());
        var synchronizedScrollTop = ResolveSynchronizedScrollTop(
            beforeMeasurement.ScrollTop,
            afterMeasurement.ScrollTop);

        var appliedScrollTops = await Task.WhenAll(
            beforeView.ExecuteScriptAsync(BuildApplyAlignmentGapsScript(
                JsonSerializer.Serialize(gapPlan.Before, _jsonOptions),
                synchronizedScrollTop)),
            afterView.ExecuteScriptAsync(BuildApplyAlignmentGapsScript(
                JsonSerializer.Serialize(gapPlan.After, _jsonOptions),
                synchronizedScrollTop)));
        if (isCurrent is not null && !isCurrent())
        {
            return;
        }

        var beforeAppliedScrollTop = DeserializeDouble(appliedScrollTops[0]);
        var afterAppliedScrollTop = DeserializeDouble(appliedScrollTops[1]);
        if (beforeAppliedScrollTop is null || afterAppliedScrollTop is null)
        {
            return;
        }

        var appliedSynchronizedScrollTop = ResolveAppliedSynchronizedScrollTop(
            beforeAppliedScrollTop.Value,
            afterAppliedScrollTop.Value);
        var finalScrollScript = MainWindow.BuildApplySynchronizedScrollScript(
            appliedSynchronizedScrollTop);
        await Task.WhenAll(
            beforeView.ExecuteScriptAsync(finalScrollScript),
            afterView.ExecuteScriptAsync(finalScrollScript));
    }

    internal static double ResolveSynchronizedScrollTop(double beforeScrollTop, double afterScrollTop)
        => Math.Max(Math.Max(0, beforeScrollTop), afterScrollTop);

    internal static double ResolveAppliedSynchronizedScrollTop(
        double beforeScrollTop,
        double afterScrollTop)
        => Math.Min(Math.Max(0, beforeScrollTop), Math.Max(0, afterScrollTop));

    internal static PreviewDiffAlignmentGapPlan BuildAlignmentGapPlan(
        IReadOnlyList<PreviewDiffChange> changes,
        IReadOnlyList<double> beforeAnchorOffsets,
        IReadOnlyList<double> afterAnchorOffsets)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(beforeAnchorOffsets);
        ArgumentNullException.ThrowIfNull(afterAnchorOffsets);
        if (beforeAnchorOffsets.Count != changes.Count || afterAnchorOffsets.Count != changes.Count)
        {
            throw new ArgumentException("Every diff change must have one measured anchor in each pane.");
        }

        var beforeGaps = new List<PreviewDiffAlignmentGap>();
        var afterGaps = new List<PreviewDiffAlignmentGap>();
        var cumulativeBeforeGap = 0d;
        var cumulativeAfterGap = 0d;
        for (var index = 0; index < changes.Count; index++)
        {
            var beforeOffset = beforeAnchorOffsets[index] + cumulativeBeforeGap;
            var afterOffset = afterAnchorOffsets[index] + cumulativeAfterGap;
            var delta = beforeOffset - afterOffset;
            if (Math.Abs(delta) < 1)
            {
                continue;
            }

            var change = changes[index];
            if (delta < 0)
            {
                var height = -delta;
                beforeGaps.Add(new PreviewDiffAlignmentGap(change.BeforeAnchorIndex, height, index));
                cumulativeBeforeGap += height;
            }
            else
            {
                afterGaps.Add(new PreviewDiffAlignmentGap(change.AfterAnchorIndex, delta, index));
                cumulativeAfterGap += delta;
            }
        }

        return new PreviewDiffAlignmentGapPlan(beforeGaps, afterGaps);
    }

    internal static string BuildMeasureAlignmentAnchorsScript(string anchorIndexesJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorIndexesJson);
        return $$"""
(() => {
  const root =
    document.querySelector('main article') ||
    document.querySelector('article') ||
    document.querySelector('[data-testid="article-body"]') ||
    document.querySelector('main') ||
    document.body;
  const scrollingRoot = document.scrollingElement || document.documentElement || document.body;
  const scrollTop = window.scrollY || scrollingRoot?.scrollTop || 0;
  const existingGaps = Array.from(
    document.querySelectorAll('.rsr-preview-diff-alignment-gap'));
  existingGaps.forEach((element) => {
    element.style.setProperty('display', 'none', 'important');
  });
  const restoreExistingGaps = () => {
    existingGaps.forEach((element) => element.style.removeProperty('display'));
    const scrollSyncState = window.__repoSyncRadarPreviewScrollSync;
    if (scrollSyncState) {
      scrollSyncState.suppressUntil = Date.now() + 1000;
    }
    const maxScrollTop = Math.max(0, (scrollingRoot?.scrollHeight || 0) - window.innerHeight);
    window.scrollTo({ top: Math.min(scrollTop, maxScrollTop), behavior: 'auto' });
    if (scrollSyncState) {
      scrollSyncState.lastScrollTop = window.scrollY || scrollingRoot?.scrollTop || 0;
    }
  };
  if (!root) {
    restoreExistingGaps();
    return { scrollTop, offsets: [] };
  }
  const offsets = {{anchorIndexesJson}}.map((anchorIndex) => {
      if (anchorIndex === null) {
        return root.getBoundingClientRect().bottom + window.scrollY;
      }
      const anchor = document.querySelector(`[data-rsr-diff-index="${anchorIndex}"]`);
      return anchor
        ? anchor.getBoundingClientRect().top + window.scrollY
        : null;
    });
  restoreExistingGaps();
  return { scrollTop, offsets };
})();
""";
    }

    internal static string BuildApplyAlignmentGapsScript(
        string gapsJson,
        double preservedScrollTop = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gapsJson);
        var scrollTopLiteral = Math.Max(0, preservedScrollTop)
            .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
(() => {
  const preservedScrollTop = {{scrollTopLiteral}};
  const styleId = 'rsr-preview-diff-alignment-style';
  if (!document.getElementById(styleId)) {
    const style = document.createElement('style');
    style.id = styleId;
    style.textContent = `
:root {
  --rsr-preview-gap-bg: rgba(110, 118, 129, 0.08);
  --rsr-preview-gap-stripe: rgba(110, 118, 129, 0.42);
  --rsr-preview-gap-border: rgba(110, 118, 129, 0.22);
}
@media (prefers-color-scheme: dark) {
  :root:not([data-color-mode="light"]) {
    --rsr-preview-gap-bg: rgba(139, 148, 158, 0.08);
    --rsr-preview-gap-stripe: rgba(139, 148, 158, 0.42);
    --rsr-preview-gap-border: rgba(139, 148, 158, 0.24);
  }
}
:root[data-color-mode="dark"] {
  --rsr-preview-gap-bg: rgba(139, 148, 158, 0.08);
  --rsr-preview-gap-stripe: rgba(139, 148, 158, 0.42);
  --rsr-preview-gap-border: rgba(139, 148, 158, 0.24);
}
html, body {
  overflow-anchor: none !important;
}
.rsr-preview-diff-alignment-gap {
  background-color: var(--rsr-preview-gap-bg) !important;
  background-image: repeating-linear-gradient(
    -45deg,
    transparent 0 6px,
    var(--rsr-preview-gap-stripe) 6px 8px) !important;
  border-block-start: 1px solid var(--rsr-preview-gap-border) !important;
  border-block-end: 1px solid transparent !important;
  box-sizing: border-box !important;
  display: block !important;
  list-style: none !important;
  margin: 0 !important;
  padding: 0 !important;
  pointer-events: none !important;
  width: 100% !important;
}
td.rsr-preview-diff-alignment-gap {
  display: table-cell !important;
}
`;
    document.head.appendChild(style);
  }

  document.querySelectorAll(
    '.rsr-preview-diff-alignment-gap-row,.rsr-preview-diff-alignment-gap').forEach((element) => {
    element.remove();
  });
  const root =
    document.querySelector('main article') ||
    document.querySelector('article') ||
    document.querySelector('[data-testid="article-body"]') ||
    document.querySelector('main') ||
    document.body;
  if (!root) {
    return null;
  }

  const createGap = (height, navigationIndex, tagName = 'div') => {
    const element = document.createElement(tagName);
    element.className = 'rsr-preview-diff-alignment-gap';
    element.setAttribute('aria-hidden', 'true');
    element.setAttribute('role', 'presentation');
    element.setAttribute('data-rsr-diff-navigation-index', String(navigationIndex));
    element.style.height = `${height.toFixed(2)}px`;
    return element;
  };
  const insertGapBefore = (anchor, gap, desiredHeight) => {
    const anchorTopBefore = anchor.getBoundingClientRect().top;
    anchor.parentNode.insertBefore(gap, anchor);
    let renderedHeight = desiredHeight;
    for (let attempt = 0; attempt < 3; attempt++) {
      const actualDisplacement = anchor.getBoundingClientRect().top - anchorTopBefore;
      const correction = desiredHeight - actualDisplacement;
      if (Math.abs(correction) <= 0.1) {
        break;
      }
      renderedHeight = Math.max(1, renderedHeight + correction);
      gap.style.height = `${renderedHeight.toFixed(2)}px`;
    }
  };
  const getTableColumnCount = (table) => {
    const rows = Array.from(table?.rows || []);
    const activeRowSpans = [];
    let widestColumnCount = 1;
    rows.forEach((tableRow, rowIndex) => {
      const sectionEndRowIndex = rows.reduce(
        (endIndex, candidate, candidateIndex) =>
          candidate.parentElement === tableRow.parentElement ? candidateIndex : endIndex,
        rowIndex);
      let columnIndex = 0;
      Array.from(tableRow.cells).forEach((cell) => {
        const colSpan = Math.max(1, cell.colSpan || 1);
        let fits = false;
        while (!fits) {
          while ((activeRowSpans[columnIndex] || 0) > 0) {
            columnIndex++;
          }
          fits = true;
          for (let offset = 0; offset < colSpan; offset++) {
            if ((activeRowSpans[columnIndex + offset] || 0) > 0) {
              columnIndex += offset + 1;
              fits = false;
              break;
            }
          }
        }
        const rowsRemainingInSection = sectionEndRowIndex - rowIndex + 1;
        const effectiveRowSpan = cell.rowSpan === 0
          ? rowsRemainingInSection
          : Math.min(rowsRemainingInSection, Math.max(1, cell.rowSpan));
        for (let offset = 0; offset < colSpan; offset++) {
          activeRowSpans[columnIndex + offset] = Math.max(
            activeRowSpans[columnIndex + offset] || 0,
            effectiveRowSpan);
        }
        columnIndex += colSpan;
      });
      const occupiedColumnCount = activeRowSpans.reduce(
        (count, remainingRows, index) => remainingRows > 0 ? index + 1 : count,
        0);
      widestColumnCount = Math.max(
        widestColumnCount,
        columnIndex,
        occupiedColumnCount);
      for (let index = 0; index < activeRowSpans.length; index++) {
        activeRowSpans[index] = Math.max(0, (activeRowSpans[index] || 0) - 1);
      }
    });
    return widestColumnCount;
  };
  const gaps = {{gapsJson}};
  gaps.forEach((gap) => {
    const height = Math.max(1, Number(gap.height) || 0);
    const anchor = gap.anchorIndex === null
      ? null
      : document.querySelector(`[data-rsr-diff-index="${gap.anchorIndex}"]`);
    const row = anchor?.closest('tr');
    if (row?.parentNode) {
      const gapRow = document.createElement('tr');
      gapRow.className = 'rsr-preview-diff-alignment-gap-row';
      gapRow.setAttribute('aria-hidden', 'true');
      gapRow.setAttribute('role', 'presentation');
      const gapCell = createGap(height, gap.navigationIndex, 'td');
      const table = row.closest('table');
      gapCell.colSpan = getTableColumnCount(table);
      gapRow.appendChild(gapCell);
      row.parentNode.insertBefore(gapRow, row);
      return;
    }
    if (anchor?.parentNode) {
      const tagName = anchor.matches('.rsr-code-line') ? 'span' : 'div';
      insertGapBefore(anchor, createGap(height, gap.navigationIndex, tagName), height);
      return;
    }
    root.appendChild(createGap(height, gap.navigationIndex));
  });
  window.__repoSyncRadarDiffNavigation?.refresh?.();
  window.__repoSyncRadarDiffScrollbar?.scheduleBuild?.();
  const scrollingRoot = document.scrollingElement || document.documentElement || document.body;
  const scrollSyncState = window.__repoSyncRadarPreviewScrollSync;
  if (scrollSyncState) {
    scrollSyncState.suppressUntil = Date.now() + 1000;
  }
  const maxScrollTop = Math.max(0, (scrollingRoot?.scrollHeight || 0) - window.innerHeight);
  window.scrollTo({ top: Math.min(preservedScrollTop, maxScrollTop), behavior: 'auto' });
  if (scrollSyncState) {
    scrollSyncState.lastScrollTop = window.scrollY || scrollingRoot?.scrollTop || 0;
  }
  return window.scrollY || scrollingRoot?.scrollTop || 0;
})();
""";
    }

    private static PreviewDiffAlignmentMeasurement DeserializeMeasurement(string? scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult)
            || string.Equals(scriptResult, "null", StringComparison.Ordinal))
        {
            return new PreviewDiffAlignmentMeasurement(0, []);
        }

        try
        {
            return JsonSerializer.Deserialize<PreviewDiffAlignmentMeasurement>(
                    scriptResult,
                    _jsonOptions)
                ?? new PreviewDiffAlignmentMeasurement(0, []);
        }
        catch (JsonException)
        {
            return new PreviewDiffAlignmentMeasurement(0, []);
        }
    }

    private static double? DeserializeDouble(string? scriptResult)
    {
        if (string.IsNullOrWhiteSpace(scriptResult)
            || string.Equals(scriptResult, "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<double>(scriptResult, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
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
.rsr-code-line.rsr-preview-diff-block {
  border-radius: 2px !important;
  margin-left: 0 !important;
  padding: 0 0 0 0.35rem !important;
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
  const buildMarkers = () => {
    document.getElementById(markerRootId)?.remove();
    const markerElements = Array.from(
      document.querySelectorAll('.rsr-preview-diff-block,[data-rsr-diff-navigation-index]'));
    if (markerElements.length === 0) {
      return;
    }
    const root = document.scrollingElement || document.documentElement || document.body;
    const maxScrollTop = Math.max(1, root.scrollHeight - window.innerHeight);
    const rail = document.createElement('div');
    rail.id = markerRootId;
    rail.className = 'rsr-preview-diff-scrollbar';
    const markerGroups = new Map();
    markerElements.forEach((element) => {
        const navigationIndex = element.getAttribute('data-rsr-diff-navigation-index');
        const blockIndex = element.getAttribute('data-rsr-diff-index') ?? markerGroups.size;
        const groupKey = navigationIndex === null ? `block-${blockIndex}` : `hunk-${navigationIndex}`;
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
  };
  window.__repoSyncRadarDiffScrollbar = { scheduleBuild: buildMarkers };
  buildMarkers();

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
  window.__repoSyncRadarDiffNavigation = undefined;

  let targets = Array.from(
    document.querySelectorAll('[data-rsr-diff-navigation-index="{{navigationIndex}}"]'));
  if (targets.length === 0) {
    return { found: false };
  }
  const root = document.scrollingElement || document.documentElement || document.body;
  const maxScrollTop = Math.max(1, (root?.scrollHeight || 0) - window.innerHeight);
  const targetRects = targets
    .map((target) => target.getBoundingClientRect())
    .filter((rect) => rect.width > 0 && rect.height > 0);
  if (targetRects.length === 0) {
    return { found: false };
  }
  const targetTop = Math.min(...targetRects.map((rect) => rect.top)) + window.scrollY;
  const targetBottom = Math.max(...targetRects.map((rect) => rect.bottom)) + window.scrollY;
  const targetHeight = targetBottom - targetTop;
  const centeredScrollTop = Math.max(
    0,
    Math.min(maxScrollTop, targetTop - (window.innerHeight - targetHeight) / 2));
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
  window.addEventListener('resize', positionOverlay, { passive: true });
  const refreshTargets = () => {
    targets = Array.from(
      document.querySelectorAll('[data-rsr-diff-navigation-index="{{navigationIndex}}"]'));
    overlay.__resizeObserver?.disconnect();
    positionOverlay();
    if (typeof ResizeObserver === 'function') {
      const resizeObserver = new ResizeObserver(positionOverlay);
      targets.forEach((target) => resizeObserver.observe(target));
      resizeObserver.observe(document.body);
      overlay.__resizeObserver = resizeObserver;
    }
  };
  window.__repoSyncRadarDiffNavigation = {
    navigationIndex: {{navigationIndex}},
    refresh: refreshTargets,
  };
  refreshTargets();
  const scrollSyncState = window.__repoSyncRadarPreviewScrollSync;
  if (scrollSyncState) {
    scrollSyncState.suppressUntil = Date.now() + 1000;
  }
  window.scrollTo({ top: centeredScrollTop, behavior: 'auto' });
  return { found: true };
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
            return result;
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