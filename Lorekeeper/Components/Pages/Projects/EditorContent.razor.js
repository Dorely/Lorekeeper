// Editor workspace browser state, column resizing, and review scrolling.

const lastChapterKey = (projectId) => `Lorekeeper.editor.lastChapter:${projectId}`;
const columnLayoutKey = (projectId) => `Lorekeeper.editor.columnLayout:v1:${projectId}`;
const workspacePreferencesKey = (projectId) => `Lorekeeper.editor.workspace:v1:${projectId}`;
const editorModes = new Set(["Edit", "Read", "Pages", "Review"]);

const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));

export function getLastEditorChapterId(projectId) {
    try {
        return window.localStorage.getItem(lastChapterKey(projectId));
    } catch (_) {
        return null;
    }
}
export function setLastEditorChapterId(projectId, chapterId) {
    try {
        window.localStorage.setItem(lastChapterKey(projectId), chapterId);
    } catch (_) {
        // Storage can be unavailable in private or restricted browsing modes.
    }
}

export function pushEditorLocation(href) {
    if (typeof href !== "string" || href.length === 0) return;

    const target = new URL(href, document.baseURI);
    if (target.origin !== window.location.origin) return;

    const location = `${target.pathname}${target.search}${target.hash}`;
    if (`${window.location.pathname}${window.location.search}${window.location.hash}` === location) return;
    window.history.pushState(window.history.state, "", location);
}

export function clearLastEditorChapterId(projectId) {
    try {
        window.localStorage.removeItem(lastChapterKey(projectId));
    } catch (_) {
        // Storage can be unavailable in private or restricted browsing modes.
    }
}

export function getEditorWorkspacePreferences(projectId) {
    return readWorkspacePreferences(projectId);
}

export function setEditorChapterMode(projectId, chapterId, mode) {
    if (!chapterId || !editorModes.has(mode)) return;
    const preferences = readWorkspacePreferences(projectId);
    preferences.chapterModes[chapterId] = mode;
    writeWorkspacePreferences(projectId, preferences);
}

export function setEditorReadFacing(projectId, facing) {
    const preferences = readWorkspacePreferences(projectId);
    preferences.readFacing = facing === true;
    writeWorkspacePreferences(projectId, preferences);
}

export function setEditorPaneCollapsed(projectId, pane, collapsed) {
    if (pane !== "chat" && pane !== "memory") return;
    const preferences = readWorkspacePreferences(projectId);
    preferences[`${pane}Collapsed`] = collapsed === true;
    writeWorkspacePreferences(projectId, preferences);
}

export function setEditorContentTarget(projectId, target) {
    const preferences = readWorkspacePreferences(projectId);
    preferences.contentTarget = typeof target === "string" ? target : "Core";
    writeWorkspacePreferences(projectId, preferences);
}

function reviewLines(container) {
    return container
        ? [...container.querySelectorAll("[data-review-line]")]
        : [];
}

export function captureReviewLocation(container) {
    const lines = reviewLines(container);
    if (lines.length === 0) return null;
    const bounds = container.getBoundingClientRect();
    const center = bounds.top + bounds.height / 2;
    const target = lines
        .map(line => ({
            line,
            distance: Math.abs(line.getBoundingClientRect().top + line.getBoundingClientRect().height / 2 - center)
        }))
        .sort((left, right) => left.distance - right.distance)[0].line;
    const start = Number(target.dataset.sourceStartUtf16);
    const end = Number(target.dataset.sourceEndUtf16);
    const hasExactAnchor = target.dataset.manuscriptBlockId
        && Number.isFinite(start)
        && Number.isFinite(end);
    return {
        blockId: hasExactAnchor ? target.dataset.manuscriptBlockId : null,
        node: false,
        anchorOffset: hasExactAnchor ? start : 0,
        headOffset: hasExactAnchor ? start : 0,
        logicalProgress: Number.isFinite(Number(target.dataset.locationProgress))
            ? Number(target.dataset.locationProgress)
            : lines.indexOf(target) / Math.max(1, lines.length - 1),
        fallbackLine: Number(target.dataset.reviewLine) || lines.indexOf(target) + 1,
        viewportAnchor: true
    };
}

