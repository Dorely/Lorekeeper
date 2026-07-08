const stateByRoot = new WeakMap();

export function attach(root, dotNetRef) {
    if (!root) return;
    detach(root);

    const state = {
        dotNetRef,
        active: null,
        onPaste: null,
        onDrop: null,
        onDragOver: null,
        onPointerDown: null,
        onPointerMove: null,
        onPointerUp: null,
        onPointerCancel: null,
        onFocusOut: null,
    };

    state.onPaste = async (event) => {
        const file = firstImageFile(event.clipboardData?.files);
        if (!file) return;
        event.preventDefault();
        await sendImageFile(dotNetRef, file, null, null);
    };

    state.onDragOver = (event) => {
        if (!firstImageFile(event.dataTransfer?.files)) return;
        event.preventDefault();
    };

    state.onDrop = async (event) => {
        const file = firstImageFile(event.dataTransfer?.files);
        if (!file) return;
        event.preventDefault();

        const point = pagePoint(root, event.clientX, event.clientY);
        await sendImageFile(dotNetRef, file, point?.x ?? null, point?.y ?? null);
    };

    state.onPointerDown = (event) => {
        if (event.button !== 0) return;
        const target = event.target instanceof Element ? event.target : null;
        if (!target) return;

        const illustrationMove = target.closest("[data-illustration-move]");
        if (illustrationMove) {
            const block = illustrationMove.closest("[data-illustration-id]");
            const id = block?.dataset.illustrationId;
            if (!block || !id || !root.contains(block)) return;

            state.active = {
                type: "illustration",
                pointerId: event.pointerId,
                element: block,
                id,
                currentTarget: null,
            };

            block.classList.add("illustration-block--dragging");
            root.classList.add("illustration-drag-active");
            setIllustrationDropTarget(root, state.active, event.clientX, event.clientY);
            window.addEventListener("pointermove", state.onPointerMove, true);
            window.addEventListener("pointerup", state.onPointerUp, true);
            window.addEventListener("pointercancel", state.onPointerCancel, true);
            event.preventDefault();
            return;
        }

        if (target.closest("[data-picture-remove]")) return;

        const element = target.closest("[data-picture-element-id]");
        if (!element || !root.contains(element)) return;

        const isResize = !!target.closest("[data-picture-resize]");
        const isMoveHandle = !!target.closest("[data-picture-move]");
        const kind = element.dataset.pictureElementKind;
        const id = element.dataset.pictureElementId;
        if (!kind || !id) return;
        if (!isResize && kind === "text" && !isMoveHandle) return;
        if (!isResize && isFormControl(target) && !isMoveHandle) return;

        const page = element.closest(".picture-page");
        if (!page) return;

        const pageRect = page.getBoundingClientRect();
        const elementRect = element.getBoundingClientRect();
        const start = elementPercents(pageRect, elementRect);
        state.active = {
            type: "picture",
            pointerId: event.pointerId,
            element,
            page,
            kind,
            id,
            mode: isResize ? "resize" : "move",
            offsetXPercent: ((event.clientX - elementRect.left) / pageRect.width) * 100,
            offsetYPercent: ((event.clientY - elementRect.top) / pageRect.height) * 100,
            startX: start.x,
            startY: start.y,
            startWidth: start.width,
            startHeight: start.height,
        };

        element.classList.add("picture-element--active");
        window.addEventListener("pointermove", state.onPointerMove, true);
        window.addEventListener("pointerup", state.onPointerUp, true);
        window.addEventListener("pointercancel", state.onPointerCancel, true);
        event.preventDefault();
    };

    state.onPointerMove = (event) => {
        const active = state.active;
        if (!active || active.pointerId !== event.pointerId) return;
        event.preventDefault();

        if (active.type === "illustration") {
            setIllustrationDropTarget(root, active, event.clientX, event.clientY);
            return;
        }

        applyPointerPreview(active, event.clientX, event.clientY);
    };

    state.onPointerUp = async (event) => {
        const active = state.active;
        if (!active || active.pointerId !== event.pointerId) return;
        event.preventDefault();
        state.active = null;
        removeActivePointerListeners(state);

        if (active.type === "illustration") {
            const target = setIllustrationDropTarget(root, active, event.clientX, event.clientY);
            cleanupIllustrationDrag(root, active);
            if (!target) return;

            const paragraphIndex = Number.parseInt(target.dataset.illustrationParagraphIndex ?? "0", 10);
            const anchorPosition = target.dataset.illustrationAnchorPosition ?? "";
            if (Number.isNaN(paragraphIndex) || !anchorPosition) return;

            await dotNetRef.invokeMethodAsync(
                "MoveIllustrationBlockAsync",
                active.id,
                paragraphIndex,
                anchorPosition);
            return;
        }

        active.element.classList.remove("picture-element--active");
        const result = applyPointerPreview(active, event.clientX, event.clientY);
        if (active.mode === "resize") {
            await dotNetRef.invokeMethodAsync(
                "ResizePictureElementAsync",
                active.kind,
                active.id,
                result.width,
                result.height);
        } else {
            await dotNetRef.invokeMethodAsync(
                "MovePictureElementAsync",
                active.kind,
                active.id,
                result.x,
                result.y);
        }
    };

    state.onPointerCancel = (event) => {
        const active = state.active;
        if (!active || active.pointerId !== event.pointerId) return;
        state.active = null;
        removeActivePointerListeners(state);
        if (active.type === "illustration") {
            cleanupIllustrationDrag(root, active);
            return;
        }

        active.element.classList.remove("picture-element--active");
    };

    state.onFocusOut = async (event) => {
        const target = event.target instanceof HTMLElement ? event.target : null;
        const editor = target?.closest("[data-picture-text-editor]");
        if (!editor || !root.contains(editor)) return;

        const element = editor.closest("[data-picture-element-id]");
        const id = element?.dataset.pictureElementId;
        if (!id || element?.dataset.pictureElementKind !== "text") return;

        await dotNetRef.invokeMethodAsync("UpdatePictureTextBodyAsync", id, editableText(editor));
    };

    root.addEventListener("paste", state.onPaste);
    root.addEventListener("dragover", state.onDragOver);
    root.addEventListener("drop", state.onDrop);
    root.addEventListener("pointerdown", state.onPointerDown, true);
    root.addEventListener("focusout", state.onFocusOut);
    stateByRoot.set(root, state);
}

