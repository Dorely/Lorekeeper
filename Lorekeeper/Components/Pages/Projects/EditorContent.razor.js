// Debounced textarea bridge with line-number gutter for Blazor.
// attach(elements, dotNetRef, debounceMs, initialValue)
//   elements: { textarea, gutter }
// returns handle: { setValue, flush, setReadOnly, dispose }

const lastChapterKey = (projectId) => `Lorekeeper.editor.lastChapter:${projectId}`;
const columnLayoutKey = (projectId) => `Lorekeeper.editor.columnLayout:v1:${projectId}`;

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

export function clearLastEditorChapterId(projectId) {
    try {
        window.localStorage.removeItem(lastChapterKey(projectId));
    } catch (_) {
        // Storage can be unavailable in private or restricted browsing modes.
    }
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

export function attach(elements, dotNetRef, debounceMs, initialValue) {
    const el = elements.textarea ?? elements; // back-compat
    const gutter = elements.gutter ?? null;

    if (initialValue !== undefined && initialValue !== null) {
        el.value = initialValue;
    }

    let timer = null;
    let lastQueued = el.value;
    let saveChain = Promise.resolve(true);
    let mirror = null;
    let resizeObserver = null;
    let resizeListener = null;
    let renderFrame = null;

    const parsePixels = (value) => {
        const parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : 0;
    };

    const resolveLineHeight = (computed) => {
        const lineHeight = parsePixels(computed.lineHeight);
        if (lineHeight > 0) return lineHeight;

        const fontSize = parsePixels(computed.fontSize);
        return fontSize > 0 ? fontSize * 1.2 : 16;
    };

    const splitLogicalLines = (text) => {
        const normalized = (text ?? "").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
        return normalized.length === 0 ? [""] : normalized.split("\n");
    };

    const ensureMirror = () => {
        if (mirror) return mirror;

        mirror = document.createElement("div");
        mirror.setAttribute("aria-hidden", "true");
        mirror.style.position = "fixed";
        mirror.style.left = "-10000px";
        mirror.style.top = "0";
        mirror.style.visibility = "hidden";
        mirror.style.pointerEvents = "none";
        mirror.style.overflow = "hidden";
        mirror.style.padding = "0";
        mirror.style.border = "0";
        mirror.style.margin = "0";
        mirror.style.whiteSpace = "pre-wrap";
        mirror.style.overflowWrap = "break-word";
        document.body.appendChild(mirror);
        return mirror;
    };

    const syncMirrorStyles = () => {
        const computed = getComputedStyle(el);
        const contentWidth = Math.max(
            1,
            el.clientWidth - parsePixels(computed.paddingLeft) - parsePixels(computed.paddingRight));

        const mirrorElement = ensureMirror();
        mirrorElement.style.boxSizing = "content-box";
        mirrorElement.style.width = `${contentWidth}px`;
        mirrorElement.style.fontFamily = computed.fontFamily;
        mirrorElement.style.fontSize = computed.fontSize;
        mirrorElement.style.fontStyle = computed.fontStyle;
        mirrorElement.style.fontVariant = computed.fontVariant;
        mirrorElement.style.fontWeight = computed.fontWeight;
        mirrorElement.style.letterSpacing = computed.letterSpacing;
        mirrorElement.style.lineHeight = computed.lineHeight;
        mirrorElement.style.textIndent = computed.textIndent;
        mirrorElement.style.textTransform = computed.textTransform;
        mirrorElement.style.tabSize = computed.tabSize;
        mirrorElement.style.wordBreak = computed.wordBreak;
        mirrorElement.style.overflowWrap = "break-word";

        return { computed, lineHeight: resolveLineHeight(computed) };
    };

    const measureLineHeights = (lines) => {
        const { computed, lineHeight } = syncMirrorStyles();
        const fragment = document.createDocumentFragment();
        const blocks = [];

        mirror.replaceChildren();

        for (const line of lines) {
            const block = document.createElement("div");
            block.style.display = "block";
            block.style.minHeight = `${lineHeight}px`;
            block.style.lineHeight = computed.lineHeight;
            block.style.margin = "0";
            block.style.padding = "0";
            block.style.border = "0";
            block.style.whiteSpace = "pre-wrap";
            block.style.overflowWrap = "break-word";
            block.style.wordBreak = computed.wordBreak;
            block.textContent = line.length === 0 ? "\u00a0" : line;
            blocks.push(block);
            fragment.appendChild(block);
        }

        mirror.appendChild(fragment);
        return blocks.map(block => Math.max(lineHeight, block.getBoundingClientRect().height));
    };

    const renderGutter = () => {
        if (!gutter) return;
        const lines = splitLogicalLines(el.value);
        const lineHeights = measureLineHeights(lines);
        const width = Math.max(4, String(lines.length).length);
        const fragment = document.createDocumentFragment();

        for (let i = 0; i < lines.length; i++) {
            const row = document.createElement("div");
            row.className = "line-gutter-row";
            row.textContent = String(i + 1).padStart(width, "0");
            row.style.boxSizing = "border-box";
            row.style.display = "block";
            row.style.height = `${lineHeights[i]}px`;
            row.style.minHeight = `${lineHeights[i]}px`;
            row.style.lineHeight = "inherit";
            row.style.whiteSpace = "nowrap";
            fragment.appendChild(row);
        }

        gutter.replaceChildren(fragment);
        syncScroll();
    };

    const syncScroll = () => {
        if (gutter) gutter.scrollTop = el.scrollTop;
    };

    const scheduleRenderGutter = () => {
        if (!gutter) return;
        if (renderFrame !== null) cancelAnimationFrame(renderFrame);
        renderFrame = requestAnimationFrame(() => {
            renderFrame = null;
            renderGutter();
        });
    };

    const fireNow = () => {
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
        const v = el.value;
        if (v === lastQueued) return saveChain;

        lastQueued = v;
        saveChain = saveChain
            .catch(() => false)
            .then(async () => {
                try {
                    const saved = await dotNetRef.invokeMethodAsync("OnBodyDebounced", v);
                    if (saved === false && lastQueued === v) lastQueued = null;
                    return saved !== false;
                } catch (_) {
                    if (lastQueued === v) lastQueued = null;
                    return false;
                }
            });
        return saveChain;
    };

    const onInput = () => {
        renderGutter();
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { void fireNow(); }, debounceMs);
    };

    const onBlur = () => fireNow();

    el.addEventListener("input", onInput);
    el.addEventListener("blur", onBlur);
    el.addEventListener("scroll", syncScroll);

    if (gutter) {
        if (typeof ResizeObserver !== "undefined") {
            resizeObserver = new ResizeObserver(scheduleRenderGutter);
            resizeObserver.observe(el);
            if (el.parentElement) resizeObserver.observe(el.parentElement);
        } else {
            resizeListener = scheduleRenderGutter;
            window.addEventListener("resize", resizeListener);
        }
    }

    renderGutter();

    return {
        setValue(v) {
            // Programmatic switch — update without firing.
            if (timer) { clearTimeout(timer); timer = null; }
            el.value = v ?? "";
            lastQueued = el.value;
            saveChain = saveChain.then(() => true, () => true);
            renderGutter();
        },
        flush() {
            return fireNow();
        },
        setReadOnly(readOnly) {
            el.readOnly = !!readOnly;
        },
        dispose() {
            if (timer) { clearTimeout(timer); timer = null; }
            if (renderFrame !== null) { cancelAnimationFrame(renderFrame); renderFrame = null; }
            if (resizeObserver) { resizeObserver.disconnect(); resizeObserver = null; }
            if (resizeListener) { window.removeEventListener("resize", resizeListener); resizeListener = null; }
            if (mirror) { mirror.remove(); mirror = null; }
            el.removeEventListener("input", onInput);
            el.removeEventListener("blur", onBlur);
            el.removeEventListener("scroll", syncScroll);
        }
    };
}