export function restoreReviewLocation(container, location) {
    const lines = reviewLines(container);
    if (lines.length === 0) return false;
    let target = null;
    if (location?.blockId) {
        const offset = Number(location.headOffset ?? location.anchorOffset ?? 0);
        const matching = lines.filter(line => line.dataset.manuscriptBlockId === location.blockId);
        target = matching.find(line => Number(line.dataset.sourceStartUtf16) <= offset
            && Number(line.dataset.sourceEndUtf16) >= offset) || matching[0];
    }
    if (!target && Number.isFinite(Number(location?.logicalProgress))) {
        const progress = Math.max(0, Math.min(1, Number(location.logicalProgress)));
        target = lines[Math.round(progress * (lines.length - 1))];
    }
    if (!target) {
        const lineNumber = Math.max(1, Number(location?.fallbackLine) || 1);
        target = lines[Math.min(lines.length - 1, lineNumber - 1)];
    }
    target.scrollIntoView({ block: "center", inline: "nearest", behavior: "auto" });
    return true;
}

export function attachColumnLayout(elements, projectId) {
    const grid = elements?.grid;
    const chatSplitter = elements?.chatSplitter;
    const memorySplitter = elements?.memorySplitter;
    if (!grid || !chatSplitter || !memorySplitter) {
        return { dispose() { } };
    }

    const rootFontSize = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
    const minimums = {
        chat: 11 * rootFontSize,
        memory: 12 * rootFontSize
    };
    const cssProperties = {
        chat: "--editor-chat-column",
        memory: "--editor-memory-column"
    };
    const splitters = {
        chat: chatSplitter,
        memory: memorySplitter
    };
    const collapsedClasses = [
        "editor-grid--chat-collapsed",
        "editor-grid--right-collapsed",
        "editor-grid--contest-review"
    ];
    const ratios = readStoredRatios(projectId);
    let maximums = { ...minimums };
    let resizeFrame = null;
    let resizeObserver = null;
    let resizeListener = null;
    let drag = null;
    let disposed = false;

    const measureDefaultMaximums = () => {
        const savedProperties = Object.fromEntries(
            Object.entries(cssProperties).map(([pane, property]) => [pane, grid.style.getPropertyValue(property)]));
        const savedClasses = Object.fromEntries(
            collapsedClasses.map(className => [className, grid.classList.contains(className)]));

        for (const property of Object.values(cssProperties)) {
            grid.style.removeProperty(property);
        }
        for (const className of collapsedClasses) {
            grid.classList.remove(className);
        }

        const computedTracks = getComputedStyle(grid).gridTemplateColumns;
        const trackPixels = [...computedTracks.matchAll(/(-?\d+(?:\.\d+)?)px/g)]
            .map(match => Number.parseFloat(match[1]));

        for (const [className, wasPresent] of Object.entries(savedClasses)) {
            if (wasPresent) grid.classList.add(className);
        }
        for (const [pane, property] of Object.entries(cssProperties)) {
            const savedValue = savedProperties[pane];
            if (savedValue) grid.style.setProperty(property, savedValue);
        }

        if (trackPixels.length >= 5) {
            return {
                chat: Math.max(minimums.chat, trackPixels[0]),
                memory: Math.max(minimums.memory, trackPixels[4])
            };
        }

        const usableWidth = Math.max(0, grid.clientWidth - rootFontSize);
        const fractionTotal = 1.05 + 2 + 1.1;
        return {
            chat: Math.max(17 * rootFontSize, usableWidth * 1.05 / fractionTotal),
            memory: Math.max(18 * rootFontSize, usableWidth * 1.1 / fractionTotal)
        };
    };

    const widthFromRatio = (pane) => {
        const minimum = minimums[pane];
        return minimum + (maximums[pane] - minimum) * ratios[pane];
    };

    const ratioFromWidth = (pane, width) => {
        const range = maximums[pane] - minimums[pane];
        return range <= 0 ? 1 : clamp((width - minimums[pane]) / range, 0, 1);
    };

    const updateAccessibility = (pane, width) => {
        const splitter = splitters[pane];
        const percent = Math.round(width / maximums[pane] * 100);
        splitter.setAttribute("aria-valuemin", String(Math.round(minimums[pane])));
        splitter.setAttribute("aria-valuemax", String(Math.round(maximums[pane])));
        splitter.setAttribute("aria-valuenow", String(Math.round(width)));
        splitter.setAttribute("aria-valuetext", `${Math.round(width)} pixels, ${percent}% of the default width`);
    };

    const applyPane = (pane) => {
        const width = clamp(widthFromRatio(pane), minimums[pane], maximums[pane]);
        grid.style.setProperty(cssProperties[pane], `${width}px`);
        updateAccessibility(pane, width);
    };

    const applyLayout = () => {
        applyPane("chat");
        applyPane("memory");
    };

    const refreshMaximums = () => {
        if (disposed || drag) return;
        maximums = measureDefaultMaximums();
        applyLayout();
    };

    const scheduleMaximumRefresh = () => {
        if (disposed || drag) return;
        if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
        resizeFrame = requestAnimationFrame(() => {
            resizeFrame = null;
            refreshMaximums();
        });
    };

    const persist = () => {
        try {
            if (ratios.chat === 1 && ratios.memory === 1) {
                window.localStorage.removeItem(columnLayoutKey(projectId));
                return;
            }

            window.localStorage.setItem(columnLayoutKey(projectId), JSON.stringify({
                chat: Number(ratios.chat.toFixed(4)),
                memory: Number(ratios.memory.toFixed(4))
            }));
        } catch (_) {
            // Storage can be unavailable in private or restricted browsing modes.
        }
    };

    const setPaneWidth = (pane, width, shouldPersist) => {
        ratios[pane] = ratioFromWidth(pane, clamp(width, minimums[pane], maximums[pane]));
        applyPane(pane);
        if (shouldPersist) persist();
    };

    const isDisabled = (splitter) =>
        splitter.getAttribute("aria-disabled") === "true"
        || splitter.classList.contains("editor-column-splitter--disabled");

    const restoreDocumentDragStyles = (activeDrag) => {
        document.body.style.cursor = activeDrag.bodyCursor;
        document.body.style.userSelect = activeDrag.bodyUserSelect;
    };

    const finishDrag = (shouldPersist, shouldRevert) => {
        if (!drag) return;
        const activeDrag = drag;
        drag = null;

        if (shouldRevert) {
            ratios[activeDrag.pane] = activeDrag.startRatio;
            applyPane(activeDrag.pane);
        } else if (shouldPersist) {
            persist();
        }

        activeDrag.splitter.classList.remove("editor-column-splitter--active");
        try {
            if (activeDrag.splitter.hasPointerCapture(activeDrag.pointerId)) {
                activeDrag.splitter.releasePointerCapture(activeDrag.pointerId);
            }
        } catch (_) {
            // Pointer capture may already have been released by the browser.
        }
        restoreDocumentDragStyles(activeDrag);
        scheduleMaximumRefresh();
    };

    const handlers = Object.entries(splitters).map(([pane, splitter]) => {
        const onPointerDown = (event) => {
            if (event.button !== 0 || isDisabled(splitter)) return;
            if (drag) finishDrag(true, false);

            drag = {
                pane,
                splitter,
                pointerId: event.pointerId,
                startX: event.clientX,
                startWidth: widthFromRatio(pane),
                startRatio: ratios[pane],
                bodyCursor: document.body.style.cursor,
                bodyUserSelect: document.body.style.userSelect
            };
            splitter.classList.add("editor-column-splitter--active");
            splitter.setPointerCapture?.(event.pointerId);
            document.body.style.cursor = "col-resize";
            document.body.style.userSelect = "none";
            event.preventDefault();
        };

        const onPointerMove = (event) => {
            if (!drag || drag.splitter !== splitter || drag.pointerId !== event.pointerId) return;
            const delta = pane === "chat"
                ? event.clientX - drag.startX
                : drag.startX - event.clientX;
            setPaneWidth(pane, drag.startWidth + delta, false);
            event.preventDefault();
        };

        const onPointerUp = (event) => {
            if (!drag || drag.splitter !== splitter || drag.pointerId !== event.pointerId) return;
            finishDrag(true, false);
        };

        const onPointerCancel = (event) => {
            if (!drag || drag.splitter !== splitter || drag.pointerId !== event.pointerId) return;
            finishDrag(false, true);
        };

        const onLostPointerCapture = (event) => {
            if (!drag || drag.splitter !== splitter || drag.pointerId !== event.pointerId) return;
            finishDrag(true, false);
        };

        const onKeyDown = (event) => {
            if (isDisabled(splitter)) return;
            const step = event.shiftKey ? 48 : 16;
            let width = widthFromRatio(pane);

            if (event.key === "Home") {
                width = minimums[pane];
            } else if (event.key === "End") {
                width = maximums[pane];
            } else if (event.key === "ArrowLeft") {
                width += pane === "chat" ? -step : step;
            } else if (event.key === "ArrowRight") {
                width += pane === "chat" ? step : -step;
            } else {
                return;
            }

            setPaneWidth(pane, width, true);
            event.preventDefault();
        };

        const onDoubleClick = (event) => {
            if (isDisabled(splitter)) return;
            ratios[pane] = 1;
            applyPane(pane);
            persist();
            event.preventDefault();
        };

        splitter.addEventListener("pointerdown", onPointerDown);
        splitter.addEventListener("pointermove", onPointerMove);
        splitter.addEventListener("pointerup", onPointerUp);
        splitter.addEventListener("pointercancel", onPointerCancel);
        splitter.addEventListener("lostpointercapture", onLostPointerCapture);
        splitter.addEventListener("keydown", onKeyDown);
        splitter.addEventListener("dblclick", onDoubleClick);

        return {
            splitter,
            onPointerDown,
            onPointerMove,
            onPointerUp,
            onPointerCancel,
            onLostPointerCapture,
            onKeyDown,
            onDoubleClick
        };
    });

    maximums = measureDefaultMaximums();
    applyLayout();

    if (typeof ResizeObserver !== "undefined") {
        resizeObserver = new ResizeObserver(scheduleMaximumRefresh);
        resizeObserver.observe(grid);
    } else {
        resizeListener = scheduleMaximumRefresh;
        window.addEventListener("resize", resizeListener);
    }

    return {
        dispose() {
            if (disposed) return;
            disposed = true;
            if (drag) finishDrag(false, true);
            if (resizeFrame !== null) cancelAnimationFrame(resizeFrame);
            if (resizeObserver) resizeObserver.disconnect();
            if (resizeListener) window.removeEventListener("resize", resizeListener);

            for (const handler of handlers) {
                handler.splitter.removeEventListener("pointerdown", handler.onPointerDown);
                handler.splitter.removeEventListener("pointermove", handler.onPointerMove);
                handler.splitter.removeEventListener("pointerup", handler.onPointerUp);
                handler.splitter.removeEventListener("pointercancel", handler.onPointerCancel);
                handler.splitter.removeEventListener("lostpointercapture", handler.onLostPointerCapture);
                handler.splitter.removeEventListener("keydown", handler.onKeyDown);
                handler.splitter.removeEventListener("dblclick", handler.onDoubleClick);
                handler.splitter.classList.remove("editor-column-splitter--active");
                handler.splitter.removeAttribute("aria-valuemin");
                handler.splitter.removeAttribute("aria-valuemax");
                handler.splitter.removeAttribute("aria-valuenow");
                handler.splitter.removeAttribute("aria-valuetext");
            }

            for (const property of Object.values(cssProperties)) {
                grid.style.removeProperty(property);
            }
        }
    };
}