export function detach(root) {
    const state = stateByRoot.get(root);
    if (!state) return;

    root.removeEventListener("paste", state.onPaste);
    root.removeEventListener("dragover", state.onDragOver);
    root.removeEventListener("drop", state.onDrop);
    root.removeEventListener("pointerdown", state.onPointerDown, true);
    root.removeEventListener("focusout", state.onFocusOut);
    removeActivePointerListeners(state);
    cleanupActive(root, state.active);
    stateByRoot.delete(root);
}

function firstImageFile(files) {
    if (!files) return null;
    for (const file of files) {
        if (file && (file.type === "image/png" || file.type === "image/jpeg")) return file;
    }
    return null;
}

async function sendImageFile(dotNetRef, file, xPercent, yPercent) {
    const base64 = await fileToBase64(file);
    await dotNetRef.invokeMethodAsync(
        "AddDroppedImageAsync",
        file.name || "dropped-image",
        file.type,
        base64,
        xPercent,
        yPercent);
}

function fileToBase64(file) {
    return new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => {
            const result = String(reader.result ?? "");
            resolve(result.includes(",") ? result.split(",", 2)[1] : result);
        };
        reader.onerror = () => reject(reader.error);
        reader.readAsDataURL(file);
    });
}

function pagePoint(root, clientX, clientY) {
    const page = root.querySelector(".picture-page");
    if (!page) return null;

    const rect = page.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return null;
    return {
        x: clamp(((clientX - rect.left) / rect.width) * 100, 0, 100),
        y: clamp(((clientY - rect.top) / rect.height) * 100, 0, 100),
    };
}

function applyPointerPreview(active, clientX, clientY) {
    const rect = active.page.getBoundingClientRect();

    if (active.mode === "resize") {
        const pointerX = ((clientX - rect.left) / rect.width) * 100;
        const pointerY = ((clientY - rect.top) / rect.height) * 100;
        const width = clamp(pointerX - active.startX, 5, Math.max(5, 100 - active.startX));
        const height = clamp(pointerY - active.startY, 5, Math.max(5, 100 - active.startY));
        active.element.style.width = `${width}%`;
        active.element.style.height = `${height}%`;
        return { width, height };
    }

    const pointerX = ((clientX - rect.left) / rect.width) * 100;
    const pointerY = ((clientY - rect.top) / rect.height) * 100;
    const x = clamp(pointerX - active.offsetXPercent, 0, Math.max(0, 100 - active.startWidth));
    const y = clamp(pointerY - active.offsetYPercent, 0, Math.max(0, 100 - active.startHeight));
    active.element.style.left = `${x}%`;
    active.element.style.top = `${y}%`;
    return { x, y };
}

function elementPercents(pageRect, elementRect) {
    return {
        x: ((elementRect.left - pageRect.left) / pageRect.width) * 100,
        y: ((elementRect.top - pageRect.top) / pageRect.height) * 100,
        width: (elementRect.width / pageRect.width) * 100,
        height: (elementRect.height / pageRect.height) * 100,
    };
}

function removeActivePointerListeners(state) {
    window.removeEventListener("pointermove", state.onPointerMove, true);
    window.removeEventListener("pointerup", state.onPointerUp, true);
    window.removeEventListener("pointercancel", state.onPointerCancel, true);
}

function setIllustrationDropTarget(root, active, clientX, clientY) {
    const next = illustrationDropTargetAt(root, clientX, clientY);
    if (active.currentTarget === next) return next;

    active.currentTarget?.classList.remove("illustration-drop-zone--active");
    next?.classList.add("illustration-drop-zone--active");
    active.currentTarget = next;
    return next;
}

function illustrationDropTargetAt(root, clientX, clientY) {
    const target = document.elementFromPoint(clientX, clientY);
    const dropTarget = target instanceof Element
        ? target.closest("[data-illustration-drop-target]")
        : null;
    return dropTarget && root.contains(dropTarget) ? dropTarget : null;
}

function cleanupActive(root, active) {
    if (!active) return;
    if (active.type === "illustration") {
        cleanupIllustrationDrag(root, active);
        return;
    }

    active.element?.classList.remove("picture-element--active");
}

function cleanupIllustrationDrag(root, active) {
    active.element?.classList.remove("illustration-block--dragging");
    active.currentTarget?.classList.remove("illustration-drop-zone--active");
    active.currentTarget = null;
    root.classList.remove("illustration-drag-active");
}

function isFormControl(target) {
    return !!target.closest("input,select,button,[contenteditable='true']");
}

function editableText(editor) {
    return (editor.innerText ?? editor.textContent ?? "")
        .replace(/\r\n/g, "\n")
        .replace(/\u00a0/g, " ");
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
