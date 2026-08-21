function lineFor(node) {
    return node instanceof Element
        ? node.closest(".chapter-preview-line")
        : node?.parentElement?.closest(".chapter-preview-line");
}

function offsetWithin(line, node, offset) {
    const range = document.createRange();
    range.selectNodeContents(line);
    range.setEnd(node, offset);
    return range.toString().length;
}

function endpoint(line, node, offset) {
    const lineStart = Number(line.dataset.sourceStartUtf16);
    const lineEnd = Number(line.dataset.sourceEndUtf16);
    const prefix = line.textContent?.startsWith("• ") ? 2 : 0;
    const displayed = Math.max(0, offsetWithin(line, node, offset) - prefix);
    return {
        blockId: line.dataset.blockId,
        offset: Math.min(lineEnd, lineStart + displayed)
    };
}

function lineProgress(root, line) {
    const lines = [...root.querySelectorAll(".chapter-preview-line")];
    const index = Math.max(0, lines.indexOf(line));
    return lines.length <= 1 ? 0 : index / (lines.length - 1);
}

function viewportLine(root) {
    const scroll = root.querySelector(".chapter-preview-scroll") || root;
    const bounds = scroll.getBoundingClientRect();
    const center = bounds.top + bounds.height / 2;
    const lines = [...root.querySelectorAll(".chapter-preview-line")];
    return lines
        .map(line => ({line, distance: Math.abs(line.getBoundingClientRect().top + line.getBoundingClientRect().height / 2 - center)}))
        .sort((left, right) => left.distance - right.distance)[0]?.line || null;
}

function viewportPage(root) {
    const scroll = root.querySelector(".chapter-preview-scroll") || root;
    const bounds = scroll.getBoundingClientRect();
    const center = bounds.top + bounds.height / 2;
    const pages = [...root.querySelectorAll(".chapter-preview-page")];
    return pages
        .map(page => ({page, distance: Math.abs(page.getBoundingClientRect().top + page.getBoundingClientRect().height / 2 - center)}))
        .sort((left, right) => left.distance - right.distance)[0]?.page || null;
}

function locationForLine(root, line, offset = null) {
    if (!line) return null;
    const lines = [...root.querySelectorAll(".chapter-preview-line")];
    const lineIndex = lines.indexOf(line);
    const start = Number(line.dataset.sourceStartUtf16);
    const end = Number(line.dataset.sourceEndUtf16);
    const hasRange = line.dataset.blockId && Number.isFinite(start) && Number.isFinite(end);
    const sourceOffset = hasRange
        ? Math.max(start, Math.min(end, offset ?? start))
        : 0;
    return {
        blockId: hasRange ? line.dataset.blockId : null,
        node: false,
        anchorOffset: sourceOffset,
        headOffset: sourceOffset,
        logicalProgress: Number.isFinite(Number(line.dataset.locationProgress))
            ? Number(line.dataset.locationProgress)
            : lineProgress(root, line),
        fallbackLine: Number(line.dataset.reviewLine) || (lineIndex < 0 ? 1 : lineIndex + 1),
        viewportAnchor: true
    };
}

export function captureLocation(root) {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
        const focusLine = lineFor(selection.focusNode, selection.focusOffset);
        if (focusLine && root.contains(focusLine)) {
            const focus = endpoint(focusLine, selection.focusNode, selection.focusOffset);
            if (focus.blockId) {
                return {
                    blockId: focus.blockId,
                    node: false,
                    anchorOffset: focus.offset,
                    headOffset: focus.offset,
                    logicalProgress: lineProgress(root, focusLine),
                    fallbackLine: [...root.querySelectorAll(".chapter-preview-line")].indexOf(focusLine) + 1,
                    viewportAnchor: true
                };
            }
        }
    }
    const line = viewportLine(root);
    if (line) return locationForLine(root, line);
    const page = viewportPage(root);
    if (!page) return null;
    const pages = [...root.querySelectorAll(".chapter-preview-page")];
    return {
        blockId: page.dataset.pageBlockId || null,
        node: !!page.dataset.pageBlockId,
        anchorOffset: 0,
        headOffset: 0,
        logicalProgress: pages.length <= 1 ? 0 : pages.indexOf(page) / (pages.length - 1),
        fallbackLine: 1,
        viewportAnchor: true
    };
}

