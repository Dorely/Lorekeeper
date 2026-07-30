// Serialized debounced textarea bridge with a wrapping-aware line-number gutter.
export function attach(elements, dotNetRef, debounceMs, initialValue) {
    const el = elements.textarea ?? elements;
    const gutter = elements.gutter ?? null;
    el.value = initialValue ?? "";
    let timer = null;
    let lastQueued = el.value;
    let saveChain = Promise.resolve(true);
    let mirror = null;
    let resizeObserver = null;
    let resizeListener = null;
    let renderFrame = null;

    const parsePixels = value => {
        const parsed = Number.parseFloat(value);
        return Number.isFinite(parsed) ? parsed : 0;
    };
    const resolveLineHeight = computed => {
        const lineHeight = parsePixels(computed.lineHeight);
        if (lineHeight > 0) return lineHeight;
        const fontSize = parsePixels(computed.fontSize);
        return fontSize > 0 ? fontSize * 1.2 : 16;
    };
    const splitLogicalLines = text => {
        const normalized = (text ?? "").replace(/\r\n/g, "\n").replace(/\r/g, "\n");
        return normalized.length === 0 ? [""] : normalized.split("\n");
    };
    const ensureMirror = () => {
        if (mirror) return mirror;
        mirror = document.createElement("div");
        mirror.setAttribute("aria-hidden", "true");
        Object.assign(mirror.style, {
            position: "fixed",
            left: "-10000px",
            top: "0",
            visibility: "hidden",
            pointerEvents: "none",
            overflow: "hidden",
            padding: "0",
            border: "0",
            margin: "0",
            whiteSpace: "pre-wrap",
            overflowWrap: "break-word"
        });
        document.body.appendChild(mirror);
        return mirror;
    };
    const syncMirrorStyles = () => {
        const computed = getComputedStyle(el);
        const contentWidth = Math.max(
            1,
            el.clientWidth - parsePixels(computed.paddingLeft) - parsePixels(computed.paddingRight));
        const mirrorElement = ensureMirror();
        Object.assign(mirrorElement.style, {
            boxSizing: "content-box",
            width: `${contentWidth}px`,
            fontFamily: computed.fontFamily,
            fontSize: computed.fontSize,
            fontStyle: computed.fontStyle,
            fontVariant: computed.fontVariant,
            fontWeight: computed.fontWeight,
            letterSpacing: computed.letterSpacing,
            lineHeight: computed.lineHeight,
            textIndent: computed.textIndent,
            textTransform: computed.textTransform,
            tabSize: computed.tabSize,
            wordBreak: computed.wordBreak,
            overflowWrap: "break-word"
        });
        return {computed, lineHeight: resolveLineHeight(computed)};
    };
    const measureLineHeights = lines => {
        const {computed, lineHeight} = syncMirrorStyles();
        const fragment = document.createDocumentFragment();
        const blocks = [];
        mirror.replaceChildren();
        for (const line of lines) {
            const block = document.createElement("div");
            Object.assign(block.style, {
                display: "block",
                minHeight: `${lineHeight}px`,
                lineHeight: computed.lineHeight,
                margin: "0",
                padding: "0",
                border: "0",
                whiteSpace: "pre-wrap",
                overflowWrap: "break-word",
                wordBreak: computed.wordBreak
            });
            block.textContent = line.length === 0 ? "\u00a0" : line;
            blocks.push(block);
            fragment.appendChild(block);
        }
        mirror.appendChild(fragment);
        return blocks.map(block => Math.max(lineHeight, block.getBoundingClientRect().height));
    };
    const syncScroll = () => {
        if (gutter) gutter.scrollTop = el.scrollTop;
    };
    const renderGutter = () => {
        if (!gutter) return;
        const lines = splitLogicalLines(el.value);
        const lineHeights = measureLineHeights(lines);
        const width = Math.max(4, String(lines.length).length);
        const fragment = document.createDocumentFragment();
        for (let index = 0; index < lines.length; index++) {
            const row = document.createElement("div");
            row.className = "line-gutter-row";
            row.textContent = String(index + 1).padStart(width, "0");
            Object.assign(row.style, {
                boxSizing: "border-box",
                display: "block",
                height: `${lineHeights[index]}px`,
                minHeight: `${lineHeights[index]}px`,
                lineHeight: "inherit",
                whiteSpace: "nowrap"
            });
            fragment.appendChild(row);
        }
        gutter.replaceChildren(fragment);
        syncScroll();
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
        const value = el.value;
        if (value === lastQueued) return saveChain;
        lastQueued = value;
        saveChain = saveChain.catch(() => false).then(async () => {
            try {
                const saved = await dotNetRef.invokeMethodAsync("OnBodyDebounced", value);
                if (saved === false && lastQueued === value) lastQueued = null;
                return saved !== false;
            } catch {
                if (lastQueued === value) lastQueued = null;
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
        setValue(value) {
            if (timer) {
                clearTimeout(timer);
                timer = null;
            }
            el.value = value ?? "";
            lastQueued = el.value;
            renderGutter();
        },
        flush: fireNow,
        waitForSaves() {
            return saveChain.catch(() => false);
        },
        setReadOnly(readOnly) {
            el.readOnly = !!readOnly;
        },
        dispose() {
            if (timer) clearTimeout(timer);
            if (renderFrame !== null) cancelAnimationFrame(renderFrame);
            if (resizeObserver) resizeObserver.disconnect();
            if (resizeListener) window.removeEventListener("resize", resizeListener);
            if (mirror) mirror.remove();
            el.removeEventListener("input", onInput);
            el.removeEventListener("blur", onBlur);
            el.removeEventListener("scroll", syncScroll);
        }
    };
}
