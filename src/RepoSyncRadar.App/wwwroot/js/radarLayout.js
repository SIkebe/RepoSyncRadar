(function () {
  const storageKey = "reposyncradar.sidebarWidth";
  const defaultWidth = 240;
  const minWidth = 180;
  const minWorkbenchWidth = 360;
  const step = 24;

  function readStoredWidth() {
    try {
      const value = Number(window.localStorage.getItem(storageKey));
      return Number.isFinite(value) && value > 0 ? value : null;
    } catch {
      return null;
    }
  }

  function storeWidth(width) {
    try {
      window.localStorage.setItem(storageKey, String(Math.round(width)));
    } catch {
      // localStorage can be unavailable in constrained WebView profiles.
    }
  }

  function maxSidebarWidth(shell) {
    const shellWidth = shell.getBoundingClientRect().width;
    if (!Number.isFinite(shellWidth) || shellWidth <= 0) {
      return 640;
    }
    return Math.max(minWidth, shellWidth - minWorkbenchWidth);
  }

  function clampWidth(shell, width) {
    return Math.min(Math.max(width, minWidth), maxSidebarWidth(shell));
  }

  function currentSidebarWidth(shell) {
    const cssValue = getComputedStyle(shell).getPropertyValue("--radar-sidebar-width");
    const parsedCssValue = Number.parseFloat(cssValue);
    if (Number.isFinite(parsedCssValue) && parsedCssValue > 0) {
      return parsedCssValue;
    }

    const sidebar = shell.querySelector(".radar-sidebar-pane");
    const measuredWidth = sidebar?.getBoundingClientRect().width;
    return Number.isFinite(measuredWidth) && measuredWidth > 0 ? measuredWidth : defaultWidth;
  }

  function applySidebarWidth(shell, width, persist) {
    const nextWidth = clampWidth(shell, width);
    shell.style.setProperty("--radar-sidebar-width", `${Math.round(nextWidth)}px`);
    if (persist) {
      storeWidth(nextWidth);
    }
  }

  window.repoSyncRadarLayout = window.repoSyncRadarLayout || {};
  window.repoSyncRadarLayout.initSidebarSplitter = function initSidebarSplitter(shellSelector) {
    const shell = document.querySelector(shellSelector);
    if (!shell || shell.dataset.sidebarSplitterReady === "true") {
      return;
    }

    const splitter = shell.querySelector("[data-testid='radar-sidebar-resizer']");
    if (!splitter) {
      return;
    }

    shell.dataset.sidebarSplitterReady = "true";
    applySidebarWidth(shell, readStoredWidth() ?? currentSidebarWidth(shell), false);

    let dragging = false;
    let startX = 0;
    let startWidth = defaultWidth;

    function stopDragging() {
      if (!dragging) {
        return;
      }

      dragging = false;
      document.body.classList.remove("radar-resizing-columns");
      window.removeEventListener("pointermove", onPointerMove, true);
      window.removeEventListener("pointerup", stopDragging, true);
      window.removeEventListener("pointercancel", stopDragging, true);
    }

    function onPointerMove(event) {
      if (!dragging) {
        return;
      }

      event.preventDefault();
      applySidebarWidth(shell, startWidth + event.clientX - startX, true);
    }

    splitter.addEventListener("pointerdown", (event) => {
      if (event.button !== 0) {
        return;
      }

      dragging = true;
      startX = event.clientX;
      startWidth = currentSidebarWidth(shell);
      document.body.classList.add("radar-resizing-columns");
      splitter.setPointerCapture?.(event.pointerId);
      window.addEventListener("pointermove", onPointerMove, true);
      window.addEventListener("pointerup", stopDragging, true);
      window.addEventListener("pointercancel", stopDragging, true);
      event.preventDefault();
    });

    splitter.addEventListener("keydown", (event) => {
      if (event.key === "ArrowLeft") {
        applySidebarWidth(shell, currentSidebarWidth(shell) - step, true);
        event.preventDefault();
      } else if (event.key === "ArrowRight") {
        applySidebarWidth(shell, currentSidebarWidth(shell) + step, true);
        event.preventDefault();
      } else if (event.key === "Home") {
        applySidebarWidth(shell, minWidth, true);
        event.preventDefault();
      } else if (event.key === "End") {
        applySidebarWidth(shell, maxSidebarWidth(shell), true);
        event.preventDefault();
      }
    });
  };
})();
