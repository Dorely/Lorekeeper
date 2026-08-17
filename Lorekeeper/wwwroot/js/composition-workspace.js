export function begin(stage, pointerId, objectId) {
    if (!stage) {
        throw new Error("The page canvas is not available.");
    }
    stage.setPointerCapture(pointerId);
    const rect = stage.getBoundingClientRect();
    return {
        left: rect.left,
        top: rect.top,
        width: rect.width,
        height: rect.height,
        imageAspectRatio: imageAspectRatio(stage, objectId),
    };
}

export function imageAspectRatio(stage, objectId) {
    if (!stage || !objectId) {
        return null;
    }
    const image = stage.querySelector(`[data-composition-object-id="${objectId}"] img`);
    if (!image || image.naturalWidth <= 0 || image.naturalHeight <= 0) {
        return null;
    }
    return image.naturalWidth / image.naturalHeight;
}

export function end(stage, pointerId) {
    if (stage?.hasPointerCapture(pointerId)) {
        stage.releasePointerCapture(pointerId);
    }
}

function textEditor(stage, objectId) {
    if (!stage) {
        return null;
    }
    return stage.querySelector(`[data-composition-text-id="${objectId}"]`);
}

function normalizeText(value) {
    return (value || "").replace(/\r\n?/g, "\n").replace(/\u00a0/g, " ");
}

function prepareTextEditor(editor) {
    if (editor.dataset.plainTextPaste === "true") {
        return;
    }
    editor.dataset.plainTextPaste = "true";
    editor.addEventListener("paste", event => {
        event.preventDefault();
        const text = event.clipboardData?.getData("text/plain") || "";
        const selection = window.getSelection();
        if (!selection || selection.rangeCount === 0) {
            return;
        }
        const range = selection.getRangeAt(0);
        range.deleteContents();
        const node = document.createTextNode(text);
        range.insertNode(node);
        range.setStartAfter(node);
        range.collapse(true);
        selection.removeAllRanges();
        selection.addRange(range);
    });
}

function boundaryOffset(editor, node, offset) {
    if (!node || !editor.contains(node) && node !== editor) {
        return 0;
    }
    const range = document.createRange();
    range.selectNodeContents(editor);
    range.setEnd(node, offset);
    return normalizeText(range.toString()).length;
}

export function focusTextEditor(stage, objectId) {
    const editor = textEditor(stage, objectId);
    if (!editor) {
        return;
    }
    prepareTextEditor(editor);
    editor.focus({ preventScroll: true });
    const selection = window.getSelection();
    if (!selection) {
        return;
    }
    const range = document.createRange();
    range.selectNodeContents(editor);
    range.collapse(false);
    selection.removeAllRanges();
    selection.addRange(range);
}

export function focusTextEditorAt(stage, objectId, clientX, clientY) {
    const editor = textEditor(stage, objectId);
    if (!editor) {
        return;
    }
    prepareTextEditor(editor);
    editor.focus({ preventScroll: true });
    const selection = window.getSelection();
    if (!selection) {
        return;
    }
    const position = document.caretPositionFromPoint?.(clientX, clientY);
    const legacyRange = position ? null : document.caretRangeFromPoint?.(clientX, clientY);
    const node = position?.offsetNode || legacyRange?.startContainer;
    const offset = position?.offset ?? legacyRange?.startOffset;
    if (!node || offset === undefined || !editor.contains(node)) {
        focusTextEditor(stage, objectId);
        return;
    }
    const range = document.createRange();
    range.setStart(node, offset);
    range.collapse(true);
    selection.removeAllRanges();
    selection.addRange(range);
}