function lineForLocation(root, location) {
    const lines = [...root.querySelectorAll(".chapter-preview-line")];
    if (location?.blockId) {
        const matching = lines.filter(line => line.dataset.blockId === location.blockId);
        const offset = Number(location.headOffset ?? location.anchorOffset ?? 0);
        const exact = matching.find(line => Number(line.dataset.sourceStartUtf16) <= offset
            && Number(line.dataset.sourceEndUtf16) >= offset);
        if (exact) return exact;
        if (matching.length > 0) return matching[0];
    }
    if (lines.length === 0) {
        if (location?.blockId) {
            const page = [...root.querySelectorAll(".chapter-preview-page")]
                .find(candidate => candidate.dataset.pageBlockId === location.blockId);
            if (page) return page;
        }
        const pages = [...root.querySelectorAll(".chapter-preview-page")];
        if (pages.length === 0) return null;
        const progress = Math.max(0, Math.min(1, Number(location?.logicalProgress) || 0));
        return pages[Math.round(progress * (pages.length - 1))];
    }
    if (Number.isFinite(Number(location?.logicalProgress))) {
        const target = Math.max(0, Math.min(1, Number(location.logicalProgress)));
        return lines[Math.round(target * (lines.length - 1))];
    }
    const lineNumber = Math.max(1, Number(location?.fallbackLine) || 1);
    return lines[Math.min(lines.length - 1, lineNumber - 1)];
}

export function restoreLocation(root, location) {
    const target = lineForLocation(root, location);
    if (!target) return false;
    target.scrollIntoView({block: "center", behavior: "auto"});
    return true;
}

export function readSelection(root) {
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0 || selection.isCollapsed)
        return {error: "Select flowing manuscript text in the preview first."};
    const domRange = selection.getRangeAt(0);
    const startLine = lineFor(domRange.startContainer);
    const endLine = lineFor(domRange.endContainer);
    if (!startLine || !endLine || !root.contains(startLine) || !root.contains(endLine))
        return {error: "Selections must begin and end in flowing manuscript text."};
    const selectedLines = [...root.querySelectorAll(".chapter-preview-line")]
        .filter(line => {
            try { return domRange.intersectsNode(line); } catch { return false; }
        });
    const crossesDesignedPage = [...root.querySelectorAll(
        '.chapter-preview-page[data-page-kind="DesignedPage"],.chapter-preview-page[data-page-kind="Designed"]')]
        .some(page => {
            try { return domRange.intersectsNode(page); } catch { return false; }
        });
    if (crossesDesignedPage)
        return {error: "Review annotations cannot cross Designed Page canvas text."};
    if (selectedLines.length === 0
        || selectedLines.some(line => !line.dataset.blockId
            || line.dataset.sourceStartUtf16 === undefined
            || line.dataset.sourceEndUtf16 === undefined))
        return {error: "This selection crosses non-flowing or generated page content. Select only manuscript text and Figure captions."};
    const start = endpoint(startLine, domRange.startContainer, domRange.startOffset);
    const end = endpoint(endLine, domRange.endContainer, domRange.endOffset);
    if (!start.blockId || !end.blockId || start.blockId === end.blockId && start.offset >= end.offset)
        return {error: "Select a non-empty manuscript text range."};
    return {
        range: {
            startBlockId: start.blockId,
            startOffset: start.offset,
            endBlockId: end.blockId,
            endOffset: end.offset
        }
    };
}

export function selectRange(root, range) {
    const startLines = [...root.querySelectorAll(`.chapter-preview-line[data-block-id="${CSS.escape(range.startBlockId)}"]`)];
    const endLines = [...root.querySelectorAll(`.chapter-preview-line[data-block-id="${CSS.escape(range.endBlockId)}"]`)];
    const first = startLines.find(line => Number(line.dataset.sourceStartUtf16) <= range.startOffset
        && Number(line.dataset.sourceEndUtf16) >= range.startOffset) || startLines[0];
    const last = endLines.find(line => Number(line.dataset.sourceStartUtf16) <= range.endOffset
        && Number(line.dataset.sourceEndUtf16) >= range.endOffset) || endLines.at(-1);
    if (!first || !last) return false;
    const point = (line, sourceOffset) => {
        const prefix = line.textContent?.startsWith("• ") ? 2 : 0;
        let remaining = prefix + Math.max(0, sourceOffset - Number(line.dataset.sourceStartUtf16));
        const walker = document.createTreeWalker(line, NodeFilter.SHOW_TEXT);
        let node;
        while ((node = walker.nextNode())) {
            if (remaining <= node.data.length) return {node, offset: remaining};
            remaining -= node.data.length;
        }
        return null;
    };
    const start = point(first, range.startOffset);
    const end = point(last, range.endOffset);
    if (start && end) {
        const domRange = document.createRange();
        domRange.setStart(start.node, start.offset);
        domRange.setEnd(end.node, end.offset);
        const selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(domRange);
    }
    first.scrollIntoView({block: "center", behavior: "smooth"});
    return true;
}