function readStoredRatios(projectId) {
    try {
        const raw = window.localStorage.getItem(columnLayoutKey(projectId));
        const saved = raw ? JSON.parse(raw) : null;
        return {
            chat: Number.isFinite(saved?.chat) ? clamp(saved.chat, 0, 1) : 1,
            memory: Number.isFinite(saved?.memory) ? clamp(saved.memory, 0, 1) : 1
        };
    } catch (_) {
        return { chat: 1, memory: 1 };
    }
}

function readWorkspacePreferences(projectId) {
    const defaults = {
        chapterModes: {},
        readFacing: false,
        chatCollapsed: false,
        memoryCollapsed: false,
        contentTarget: "Core"
    };

    try {
        const raw = window.localStorage.getItem(workspacePreferencesKey(projectId));
        const saved = raw ? JSON.parse(raw) : null;
        if (!saved || typeof saved !== "object" || Array.isArray(saved)) return defaults;

        const chapterModes = {};
        if (saved.chapterModes && typeof saved.chapterModes === "object" && !Array.isArray(saved.chapterModes)) {
            for (const [chapterId, mode] of Object.entries(saved.chapterModes)) {
                if (typeof chapterId === "string" && editorModes.has(mode)) {
                    chapterModes[chapterId] = mode;
                }
            }
        }

        return {
            chapterModes,
            readFacing: saved.readFacing === true,
            chatCollapsed: saved.chatCollapsed === true,
            memoryCollapsed: saved.memoryCollapsed === true,
            contentTarget: typeof saved.contentTarget === "string" ? saved.contentTarget : "Core"
        };
    } catch (_) {
        return defaults;
    }
}

function writeWorkspacePreferences(projectId, preferences) {
    try {
        window.localStorage.setItem(workspacePreferencesKey(projectId), JSON.stringify(preferences));
    } catch (_) {
        // Storage can be unavailable in private or restricted browsing modes.
    }
}

export function scrollReviewBlock(container, blockId, anchorLine) {
    if (!container || !blockId) return;

    const escapeCss = (value) => {
        if (window.CSS && typeof window.CSS.escape === "function") {
            return window.CSS.escape(value);
        }

        return String(value).replace(/["\\]/g, "\\$&");
    };

    const target =
        container.querySelector(`[data-review-block="${escapeCss(blockId)}"]`) ??
        container.querySelector(`[data-review-line="${anchorLine}"]`);
    if (!target) return;

    target.scrollIntoView({ block: "center", inline: "nearest", behavior: "smooth" });
}
