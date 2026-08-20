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