export function readTextEditor(stage, objectId) {
    const editor = textEditor(stage, objectId);
    if (!editor) {
        return null;
    }
    const text = normalizeText(editor.innerText);
    const selection = window.getSelection();
    if (!selection || selection.rangeCount === 0
        || !editor.contains(selection.anchorNode)
        || !editor.contains(selection.focusNode)) {
        return { text, start: text.length, end: text.length };
    }
    const anchor = boundaryOffset(editor, selection.anchorNode, selection.anchorOffset);
    const focus = boundaryOffset(editor, selection.focusNode, selection.focusOffset);
    return {
        text,
        start: Math.min(anchor, focus),
        end: Math.max(anchor, focus),
    };
}

function textBoundary(editor, requestedOffset) {
    const walker = document.createTreeWalker(editor, NodeFilter.SHOW_TEXT);
    let remaining = Math.max(0, requestedOffset);
    let node = walker.nextNode();
    let last = editor;
    while (node) {
        last = node;
        if (remaining <= node.data.length) {
            return { node, offset: remaining };
        }
        remaining -= node.data.length;
        node = walker.nextNode();
    }
    return last === editor
        ? { node: editor, offset: editor.childNodes.length }
        : { node: last, offset: last.data.length };
}

export function restoreTextSelection(stage, objectId, start, end) {
    const editor = textEditor(stage, objectId);
    if (!editor) {
        return;
    }
    prepareTextEditor(editor);
    editor.focus({ preventScroll: true });
    const selection = window.getSelection();
    if (!selection) {
        return;
    }
    const from = textBoundary(editor, start);
    const to = textBoundary(editor, end);
    const range = document.createRange();
    range.setStart(from.node, from.offset);
    range.setEnd(to.node, to.offset);
    selection.removeAllRanges();
    selection.addRange(range);
}

const overflowObservers = new WeakMap();

function measureTextOverflow(stage) {
    if (!stage) {
        return [];
    }
    return [...stage.querySelectorAll(".composition-object--text[data-composition-object-id]")]
        .map(frame => {
            const content = frame.querySelector(".composition-rendered-text, .composition-text-editor");
            return {
                objectId: frame.dataset.compositionObjectId,
                overflows: !!content && (content.scrollHeight > content.clientHeight + 1
                    || content.scrollWidth > content.clientWidth + 1),
            };
        });
}

export function observeTextOverflow(stage, dotNetReference) {
    disconnectTextOverflow(stage);
    if (!stage || !dotNetReference) {
        return;
    }
    let scheduled = false;
    let lastSignature = null;
    const report = () => {
        scheduled = false;
        const measurements = measureTextOverflow(stage);
        const signature = JSON.stringify(measurements);
        if (signature === lastSignature) {
            return;
        }
        lastSignature = signature;
        dotNetReference.invokeMethodAsync("UpdateTextOverflowAsync", measurements);
    };
    const schedule = () => {
        if (!scheduled) {
            scheduled = true;
            requestAnimationFrame(report);
        }
    };
    const resizeObserver = new ResizeObserver(schedule);
    resizeObserver.observe(stage);
    const mutationObserver = new MutationObserver(() => {
        for (const frame of stage.querySelectorAll(".composition-object--text[data-composition-object-id]")) {
            resizeObserver.observe(frame);
        }
        schedule();
    });
    mutationObserver.observe(stage, { childList: true, subtree: true, characterData: true, attributes: true });
    for (const frame of stage.querySelectorAll(".composition-object--text[data-composition-object-id]")) {
        resizeObserver.observe(frame);
    }
    const fontsReady = () => schedule();
    document.fonts?.ready.then(fontsReady);
    document.fonts?.addEventListener("loadingdone", fontsReady);
    overflowObservers.set(stage, { resizeObserver, mutationObserver, fontsReady });
    schedule();
}

export function disconnectTextOverflow(stage) {
    const observer = stage ? overflowObservers.get(stage) : null;
    if (!observer) {
        return;
    }
    observer.resizeObserver.disconnect();
    observer.mutationObserver.disconnect();
    document.fonts?.removeEventListener("loadingdone", observer.fontsReady);
    overflowObservers.delete(stage);
}
