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
        const element = event.target.closest("[data-picture-element-id]");
        if (!element || !root.contains(element)) return;

        const isResize = !!event.target.closest("[data-resize-handle]");
        const isDragHandle = !!event.target.closest("[data-drag-handle]");
        const kind = element.dataset.pictureElementKind;
        if (!kind) return;
        if (!isResize && kind === "text" && !isDragHandle) return;
        if (!isResize && isFormControl(event.target) && !isDragHandle) return;

        const page = element.closest(".picture-page");
        if (!page) return;

        const pageRect = page.getBoundingClientRect();
        const elementRect = element.getBoundingClientRect();
        state.active = {
            pointerId: event.pointerId,
            element,
            page,
            kind,
            id: element.dataset.pictureElementId,
            mode: isResize ? "resize" : "move",
            pageRect,
            offsetX: event.clientX - elementRect.left,
            offsetY: event.clientY - elementRect.top,
        };

        element.setPointerCapture?.(event.pointerId);
        element.classList.add("picture-element--active");
        event.preventDefault();
    };

    state.onPointerMove = (event) => {
        const active = state.active;
        if (!active || active.pointerId !== event.pointerId) return;
        event.preventDefault();
        applyPointerPreview(active, event.clientX, event.clientY);
    };

    state.onPointerUp = async (event) => {
        const active = state.active;
        if (!active || active.pointerId !== event.pointerId) return;
        event.preventDefault();
        state.active = null;
        active.element.releasePointerCapture?.(event.pointerId);
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
        active.element.releasePointerCapture?.(event.pointerId);
        active.element.classList.remove("picture-element--active");
        state.active = null;
    };

    root.addEventListener("paste", state.onPaste);
    root.addEventListener("dragover", state.onDragOver);
    root.addEventListener("drop", state.onDrop);
    root.addEventListener("pointerdown", state.onPointerDown);
    root.addEventListener("pointermove", state.onPointerMove);
    root.addEventListener("pointerup", state.onPointerUp);
    root.addEventListener("pointercancel", state.onPointerCancel);
    stateByRoot.set(root, state);
}

export function detach(root) {
    const state = stateByRoot.get(root);
    if (!state) return;

    root.removeEventListener("paste", state.onPaste);
    root.removeEventListener("dragover", state.onDragOver);
    root.removeEventListener("drop", state.onDrop);
    root.removeEventListener("pointerdown", state.onPointerDown);
    root.removeEventListener("pointermove", state.onPointerMove);
    root.removeEventListener("pointerup", state.onPointerUp);
    root.removeEventListener("pointercancel", state.onPointerCancel);
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
    active.pageRect = rect;

    if (active.mode === "resize") {
        const elementRect = active.element.getBoundingClientRect();
        const width = clamp(((clientX - elementRect.left) / rect.width) * 100, 5, 100);
        const height = clamp(((clientY - elementRect.top) / rect.height) * 100, 5, 100);
        active.element.style.width = `${width}%`;
        active.element.style.height = `${height}%`;
        return { width, height };
    }

    const width = (active.element.getBoundingClientRect().width / rect.width) * 100;
    const height = (active.element.getBoundingClientRect().height / rect.height) * 100;
    const x = clamp(((clientX - active.offsetX - rect.left) / rect.width) * 100, 0, Math.max(0, 100 - width));
    const y = clamp(((clientY - active.offsetY - rect.top) / rect.height) * 100, 0, Math.max(0, 100 - height));
    active.element.style.left = `${x}%`;
    active.element.style.top = `${y}%`;
    return { x, y };
}

function isFormControl(target) {
    return !!target.closest("textarea,input,select,button");
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
