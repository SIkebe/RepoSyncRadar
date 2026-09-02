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
    int? AfterAnchorIndex,
    IReadOnlyList<int>? AlignmentNavigationIndexes = null);

internal sealed record PreviewDiffAlignmentGap(
    [property: JsonPropertyName("anchorIndex")] int? AnchorIndex,
    [property: JsonPropertyName("height")] double Height,
    [property: JsonPropertyName("navigationIndex")] int NavigationIndex,
    [property: JsonPropertyName("navigationOnly")] bool NavigationOnly = false);

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
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("scrollTop")] double ScrollTop = 0);

internal static class PreviewDiffHighlighter
{
    internal const int MaximumAlignedChangeCount = 512;
    private readonly record struct BlockAnchor(int BeforeIndex, int AfterIndex);
    private readonly record struct BlockRange(int BeforeStart, int BeforeEnd, int AfterStart, int AfterEnd);

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan _extractionRetryDelay = TimeSpan.FromMilliseconds(250);

    private const int _maxExtractionAttempts = 6;
    private const long _maxPlanCells = 4_000_000;
    private const long _maxComparisonWork = 8_000_000;
    private const string _extractCodeLinesToken = "__RSR_EXTRACT_CODE_LINES__";

    private const string _extractBlocksScriptTemplate = """
((extractCodeLines) => {
  const mediaSelector = 'img,video,audio,iframe,object,embed';
  const structuralContainerSelector = '.ghd-markdown-alert,.ghd-alert,.ghd-tool';
  const sourceDiffContainerSelector = '.rsr-source-diff';
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
    if (element.closest(sourceDiffContainerSelector)) {
      return false;
    }
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
    const sectionEndRowIndexes = new Map();
    rows.forEach((row, rowIndex) => {
      sectionEndRowIndexes.set(row.parentElement, rowIndex);
    });
    let groupIndex = -1;
    let groupEndRowIndex = -1;
    rows.forEach((row, rowIndex) => {
      if (rowIndex > groupEndRowIndex) {
        groupIndex++;
        groupEndRowIndex = rowIndex;
      }
      const sectionEndRowIndex =
        sectionEndRowIndexes.get(row.parentElement) ?? rowIndex;
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

        return await Task.WhenAll(
            ExtractBlocksAsync(beforeView),
            ExtractBlocksAsync(afterView));
    }

    internal static PreviewDiffPlan BuildPlan(
        IReadOnlyList<PreviewDiffBlock> beforeBlocks,
        IReadOnlyList<PreviewDiffBlock> afterBlocks)
    {
        return BuildPlan(beforeBlocks, afterBlocks, out _);
    }

    internal static PreviewDiffPlan BuildPlan(
        IReadOnlyList<PreviewDiffBlock> beforeBlocks,
        IReadOnlyList<PreviewDiffBlock> afterBlocks,
        out int patienceAnchorScanCount)
    {
        ArgumentNullException.ThrowIfNull(beforeBlocks);
        ArgumentNullException.ThrowIfNull(afterBlocks);
        patienceAnchorScanCount = 0;

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
            return BuildBoundedPlan(
                beforeBlocks,
                afterBlocks,
                beforeTexts,
                afterTexts,
                out patienceAnchorScanCount);
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
        var beforeAlignmentGroups = BuildAlignmentGroupsByIndex(beforeBlocks);
        var afterAlignmentGroups = BuildAlignmentGroupsByIndex(afterBlocks);
        void FlushChange()
        {
            if (currentBeforeIndexes.Count == 0 && currentAfterIndexes.Count == 0)
            {
                return;
            }

            var advancePastCurrentAlignmentGroup =
                IsAnchorInChangedAlignmentGroup(
                    beforeBlocks,
                    beforeAlignmentGroups,
                    beforeCursor,
                    currentBeforeIndexes)
                || IsAnchorInChangedAlignmentGroup(
                    afterBlocks,
                    afterAlignmentGroups,
                    afterCursor,
                    currentAfterIndexes);
            changes.Add(new PreviewDiffChange(
                currentBeforeIndexes.ToArray(),
                currentAfterIndexes.ToArray(),
                FindAlignmentAnchorIndex(
                    beforeBlocks,
                    beforeAlignmentGroups,
                    beforeCursor,
                    currentBeforeIndexes,
                    advancePastCurrentAlignmentGroup),
                FindAlignmentAnchorIndex(
                    afterBlocks,
                    afterAlignmentGroups,
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
        string[] afterTexts,
        out int patienceAnchorScanCount)
    {
        var beforeChangedPositions = new bool[beforeTexts.Length];
        var afterChangedPositions = new bool[afterTexts.Length];
        var remainingComparisonWork = _maxComparisonWork;
        patienceAnchorScanCount = 0;
        MarkBoundedChanges(
            beforeTexts,
            0,
            beforeTexts.Length,
            afterTexts,
            0,
            afterTexts.Length,
            beforeChangedPositions,
            afterChangedPositions,
            ref remainingComparisonWork,
            ref patienceAnchorScanCount);
        return BuildPlanFromChangedPositions(
            beforeBlocks,
            afterBlocks,
            beforeTexts,
            afterTexts,
            beforeChangedPositions,
            afterChangedPositions);
    }

    private static void MarkBoundedChanges(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd,
        bool[] beforeChanged,
        bool[] afterChanged,
        ref long remainingComparisonWork,
        ref int patienceAnchorScanCount)
    {
        var pending = new Stack<BlockRange>();
        pending.Push(new BlockRange(beforeStart, beforeEnd, afterStart, afterEnd));
        while (pending.TryPop(out var range))
        {
            beforeStart = range.BeforeStart;
            beforeEnd = range.BeforeEnd;
            afterStart = range.AfterStart;
            afterEnd = range.AfterEnd;
            while (beforeStart < beforeEnd && afterStart < afterEnd)
            {
                if (!TryReserveLinearWork(ref remainingComparisonWork, 1))
                {
                    MarkBudgetedFallbackRange(
                        beforeTexts,
                        beforeStart,
                        beforeEnd,
                        afterTexts,
                        afterStart,
                        afterEnd,
                        beforeChanged,
                        afterChanged,
                        ref remainingComparisonWork);
                    goto NextRange;
                }
                if (!string.Equals(beforeTexts[beforeStart], afterTexts[afterStart], StringComparison.Ordinal))
                {
                    break;
                }
                beforeStart++;
                afterStart++;
            }
            while (beforeStart < beforeEnd && afterStart < afterEnd)
            {
                if (!TryReserveLinearWork(ref remainingComparisonWork, 1))
                {
                    MarkBudgetedFallbackRange(
                        beforeTexts,
                        beforeStart,
                        beforeEnd,
                        afterTexts,
                        afterStart,
                        afterEnd,
                        beforeChanged,
                        afterChanged,
                        ref remainingComparisonWork);
                    goto NextRange;
                }
                if (!string.Equals(beforeTexts[beforeEnd - 1], afterTexts[afterEnd - 1], StringComparison.Ordinal))
                {
                    break;
                }
                beforeEnd--;
                afterEnd--;
            }
            if (beforeStart == beforeEnd || afterStart == afterEnd)
            {
                Array.Fill(beforeChanged, true, beforeStart, beforeEnd - beforeStart);
                Array.Fill(afterChanged, true, afterStart, afterEnd - afterStart);
                continue;
            }

            var beforeLength = beforeEnd - beforeStart;
            var afterLength = afterEnd - afterStart;
            if (!ExceedsPlanCellBudget(beforeLength, afterLength))
            {
                if (TryReserveComparisonWork(ref remainingComparisonWork, beforeLength, afterLength))
                {
                    MarkLcsChanges(
                        beforeTexts,
                        beforeStart,
                        beforeEnd,
                        afterTexts,
                        afterStart,
                        afterEnd,
                        beforeChanged,
                        afterChanged);
                }
                else
                {
                    MarkBudgetedFallbackRange(
                        beforeTexts,
                        beforeStart,
                        beforeEnd,
                        afterTexts,
                        afterStart,
                        afterEnd,
                        beforeChanged,
                        afterChanged,
                        ref remainingComparisonWork);
                }
                continue;
            }

            if (!TryReserveLinearWork(
                ref remainingComparisonWork,
                EstimatePatienceAnchorWork(beforeLength, afterLength)))
            {
                MarkBudgetedFallbackRange(
                    beforeTexts,
                    beforeStart,
                    beforeEnd,
                    afterTexts,
                    afterStart,
                    afterEnd,
                    beforeChanged,
                    afterChanged,
                    ref remainingComparisonWork);
                continue;
            }
            patienceAnchorScanCount++;
            var anchors = FindPatienceAnchors(
                beforeTexts,
                beforeStart,
                beforeEnd,
                afterTexts,
                afterStart,
                afterEnd);
            if (anchors.Count == 0)
            {
                Array.Fill(beforeChanged, true, beforeStart, beforeLength);
                Array.Fill(afterChanged, true, afterStart, afterLength);
                MarkBudgetedFallbackMatches(
                    beforeTexts,
                    beforeStart,
                    beforeEnd,
                    afterTexts,
                    afterStart,
                    afterEnd,
                    beforeChanged,
                    afterChanged,
                    ref remainingComparisonWork);
                continue;
            }

            var beforeCursor = beforeStart;
            var afterCursor = afterStart;
            var partitions = new List<BlockRange>(anchors.Count + 1);
            foreach (var anchor in anchors)
            {
                partitions.Add(new BlockRange(
                    beforeCursor,
                    anchor.BeforeIndex,
                    afterCursor,
                    anchor.AfterIndex));
                beforeCursor = anchor.BeforeIndex + 1;
                afterCursor = anchor.AfterIndex + 1;
            }
            partitions.Add(new BlockRange(beforeCursor, beforeEnd, afterCursor, afterEnd));
            for (var index = partitions.Count - 1; index >= 0; index--)
            {
                pending.Push(partitions[index]);
            }

        NextRange:
            continue;
        }
    }

    private static void MarkBudgetedFallbackRange(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd,
        bool[] beforeChanged,
        bool[] afterChanged,
        ref long remainingComparisonWork)
    {
        Array.Fill(beforeChanged, true, beforeStart, beforeEnd - beforeStart);
        Array.Fill(afterChanged, true, afterStart, afterEnd - afterStart);
        MarkBudgetedFallbackMatches(
            beforeTexts,
            beforeStart,
            beforeEnd,
            afterTexts,
            afterStart,
            afterEnd,
            beforeChanged,
            afterChanged,
            ref remainingComparisonWork);
    }

    private static void MarkLcsChanges(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd,
        bool[] beforeChanged,
        bool[] afterChanged)
    {
        var beforeLength = beforeEnd - beforeStart;
        var afterLength = afterEnd - afterStart;
        var lengths = new int[beforeLength + 1, afterLength + 1];
        for (var beforeIndex = beforeLength - 1; beforeIndex >= 0; beforeIndex--)
        {
            for (var afterIndex = afterLength - 1; afterIndex >= 0; afterIndex--)
            {
                lengths[beforeIndex, afterIndex] = string.Equals(
                    beforeTexts[beforeStart + beforeIndex],
                    afterTexts[afterStart + afterIndex],
                    StringComparison.Ordinal)
                        ? lengths[beforeIndex + 1, afterIndex + 1] + 1
                        : Math.Max(lengths[beforeIndex + 1, afterIndex], lengths[beforeIndex, afterIndex + 1]);
            }
        }

        var beforeCursor = 0;
        var afterCursor = 0;
        while (beforeCursor < beforeLength && afterCursor < afterLength)
        {
            if (string.Equals(
                beforeTexts[beforeStart + beforeCursor],
                afterTexts[afterStart + afterCursor],
                StringComparison.Ordinal))
            {
                beforeCursor++;
                afterCursor++;
            }
            else if (lengths[beforeCursor + 1, afterCursor] >= lengths[beforeCursor, afterCursor + 1])
            {
                beforeChanged[beforeStart + beforeCursor++] = true;
            }
            else
            {
                afterChanged[afterStart + afterCursor++] = true;
            }
        }
        Array.Fill(beforeChanged, true, beforeStart + beforeCursor, beforeLength - beforeCursor);
        Array.Fill(afterChanged, true, afterStart + afterCursor, afterLength - afterCursor);
    }

    private static void MarkBudgetedFallbackMatches(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd,
        bool[] beforeChanged,
        bool[] afterChanged,
        ref long remainingComparisonWork)
    {
        if (!TryMarkMyersMatches(
            beforeTexts,
            beforeStart,
            beforeEnd,
            afterTexts,
            afterStart,
            afterEnd,
            beforeChanged,
            afterChanged,
            ref remainingComparisonWork))
        {
            MarkPositionallyAlignedMatches(
                beforeTexts,
                beforeStart,
                beforeEnd,
                afterTexts,
                afterStart,
                afterEnd,
                beforeChanged,
                afterChanged);
        }
    }

    private static bool TryMarkMyersMatches(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd,
        bool[] beforeChanged,
        bool[] afterChanged,
        ref long remainingComparisonWork)
    {
        var beforeLength = beforeEnd - beforeStart;
        var afterLength = afterEnd - afterStart;
        var maximumDistance = beforeLength + afterLength;
        var layers = new List<int[]>(Math.Min(maximumDistance + 1, 4096));
        long consumedWork = 0;

        for (var distance = 0; distance <= maximumDistance; distance++)
        {
            var current = new int[(distance * 2) + 1];
            var previous = distance == 0 ? null : layers[distance - 1];
            for (var diagonal = -distance; diagonal <= distance; diagonal += 2)
            {
                if (++consumedWork > remainingComparisonWork)
                {
                    remainingComparisonWork = 0;
                    return false;
                }

                int beforeOffset;
                if (distance == 0)
                {
                    beforeOffset = 0;
                }
                else if (diagonal == -distance
                    || (diagonal != distance
                        && GetMyersLayerValue(previous!, distance - 1, diagonal - 1)
                            < GetMyersLayerValue(previous!, distance - 1, diagonal + 1)))
                {
                    beforeOffset = GetMyersLayerValue(previous!, distance - 1, diagonal + 1);
                }
                else
                {
                    beforeOffset = GetMyersLayerValue(previous!, distance - 1, diagonal - 1) + 1;
                }

                var afterOffset = beforeOffset - diagonal;
                while (beforeOffset < beforeLength && afterOffset < afterLength)
                {
                    if (++consumedWork > remainingComparisonWork)
                    {
                        remainingComparisonWork = 0;
                        return false;
                    }
                    if (!string.Equals(
                        beforeTexts[beforeStart + beforeOffset],
                        afterTexts[afterStart + afterOffset],
                        StringComparison.Ordinal))
                    {
                        break;
                    }
                    beforeOffset++;
                    afterOffset++;
                }
                current[diagonal + distance] = beforeOffset;
                if (beforeOffset >= beforeLength && afterOffset >= afterLength)
                {
                    layers.Add(current);
                    remainingComparisonWork -= consumedWork;
                    MarkMyersBacktrackMatches(
                        beforeTexts,
                        beforeStart,
                        afterTexts,
                        afterStart,
                        beforeLength,
                        afterLength,
                        layers,
                        beforeChanged,
                        afterChanged);
                    return true;
                }
            }
            layers.Add(current);
        }

        remainingComparisonWork -= consumedWork;
        return false;
    }

    private static void MarkMyersBacktrackMatches(
        string[] beforeTexts,
        int beforeStart,
        string[] afterTexts,
        int afterStart,
        int beforeLength,
        int afterLength,
        List<int[]> layers,
        bool[] beforeChanged,
        bool[] afterChanged)
    {
        var beforeOffset = beforeLength;
        var afterOffset = afterLength;
        for (var distance = layers.Count - 1; distance > 0; distance--)
        {
            var diagonal = beforeOffset - afterOffset;
            var previous = layers[distance - 1];
            var previousDiagonal = diagonal == -distance
                || (diagonal != distance
                    && GetMyersLayerValue(previous, distance - 1, diagonal - 1)
                        < GetMyersLayerValue(previous, distance - 1, diagonal + 1))
                    ? diagonal + 1
                    : diagonal - 1;
            var previousBeforeOffset = GetMyersLayerValue(previous, distance - 1, previousDiagonal);
            var previousAfterOffset = previousBeforeOffset - previousDiagonal;
            while (beforeOffset > previousBeforeOffset && afterOffset > previousAfterOffset)
            {
                beforeOffset--;
                afterOffset--;
                if (!string.Equals(
                    beforeTexts[beforeStart + beforeOffset],
                    afterTexts[afterStart + afterOffset],
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Myers preview diff backtrack produced a non-matching diagonal.");
                }
                beforeChanged[beforeStart + beforeOffset] = false;
                afterChanged[afterStart + afterOffset] = false;
            }
            if (beforeOffset == previousBeforeOffset)
            {
                afterOffset--;
            }
            else
            {
                beforeOffset--;
            }
        }
        while (beforeOffset > 0 && afterOffset > 0)
        {
            beforeOffset--;
            afterOffset--;
            beforeChanged[beforeStart + beforeOffset] = false;
            afterChanged[afterStart + afterOffset] = false;
        }
    }

    private static int GetMyersLayerValue(int[] layer, int distance, int diagonal)
        => layer[diagonal + distance];

    private static void MarkPositionallyAlignedMatches(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd,
        bool[] beforeChanged,
        bool[] afterChanged)
    {
        var pairCount = Math.Min(beforeEnd - beforeStart, afterEnd - afterStart);
        for (var offset = 0; offset < pairCount; offset++)
        {
            if (string.Equals(
                beforeTexts[beforeStart + offset],
                afterTexts[afterStart + offset],
                StringComparison.Ordinal))
            {
                beforeChanged[beforeStart + offset] = false;
                afterChanged[afterStart + offset] = false;
            }
        }
    }

    private static List<BlockAnchor> FindPatienceAnchors(
        string[] beforeTexts,
        int beforeStart,
        int beforeEnd,
        string[] afterTexts,
        int afterStart,
        int afterEnd)
    {
        var beforeOccurrences = BuildTextOccurrences(beforeTexts, beforeStart, beforeEnd);
        var afterOccurrences = BuildTextOccurrences(afterTexts, afterStart, afterEnd);
        var candidates = new List<BlockAnchor>();
        for (var index = beforeStart; index < beforeEnd; index++)
        {
            var text = beforeTexts[index];
            if (beforeOccurrences[text].Count == 1
                && afterOccurrences.TryGetValue(text, out var afterOccurrence)
                && afterOccurrence.Count == 1)
            {
                candidates.Add(new BlockAnchor(index, afterOccurrence.Index));
            }
        }
        if (candidates.Count <= 1)
        {
            return candidates;
        }

        var tails = new int[candidates.Count];
        var predecessors = new int[candidates.Count];
        Array.Fill(predecessors, -1);
        var length = 0;
        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = low + ((high - low) / 2);
                if (candidates[tails[middle]].AfterIndex < candidates[candidateIndex].AfterIndex)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            if (low > 0)
            {
                predecessors[candidateIndex] = tails[low - 1];
            }
            tails[low] = candidateIndex;
            if (low == length)
            {
                length++;
            }
        }

        var anchors = new List<BlockAnchor>(length);
        var current = tails[length - 1];
        while (current >= 0)
        {
            anchors.Add(candidates[current]);
            current = predecessors[current];
        }
        anchors.Reverse();
        return anchors;
    }

    private static Dictionary<string, (int Count, int Index)> BuildTextOccurrences(
        string[] texts,
        int start,
        int end)
    {
        var occurrences = new Dictionary<string, (int Count, int Index)>(StringComparer.Ordinal);
        for (var index = start; index < end; index++)
        {
            var text = texts[index];
            occurrences[text] = occurrences.TryGetValue(text, out var occurrence)
                ? (occurrence.Count + 1, occurrence.Index)
                : (1, index);
        }
        return occurrences;
    }

    private static PreviewDiffPlan BuildPlanFromChangedPositions(
        IReadOnlyList<PreviewDiffBlock> beforeBlocks,
        IReadOnlyList<PreviewDiffBlock> afterBlocks,
        string[] beforeTexts,
        string[] afterTexts,
        bool[] beforeChangedPositions,
        bool[] afterChangedPositions)
    {
        var beforeChanged = new List<int>();
        var afterChanged = new List<int>();
        var changes = new List<PreviewDiffChange>();
        var currentBeforeIndexes = new List<int>();
        var currentAfterIndexes = new List<int>();
        var beforeCursor = 0;
        var afterCursor = 0;
        var beforeAlignmentGroups = BuildAlignmentGroupsByIndex(beforeBlocks);
        var afterAlignmentGroups = BuildAlignmentGroupsByIndex(afterBlocks);
        void FlushChange()
        {
            if (currentBeforeIndexes.Count == 0 && currentAfterIndexes.Count == 0)
            {
                return;
            }

            var advancePastCurrentAlignmentGroup =
                IsAnchorInChangedAlignmentGroup(
                    beforeBlocks,
                    beforeAlignmentGroups,
                    beforeCursor,
                    currentBeforeIndexes)
                || IsAnchorInChangedAlignmentGroup(
                    afterBlocks,
                    afterAlignmentGroups,
                    afterCursor,
                    currentAfterIndexes);
            changes.Add(new PreviewDiffChange(
                currentBeforeIndexes.ToArray(),
                currentAfterIndexes.ToArray(),
                FindAlignmentAnchorIndex(
                    beforeBlocks,
                    beforeAlignmentGroups,
                    beforeCursor,
                    currentBeforeIndexes,
                    advancePastCurrentAlignmentGroup),
                FindAlignmentAnchorIndex(
                    afterBlocks,
                    afterAlignmentGroups,
                    afterCursor,
                    currentAfterIndexes,
                    advancePastCurrentAlignmentGroup)));
            currentBeforeIndexes.Clear();
            currentAfterIndexes.Clear();
        }

        while (beforeCursor < beforeBlocks.Count || afterCursor < afterBlocks.Count)
        {
            if (beforeCursor < beforeBlocks.Count
                && afterCursor < afterBlocks.Count
                && !beforeChangedPositions[beforeCursor]
                && !afterChangedPositions[afterCursor]
                && string.Equals(beforeTexts[beforeCursor], afterTexts[afterCursor], StringComparison.Ordinal))
            {
                FlushChange();
                beforeCursor++;
                afterCursor++;
            }
            else if (beforeCursor < beforeBlocks.Count && beforeChangedPositions[beforeCursor])
            {
                beforeChanged.Add(beforeBlocks[beforeCursor].Index);
                currentBeforeIndexes.Add(beforeBlocks[beforeCursor].Index);
                beforeCursor++;
            }
            else if (afterCursor < afterBlocks.Count && afterChangedPositions[afterCursor])
            {
                afterChanged.Add(afterBlocks[afterCursor].Index);
                currentAfterIndexes.Add(afterBlocks[afterCursor].Index);
                afterCursor++;
            }
            else
            {
                throw new InvalidOperationException("Bounded preview diff produced an unaligned unchanged block.");
            }
        }
        FlushChange();
        return new PreviewDiffPlan(beforeChanged, afterChanged, changes);
    }

    private static bool ExceedsPlanCellBudget(
        int beforeBlockCount,
        int afterBlockCount,
        long maximumCellCount = _maxPlanCells)
        => ((long)beforeBlockCount + 1) * (afterBlockCount + 1) > maximumCellCount;

    private static bool TryReserveComparisonWork(
        ref long remainingComparisonWork,
        int beforeBlockCount,
        int afterBlockCount)
    {
        var requestedWork = (long)beforeBlockCount * afterBlockCount;
        if (requestedWork > remainingComparisonWork)
        {
            return false;
        }

        remainingComparisonWork -= requestedWork;
        return true;
    }

    private static bool TryReserveLinearWork(ref long remainingComparisonWork, long requestedWork)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(requestedWork);
        if (requestedWork > remainingComparisonWork)
        {
            return false;
        }

        remainingComparisonWork -= requestedWork;
        return true;
    }

    private static long EstimatePatienceAnchorWork(int beforeBlockCount, int afterBlockCount)
    {
        var maximumCandidateCount = Math.Min(beforeBlockCount, afterBlockCount);
        var binarySearchSteps = 0;
        for (var remainingCandidates = maximumCandidateCount; remainingCandidates > 0; remainingCandidates >>= 1)
        {
            binarySearchSteps++;
        }

        return (2L * (beforeBlockCount + (long)afterBlockCount))
            + ((long)maximumCandidateCount * binarySearchSteps);
    }

    private static int? FindAlignmentAnchorIndex(
        IReadOnlyList<PreviewDiffBlock> blocks,
        IReadOnlyDictionary<int, string?> alignmentGroupsByIndex,
        int cursor,
        IReadOnlyCollection<int> changedIndexes,
        bool advancePastCurrentAlignmentGroup)
    {
        if (cursor >= blocks.Count)
        {
            return null;
        }

        var changedGroups = changedIndexes
            .Select(index => alignmentGroupsByIndex.GetValueOrDefault(index))
            .Where(static group => group is not null)
            .Select(static group => group!)
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
        IReadOnlyDictionary<int, string?> alignmentGroupsByIndex,
        int cursor,
        IReadOnlyCollection<int> changedIndexes)
    {
        if (cursor >= blocks.Count || blocks[cursor].AlignmentGroup is not { } anchorGroup)
        {
            return false;
        }

        return changedIndexes.Any(index =>
            string.Equals(
                alignmentGroupsByIndex.GetValueOrDefault(index),
                anchorGroup,
                StringComparison.Ordinal));
    }

    private static Dictionary<int, string?> BuildAlignmentGroupsByIndex(
        IReadOnlyList<PreviewDiffBlock> blocks)
        => blocks.ToDictionary(static block => block.Index, static block => block.AlignmentGroup);

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
        var alignmentChanges = CoalesceChangesForAlignment(changes);

        var beforeAnchorsJson = JsonSerializer.Serialize(
            alignmentChanges.Select(static change => change.BeforeAnchorIndex),
            _jsonOptions);
        var afterAnchorsJson = JsonSerializer.Serialize(
            alignmentChanges.Select(static change => change.AfterAnchorIndex),
            _jsonOptions);
        var beforeCodeWrappingIndexesJson = JsonSerializer.Serialize(
            GetCodeWrappingCandidateIndexes(alignmentChanges, PreviewDiffPane.Before),
            _jsonOptions);
        var afterCodeWrappingIndexesJson = JsonSerializer.Serialize(
            GetCodeWrappingCandidateIndexes(alignmentChanges, PreviewDiffPane.After),
            _jsonOptions);
        var measurements = await Task.WhenAll(
            beforeView.ExecuteScriptAsync(BuildMeasureAlignmentAnchorsScript(
                beforeAnchorsJson,
                beforeCodeWrappingIndexesJson)),
            afterView.ExecuteScriptAsync(BuildMeasureAlignmentAnchorsScript(
                afterAnchorsJson,
                afterCodeWrappingIndexesJson)));
        var beforeMeasurement = DeserializeMeasurement(measurements[0]);
        var afterMeasurement = DeserializeMeasurement(measurements[1]);
        if (isCurrent is not null && !isCurrent())
        {
            return;
        }

        if (beforeMeasurement.Offsets.Length != alignmentChanges.Count
            || afterMeasurement.Offsets.Length != alignmentChanges.Count
            || beforeMeasurement.Offsets.Any(static offset => offset is null)
            || afterMeasurement.Offsets.Any(static offset => offset is null))
        {
            return;
        }

        var gapPlan = BuildAlignmentGapPlan(
            alignmentChanges,
            beforeMeasurement.Offsets.Select(static offset => offset!.Value).ToArray(),
            afterMeasurement.Offsets.Select(static offset => offset!.Value).ToArray());
        gapPlan = AddAlignmentNavigationPlaceholders(gapPlan, changes);
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

    internal static IReadOnlyList<PreviewDiffChange> CoalesceChangesForAlignment(
        IReadOnlyList<PreviewDiffChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count <= MaximumAlignedChangeCount)
        {
            return changes;
        }

        var batchSize = (int)Math.Ceiling(changes.Count / (double)MaximumAlignedChangeCount);
        var coalesced = new List<PreviewDiffChange>(
            (changes.Count + batchSize - 1) / batchSize);
        for (var start = 0; start < changes.Count; start += batchSize)
        {
            var count = Math.Min(batchSize, changes.Count - start);
            var batch = changes.Skip(start).Take(count);
            var last = changes[start + count - 1];
            var navigationIndexes = Enumerable.Range(start, count)
                .SelectMany(index => changes[index].AlignmentNavigationIndexes ?? [index])
                .ToArray();
            coalesced.Add(new PreviewDiffChange(
                batch.SelectMany(static change => change.BeforeIndexes).ToArray(),
                batch.SelectMany(static change => change.AfterIndexes).ToArray(),
                last.BeforeAnchorIndex,
                last.AfterAnchorIndex,
                navigationIndexes));
        }
        return coalesced;
    }

    internal static double ResolveSynchronizedScrollTop(double beforeScrollTop, double afterScrollTop)
        => Math.Max(Math.Max(0, beforeScrollTop), afterScrollTop);

    internal static double ResolveAppliedSynchronizedScrollTop(
        double beforeScrollTop,
        double afterScrollTop)
        => Math.Min(Math.Max(0, beforeScrollTop), Math.Max(0, afterScrollTop));

    internal static PreviewDiffAlignmentGapPlan AddAlignmentNavigationPlaceholders(
        PreviewDiffAlignmentGapPlan gapPlan,
        IReadOnlyList<PreviewDiffChange> changes)
    {
        ArgumentNullException.ThrowIfNull(gapPlan);
        ArgumentNullException.ThrowIfNull(changes);
        var before = gapPlan.Before.ToList();
        var after = gapPlan.After.ToList();
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            if (change.BeforeIndexes.Count == 0)
            {
                before.Add(new PreviewDiffAlignmentGap(
                    change.BeforeAnchorIndex,
                    0,
                    index,
                    NavigationOnly: true));
            }
            if (change.AfterIndexes.Count == 0)
            {
                after.Add(new PreviewDiffAlignmentGap(
                    change.AfterAnchorIndex,
                    0,
                    index,
                    NavigationOnly: true));
            }
        }
        return new PreviewDiffAlignmentGapPlan(before, after);
    }

    internal static IReadOnlyList<int> GetCodeWrappingCandidateIndexes(
        IReadOnlyList<PreviewDiffChange> changes,
        PreviewDiffPane pane)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var indexes = new List<int>();
        var seen = new HashSet<int>();
        foreach (var change in changes)
        {
            var changedIndexes = pane == PreviewDiffPane.Before
                ? change.BeforeIndexes
                : change.AfterIndexes;
            foreach (var index in changedIndexes)
            {
                if (seen.Add(index))
                {
                    indexes.Add(index);
                }
            }

            var anchorIndex = pane == PreviewDiffPane.Before
                ? change.BeforeAnchorIndex
                : change.AfterAnchorIndex;
            if (anchorIndex is { } anchor)
            {
                if (anchor > 0 && seen.Add(anchor - 1))
                {
                    indexes.Add(anchor - 1);
                }
                if (seen.Add(anchor))
                {
                    indexes.Add(anchor);
                }
            }
            else if (seen.Add(-1))
            {
                indexes.Add(-1);
            }
        }

        return indexes;
    }

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
            var navigationIndexes = change.AlignmentNavigationIndexes ?? [index];
            var navigationIndex = navigationIndexes[^1];
            if (delta < 0)
            {
                var height = -delta;
                beforeGaps.Add(new PreviewDiffAlignmentGap(
                    change.BeforeAnchorIndex,
                    height,
                    navigationIndex));
                cumulativeBeforeGap += height;
            }
            else
            {
                afterGaps.Add(new PreviewDiffAlignmentGap(
                    change.AfterAnchorIndex,
                    delta,
                    navigationIndex));
                cumulativeAfterGap += delta;
            }
        }

        return new PreviewDiffAlignmentGapPlan(beforeGaps, afterGaps);
    }

    internal static string BuildMeasureAlignmentAnchorsScript(
        string anchorIndexesJson,
        string codeWrappingIndexesJson = "[]")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorIndexesJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeWrappingIndexesJson);
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
  const existingGapElements = Array.from(
    document.querySelectorAll('.rsr-preview-diff-alignment-gap-row,.rsr-preview-diff-alignment-gap'));
  existingGapElements.forEach((element) => {
    element.style.setProperty('display', 'none', 'important');
  });
  const restoreExistingGaps = () => {
    existingGapElements.forEach((element) => element.style.removeProperty('display'));
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
  const codeWrapStyleId = 'rsr-preview-diff-code-wrap-style';
  if (!document.getElementById(codeWrapStyleId)) {
    const style = document.createElement('style');
    style.id = codeWrapStyleId;
    style.textContent = `
html, body {
  overflow-anchor: none !important;
}
pre.rsr-preview-diff-aligned-code {
  overflow-wrap: anywhere !important;
  white-space: pre-wrap !important;
}
pre.rsr-preview-diff-aligned-code code {
  overflow-wrap: inherit !important;
  white-space: inherit !important;
}
`;
    document.head.appendChild(style);
  }
  root.querySelectorAll('pre.rsr-preview-diff-aligned-code').forEach((element) => {
    element.classList.remove('rsr-preview-diff-aligned-code');
  });
  const diffElements = Array.from(root.querySelectorAll('[data-rsr-diff-index]'));
  const diffElementsByIndex = new Map(
    diffElements.map((element) => [Number(element.getAttribute('data-rsr-diff-index')), element]));
  {{codeWrappingIndexesJson}}.forEach((index) => {
    const target = index >= 0
      ? diffElementsByIndex.get(index)
      : diffElements.at(-1);
    const codeBlock = target?.matches('pre') ? target : target?.closest('pre');
    codeBlock?.classList.add('rsr-preview-diff-aligned-code');
  });
  const offsets = {{anchorIndexesJson}}.map((anchorIndex) => {
      if (anchorIndex === null) {
        return root.getBoundingClientRect().bottom + window.scrollY;
      }
      const anchor = diffElementsByIndex.get(anchorIndex);
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
.rsr-preview-diff-alignment-gap {
  background-clip: content-box !important;
  background-color: var(--rsr-preview-gap-bg) !important;
  background-image: repeating-linear-gradient(
    -45deg,
    transparent 0 6px,
    var(--rsr-preview-gap-stripe) 6px 8px) !important;
  border-block-start: 1px solid var(--rsr-preview-gap-border) !important;
  box-sizing: border-box !important;
  display: block !important;
  list-style: none !important;
  margin: 0 !important;
  padding: 0 !important;
  padding-block-end: var(--rsr-preview-gap-separator) !important;
  pointer-events: none !important;
  width: 100% !important;
}
td.rsr-preview-diff-alignment-gap {
  display: table-cell !important;
  width: auto !important;
}
`;
    document.head.appendChild(style);
  }

  document.querySelectorAll(
    '.rsr-preview-diff-alignment-gap-section,' +
    '.rsr-preview-diff-alignment-gap-row,' +
    '.rsr-preview-diff-alignment-gap').forEach((element) => {
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

  const setGapHeight = (element, height) => {
    const separatorHeight = Math.min(6, Math.max(0, height - 1));
    element.style.setProperty(
      '--rsr-preview-gap-separator',
      `${separatorHeight.toFixed(2)}px`);
    element.style.height = `${height.toFixed(2)}px`;
  };
  const createGap = (height, navigationIndex, tagName = 'div') => {
    const element = document.createElement(tagName);
    element.className = 'rsr-preview-diff-alignment-gap';
    element.setAttribute('aria-hidden', 'true');
    element.setAttribute('role', 'presentation');
    element.setAttribute('data-rsr-diff-navigation-index', String(navigationIndex));
    setGapHeight(element, height);
    return element;
  };
  const createNavigationPlaceholder = (navigationIndex, tagName = 'div') => {
    const container = document.createElement(tagName);
    container.className = 'rsr-preview-diff-alignment-gap';
    container.setAttribute('aria-hidden', 'true');
    container.setAttribute('role', 'presentation');
    container.style.height = '0';
    container.style.position = 'relative';
    const target = document.createElement('span');
    target.setAttribute('data-rsr-diff-navigation-index', String(navigationIndex));
    target.style.display = 'block';
    target.style.height = '1px';
    target.style.inset = '0 0 auto';
    target.style.position = 'absolute';
    container.appendChild(target);
    return container;
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
      setGapHeight(gap, renderedHeight);
    }
  };
  const insertGapAfter = (context, gap, desiredHeight) => {
    const rootBottomBefore = root.getBoundingClientRect().bottom;
    context.parentNode.insertBefore(gap, context.nextSibling);
    let renderedHeight = desiredHeight;
    for (let attempt = 0; attempt < 3; attempt++) {
      const actualDisplacement = root.getBoundingClientRect().bottom - rootBottomBefore;
      const correction = desiredHeight - actualDisplacement;
      if (Math.abs(correction) <= 0.1) {
        break;
      }
      renderedHeight = Math.max(1, renderedHeight + correction);
      setGapHeight(gap, renderedHeight);
    }
  };
  const insertTableGapBefore = (row, gapRow, gapCell, desiredHeight) => {
    const rowTopBefore = row.getBoundingClientRect().top;
    row.parentNode.insertBefore(gapRow, row);
    let renderedHeight = desiredHeight;
    for (let attempt = 0; attempt < 3; attempt++) {
      const actualDisplacement = row.getBoundingClientRect().top - rowTopBefore;
      const correction = desiredHeight - actualDisplacement;
      if (Math.abs(correction) <= 0.1) {
        break;
      }
      renderedHeight = Math.max(1, renderedHeight + correction);
      setGapHeight(gapCell, renderedHeight);
    }
  };
  const insertTableGapAfter = (row, gapSection, gapCell, desiredHeight) => {
    const rootBottomBefore = root.getBoundingClientRect().bottom;
    const rowGroup = row.parentNode;
    const table = row.closest('table');
    table.insertBefore(gapSection, rowGroup.nextSibling);
    let renderedHeight = desiredHeight;
    for (let attempt = 0; attempt < 3; attempt++) {
      const actualDisplacement = root.getBoundingClientRect().bottom - rootBottomBefore;
      const correction = desiredHeight - actualDisplacement;
      if (Math.abs(correction) <= 0.1) {
        break;
      }
      renderedHeight = Math.max(1, renderedHeight + correction);
      setGapHeight(gapCell, renderedHeight);
    }
  };
  const tableColumnCounts = new WeakMap();
  const getTableColumnCount = (table) => {
    if (!table) {
      return 1;
    }
    const cachedColumnCount = tableColumnCounts.get(table);
    if (cachedColumnCount !== undefined) {
      return cachedColumnCount;
    }
    const rows = Array.from(table.rows);
    const sectionEndRowIndexes = new Map();
    rows.forEach((tableRow, rowIndex) => {
      sectionEndRowIndexes.set(tableRow.parentElement, rowIndex);
    });
    const activeRowSpans = [];
    let widestColumnCount = 1;
    rows.forEach((tableRow, rowIndex) => {
      const sectionEndRowIndex =
        sectionEndRowIndexes.get(tableRow.parentElement) ?? rowIndex;
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
    tableColumnCounts.set(table, widestColumnCount);
    return widestColumnCount;
  };
  const gaps = {{gapsJson}};
  const diffElements = Array.from(root.querySelectorAll('[data-rsr-diff-index]'));
  const diffElementsByIndex = new Map(
    diffElements.map((element) => [Number(element.getAttribute('data-rsr-diff-index')), element]));
  gaps.forEach((gap) => {
    const height = gap.navigationOnly ? 0 : Math.max(1, Number(gap.height) || 0);
    const anchor = gap.anchorIndex === null
      ? null
      : diffElementsByIndex.get(gap.anchorIndex);
    const row = anchor?.closest('tr');
    if (gap.navigationOnly) {
      if (row?.parentNode) {
        const placeholderRow = document.createElement('tr');
        placeholderRow.className = 'rsr-preview-diff-alignment-gap-row';
        placeholderRow.setAttribute('aria-hidden', 'true');
        placeholderRow.setAttribute('role', 'presentation');
        const placeholderCell = createNavigationPlaceholder(gap.navigationIndex, 'td');
        placeholderCell.colSpan = getTableColumnCount(row.closest('table'));
        placeholderRow.appendChild(placeholderCell);
        row.parentNode.insertBefore(placeholderRow, row);
      } else {
        const placeholder = createNavigationPlaceholder(gap.navigationIndex);
        if (anchor?.parentNode) {
          anchor.parentNode.insertBefore(placeholder, anchor);
        } else {
          root.appendChild(placeholder);
        }
      }
      return;
    }
    if (row?.parentNode) {
      const gapRow = document.createElement('tr');
      gapRow.className = 'rsr-preview-diff-alignment-gap-row';
      gapRow.setAttribute('aria-hidden', 'true');
      gapRow.setAttribute('role', 'presentation');
      const gapCell = createGap(height, gap.navigationIndex, 'td');
      const table = row.closest('table');
      gapCell.colSpan = getTableColumnCount(table);
      gapRow.appendChild(gapCell);
      insertTableGapBefore(row, gapRow, gapCell, height);
      return;
    }
    if (anchor?.parentNode) {
      const tagName = anchor.matches('.rsr-code-line') ? 'span' : 'div';
      insertGapBefore(
        anchor,
        createGap(height, gap.navigationIndex, tagName),
        height);
      return;
    }
    const terminalElement =
      diffElements.at(-1);
    const terminalRow = terminalElement?.closest('tr');
    if (terminalRow?.parentNode) {
      const table = terminalRow.closest('table');
      if (terminalRow.parentElement?.matches('tfoot')) {
        insertGapAfter(
          table,
          createGap(height, gap.navigationIndex),
          height);
        return;
      }
      const gapSection = document.createElement('tbody');
      gapSection.className = 'rsr-preview-diff-alignment-gap-section';
      gapSection.setAttribute('aria-hidden', 'true');
      gapSection.setAttribute('role', 'presentation');
      const gapRow = document.createElement('tr');
      gapRow.className = 'rsr-preview-diff-alignment-gap-row';
      gapRow.setAttribute('aria-hidden', 'true');
      gapRow.setAttribute('role', 'presentation');
      const gapCell = createGap(height, gap.navigationIndex, 'td');
      gapCell.colSpan = getTableColumnCount(table);
      gapRow.appendChild(gapCell);
      gapSection.appendChild(gapRow);
      insertTableGapAfter(terminalRow, gapSection, gapCell, height);
      return;
    }
    const terminalMediaSelector = 'img,video,audio,iframe,object,embed';
    const terminalContext = terminalElement?.matches(terminalMediaSelector)
      ? terminalElement.closest('li') ||
        terminalElement.closest('p,figure') ||
        terminalElement.closest('picture,object') ||
        terminalElement
      : terminalElement;
    if (terminalContext?.parentNode) {
      const tagName = terminalContext.matches('.rsr-code-line')
        ? 'span'
        : terminalContext.matches('li') ? 'li' : 'div';
      insertGapAfter(
        terminalContext,
        createGap(height, gap.navigationIndex, tagName),
        height);
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

    internal static double? DeserializeDouble(string? scriptResult)
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
  window.__repoSyncRadarDiffScrollbar?.disable?.();
  document.getElementById('rsr-diff-scrollbar')?.remove();

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

  document.querySelectorAll('.rsr-preview-diff-target,[data-rsr-diff-navigation-index]').forEach((element) => {
    element.classList.remove(
      'rsr-preview-diff-target',
      'rsr-preview-diff-block',
      'rsr-preview-diff-before',
      'rsr-preview-diff-after');
    element.removeAttribute('data-rsr-diff-navigation-index');
  });

  const changedIndexes = new Set({{indexesJson}});
  const navigationIndexes = new Map(
    {{navigationTargetsJson}}.map((target) => [target.index, target.navigationIndex]));
  const pane = {{paneJson}};
  const renderedDiffSelector = '.rsr-rendered-diff-added,.rsr-rendered-diff-removed';
  const diffElementsByIndex = new Map(
    Array.from(document.querySelectorAll('[data-rsr-diff-index]'))
      .map((element) => [Number(element.getAttribute('data-rsr-diff-index')), element]));
  changedIndexes.forEach((index) => {
    const element = diffElementsByIndex.get(index);
    if (!element) {
      return;
    }
    element.classList.add('rsr-preview-diff-target');
    if (!element.matches(renderedDiffSelector) && !element.querySelector(renderedDiffSelector)) {
      element.classList.add(
        'rsr-preview-diff-block',
        pane === 'before' ? 'rsr-preview-diff-before' : 'rsr-preview-diff-after');
    }
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
      document.querySelectorAll('.rsr-preview-diff-target,[data-rsr-diff-navigation-index]'));
    if (markerElements.length === 0) {
      return;
    }
    const root = document.scrollingElement || document.documentElement || document.body;
    const documentHeight = Math.max(1, root.scrollHeight);
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
      const splitMarkerSegments = (elements) => {
        const items = elements
          .map((element) => ({ element, rect: element.getBoundingClientRect() }))
          .filter((item) => item.rect.width > 0 && item.rect.height > 0)
          .sort((left, right) => left.rect.top - right.rect.top);
        const segments = [];
        items.forEach((item) => {
          const previous = segments.at(-1);
          if (!previous || item.rect.top > previous.bottom + 2) {
            segments.push({
              elements: [item.element],
              rects: [item.rect],
              bottom: item.rect.bottom,
            });
            return;
          }
          previous.elements.push(item.element);
          previous.rects.push(item.rect);
          previous.bottom = Math.max(previous.bottom, item.rect.bottom);
        });
        return segments;
      };
      markerGroups.forEach((elements) => {
        splitMarkerSegments(elements).forEach((segment) => {
          const alignmentGapSelector =
            '.rsr-preview-diff-alignment-gap,' +
            '.rsr-preview-diff-alignment-gap-row,' +
            '.rsr-preview-diff-alignment-gap-section';
          const isAlignmentGapTarget = (element) =>
            element.matches(alignmentGapSelector)
              || element.closest(alignmentGapSelector) !== null;
          const substantiveTargets = segment.elements.filter(
            (element) => !isAlignmentGapTarget(element));
          const resolvedTargets = segment.elements.flatMap((element) => {
            const inlineTargets = [
              ...(element.matches(renderedDiffSelector) ? [element] : []),
              ...element.querySelectorAll(renderedDiffSelector),
            ];
            if (inlineTargets.length > 0) {
              return inlineTargets;
            }
            return isAlignmentGapTarget(element) ? [] : [element];
          });
          const markerTargets =
            resolvedTargets.length > 0 ? resolvedTargets : segment.elements;
          const rects = markerTargets
            .map((element) => element.getBoundingClientRect())
            .filter((rect) => rect.width > 0 && rect.height > 0);
          if (rects.length === 0) {
            return;
          }
          const hasSubstantiveChange = substantiveTargets.length > 0;
          const hasRemovedMarker = markerTargets.some(
            (element) => element.matches('.rsr-rendered-diff-removed'));
          const hasAddedMarker = markerTargets.some(
            (element) => element.matches('.rsr-rendered-diff-added'));
          const isRemoval = hasRemovedMarker !== hasAddedMarker
            ? hasRemovedMarker
            : hasSubstantiveChange ? pane === 'before' : pane === 'after';
          const marker = document.createElement('div');
          marker.className = `rsr-preview-diff-scrollbar-marker ${isRemoval ? 'rsr-preview-diff-scrollbar-marker-before' : 'rsr-preview-diff-scrollbar-marker-after'}`;
          const absoluteTop = Math.min(...rects.map((rect) => rect.top)) + window.scrollY;
          const absoluteBottom = Math.max(...rects.map((rect) => rect.bottom)) + window.scrollY;
          const top = Math.max(0, Math.min(1, absoluteTop / documentHeight));
          const height = Math.max(4, Math.min(window.innerHeight, ((absoluteBottom - absoluteTop) / documentHeight) * window.innerHeight));
          const markerTop = Math.max(0, Math.min(window.innerHeight - height, top * window.innerHeight));
          marker.style.top = `${markerTop.toFixed(1)}px`;
          marker.style.height = `${height.toFixed(1)}px`;
          rail.appendChild(marker);
        });
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
  if (existingOverlay?.__scrollTargets && existingOverlay.__positionHandler) {
    existingOverlay.__scrollTargets.forEach((scrollTarget) => {
      scrollTarget.removeEventListener('scroll', existingOverlay.__positionHandler);
    });
  }
  existingOverlay?.remove();
  window.__repoSyncRadarDiffNavigation = undefined;

  let targets = Array.from(
    document.querySelectorAll('[data-rsr-diff-navigation-index="{{navigationIndex}}"]'));
  if (targets.length === 0) {
    return { found: false };
  }
  const renderedDiffSelector =
    '.rsr-rendered-diff-added,.rsr-rendered-diff-removed';
  const resolveOverlayTargets = () => {
    const alignmentGapSelector =
      '.rsr-preview-diff-alignment-gap,' +
      '.rsr-preview-diff-alignment-gap-row,' +
      '.rsr-preview-diff-alignment-gap-section';
    const isAlignmentGapTarget = (target) =>
      target.matches(alignmentGapSelector)
        || target.closest(alignmentGapSelector) !== null;
    const resolvedTargets = targets.flatMap((target) => {
      const inlineTargets = [
        ...(target.matches(renderedDiffSelector) ? [target] : []),
        ...target.querySelectorAll(renderedDiffSelector),
      ];
      if (inlineTargets.length > 0) {
        return inlineTargets;
      }
      return isAlignmentGapTarget(target) ? [] : [target];
    });
    return resolvedTargets.length > 0 ? resolvedTargets : targets;
  };
  let overlayTargets = resolveOverlayTargets();
  const root = document.scrollingElement || document.documentElement || document.body;
  const maxScrollTop = Math.max(1, (root?.scrollHeight || 0) - window.innerHeight);
  const targetRects = overlayTargets
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
  let scrollTargets = [];
  const collectScrollTargets = () => {
    const candidates = new Set();
    overlayTargets.forEach((target) => {
      let ancestor = target.parentElement;
      while (ancestor && ancestor !== document.body) {
        if (window.getComputedStyle(ancestor).overflowX !== 'visible') {
          candidates.add(ancestor);
        }
        ancestor = ancestor.parentElement;
      }
    });
    return Array.from(candidates);
  };
  const getHorizontallyVisibleRect = (target, inlinePadding) => {
    const rect = target.getBoundingClientRect();
    let left = Math.max(0, rect.left - inlinePadding);
    let right = Math.min(document.documentElement.clientWidth, rect.right + inlinePadding);
    let ancestor = target.parentElement;
    while (ancestor && ancestor !== document.body) {
      if (window.getComputedStyle(ancestor).overflowX !== 'visible') {
        const scrollRect = ancestor.getBoundingClientRect();
        const scrollStyle = window.getComputedStyle(ancestor);
        const borderLeft = Number.parseFloat(scrollStyle.borderLeftWidth) || 0;
        const borderRight = Number.parseFloat(scrollStyle.borderRightWidth) || 0;
        left = Math.max(left, scrollRect.left + borderLeft);
        right = Math.min(right, scrollRect.right - borderRight);
      }
      ancestor = ancestor.parentElement;
    }
    return right > left
      ? { left, right, top: rect.top, bottom: rect.bottom }
      : null;
  };
  const positionOverlay = () => {
    const inlinePadding = 6;
    const rects = overlayTargets
      .map((target) => getHorizontallyVisibleRect(target, inlinePadding))
      .filter((rect) => rect && rect.right > rect.left && rect.bottom > rect.top);
    if (rects.length === 0) {
      overlay.hidden = true;
      return;
    }
    overlay.hidden = false;
    const left = Math.min(...rects.map((rect) => rect.left)) + window.scrollX;
    const top = Math.min(...rects.map((rect) => rect.top)) + window.scrollY;
    const right = Math.max(...rects.map((rect) => rect.right)) + window.scrollX;
    const bottom = Math.max(...rects.map((rect) => rect.bottom)) + window.scrollY;
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
    overlayTargets = resolveOverlayTargets();
    scrollTargets.forEach((scrollTarget) => {
      scrollTarget.removeEventListener('scroll', positionOverlay);
    });
    scrollTargets = collectScrollTargets();
    scrollTargets.forEach((scrollTarget) => {
      scrollTarget.addEventListener('scroll', positionOverlay, { passive: true });
    });
    overlay.__scrollTargets = scrollTargets;
    overlay.__resizeObserver?.disconnect();
    positionOverlay();
    if (typeof ResizeObserver === 'function') {
      const resizeObserver = new ResizeObserver(positionOverlay);
      overlayTargets.forEach((target) => resizeObserver.observe(target));
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
  const appliedScrollTop = window.scrollY || root?.scrollTop || 0;
  if (scrollSyncState) {
    scrollSyncState.lastScrollTop = appliedScrollTop;
  }
  return { found: true, scrollTop: appliedScrollTop };
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