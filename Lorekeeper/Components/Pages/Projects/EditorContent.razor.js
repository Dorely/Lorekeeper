// Debounced textarea bridge with line-number gutter for Blazor.
// attach(elements, dotNetRef, debounceMs, initialValue)
//   elements: { textarea, gutter }
// returns handle: { setValue, flush, setReadOnly, dispose }

const lastChapterKey = (projectId) => `Lorekeeper.editor.lastChapter:${projectId}`;

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

export function attach(elements, dotNetRef, debounceMs, initialValue) {
    const el = elements.textarea ?? elements; // back-compat
    const gutter = elements.gutter ?? null;

    if (initialValue !== undefined && initialValue !== null) {
        el.value = initialValue;
    }

    let timer = null;
    let lastSent = el.value;
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
        if (v === lastSent) return;
        lastSent = v;
        try {
            dotNetRef.invokeMethodAsync("OnBodyDebounced", v);
        } catch (_) {
            // dotnet ref may have been disposed
        }
    };

    const onInput = () => {
        renderGutter();
        if (timer) clearTimeout(timer);
        timer = setTimeout(fireNow, debounceMs);
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
            lastSent = el.value;
            renderGutter();
        },
        flush() {
            fireNow();
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
