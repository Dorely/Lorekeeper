const states = new WeakMap();

export async function loadCropCanvas(canvas, previewCanvas, imageUrl) {
    if (!isCanvas(canvas) || !isCanvas(previewCanvas)) return;
    const image = await loadImage(imageUrl);
    const source = document.createElement("canvas");
    source.width = image.naturalWidth || image.width;
    source.height = image.naturalHeight || image.height;
    source.getContext("2d").drawImage(image, 0, 0, source.width, source.height);

    const state = {
        source,
        previewCanvas,
        tool: "crop",
        view: null,
        selection: defaultSelection(source),
        drag: null,
        resizeObserver: null,
    };
    states.set(canvas, state);
    attach(canvas, state);
    state.resizeObserver = new ResizeObserver(() => render(canvas, state));
    state.resizeObserver.observe(canvas);
    state.resizeObserver.observe(previewCanvas);
    render(canvas, state);
}

export function setCropTool(canvas, tool) {
    const state = states.get(canvas);
    if (!state) return;
    state.tool = tool === "pan" ? "pan" : "crop";
    canvas.style.cursor = state.tool === "pan" ? "grab" : "crosshair";
}

export function resetCrop(canvas) {
    const state = states.get(canvas);
    if (!state) return;
    state.selection = defaultSelection(state.source);
    state.view = null;
    state.drag = null;
    render(canvas, state);
}

export function getCropRegion(canvas) {
    const state = states.get(canvas);
    if (!state?.source || !state.selection) throw new Error("Crop canvas is not ready.");
    const selection = normalizedSelection(state.selection, state.source);
    return {
        xPercent: round(selection.x / state.source.width * 100),
        yPercent: round(selection.y / state.source.height * 100),
        widthPercent: round(selection.width / state.source.width * 100),
        heightPercent: round(selection.height / state.source.height * 100),
    };
}

export function disposeCropCanvas(canvas) {
    const state = states.get(canvas);
    state?.resizeObserver?.disconnect();
    states.delete(canvas);
}

function attach(canvas, state) {
    canvas.addEventListener("wheel", event => onWheel(canvas, state, event), { passive: false });
    canvas.addEventListener("pointerdown", event => onPointerDown(canvas, state, event));
    canvas.addEventListener("pointermove", event => onPointerMove(canvas, state, event));
    canvas.addEventListener("pointerup", event => onPointerUp(canvas, state, event));
    canvas.addEventListener("pointercancel", event => onPointerUp(canvas, state, event));
}

function onWheel(canvas, state, event) {
    event.preventDefault();
    ensureView(canvas, state);
    const screen = canvasPoint(canvas, event);
    const before = screenToWorld(state.view, screen.x, screen.y);
    state.view.scale = clamp(state.view.scale * (event.deltaY < 0 ? 1.12 : .88), .04, 64);
    state.view.x = screen.x - before.x * state.view.scale;
    state.view.y = screen.y - before.y * state.view.scale;
    render(canvas, state);
}

function onPointerDown(canvas, state, event) {
    event.preventDefault();
    canvas.setPointerCapture?.(event.pointerId);
    ensureView(canvas, state);
    const screen = canvasPoint(canvas, event);
    if (state.tool === "pan") {
        state.drag = { type: "pan", screen, view: { ...state.view } };
        canvas.style.cursor = "grabbing";
        return;
    }

    const world = clampWorld(screenToWorld(state.view, screen.x, screen.y), state.source);
    const handle = hitHandle(state, screen);
    if (handle) {
        state.drag = { type: "resize", handle, start: world, selection: { ...state.selection } };
    } else if (contains(state.selection, world)) {
        state.drag = { type: "move", start: world, selection: { ...state.selection } };
    } else {
        state.selection = { x: world.x, y: world.y, width: 1, height: 1 };
        state.drag = { type: "draw", start: world };
    }
    render(canvas, state);
}

function onPointerMove(canvas, state, event) {
    if (!state.drag) return;
    event.preventDefault();
    const screen = canvasPoint(canvas, event);
    if (state.drag.type === "pan") {
        state.view.x = state.drag.view.x + screen.x - state.drag.screen.x;
        state.view.y = state.drag.view.y + screen.y - state.drag.screen.y;
        render(canvas, state);
        return;
    }

    const world = clampWorld(screenToWorld(state.view, screen.x, screen.y), state.source);
    if (state.drag.type === "draw") {
        state.selection = rectFromPoints(state.drag.start, world);
    } else if (state.drag.type === "move") {
        const dx = world.x - state.drag.start.x;
        const dy = world.y - state.drag.start.y;
        state.selection = {
            ...state.drag.selection,
            x: clamp(state.drag.selection.x + dx, 0, state.source.width - state.drag.selection.width),
            y: clamp(state.drag.selection.y + dy, 0, state.source.height - state.drag.selection.height),
        };
    } else if (state.drag.type === "resize") {
        state.selection = resizeSelection(state.drag.selection, state.drag.handle, world, state.source);
    }
    render(canvas, state);
}

function onPointerUp(canvas, state, event) {
    if (!state.drag) return;
    event.preventDefault();
    state.selection = normalizedSelection(state.selection, state.source);
    state.drag = null;
    canvas.style.cursor = state.tool === "pan" ? "grab" : "crosshair";
    try { canvas.releasePointerCapture?.(event.pointerId); } catch { }
    render(canvas, state);
}

function render(canvas, state) {
    const ctx = resizeCanvas(canvas);
    drawChecker(ctx, canvas.width, canvas.height);
    if (!state.source) return;
    ensureView(canvas, state);
    ctx.save();
    ctx.setTransform(state.view.scale, 0, 0, state.view.scale, state.view.x, state.view.y);
    ctx.drawImage(state.source, 0, 0);
    ctx.restore();

    const selection = selectionScreenRect(state);
    ctx.fillStyle = "rgba(15,23,42,.58)";
    ctx.fillRect(0, 0, canvas.width, Math.max(0, selection.top));
    ctx.fillRect(0, selection.bottom, canvas.width, Math.max(0, canvas.height - selection.bottom));
    ctx.fillRect(0, selection.top, Math.max(0, selection.left), Math.max(0, selection.height));
    ctx.fillRect(selection.right, selection.top, Math.max(0, canvas.width - selection.right), Math.max(0, selection.height));
    ctx.strokeStyle = "#38bdf8";
    ctx.lineWidth = Math.max(2, window.devicePixelRatio || 1);
    ctx.strokeRect(selection.left, selection.top, selection.width, selection.height);
    drawHandles(ctx, selection);
    renderPreview(state);
}

function renderPreview(state) {
    const canvas = state.previewCanvas;
    const ctx = resizeCanvas(canvas);
    drawChecker(ctx, canvas.width, canvas.height);
    const selection = normalizedSelection(state.selection, state.source);
    const scale = Math.min(canvas.width / selection.width, canvas.height / selection.height);
    const width = selection.width * scale;
    const height = selection.height * scale;
    ctx.drawImage(
        state.source,
        selection.x,
        selection.y,
        selection.width,
        selection.height,
        (canvas.width - width) / 2,
        (canvas.height - height) / 2,
        width,
        height);
}

function hitHandle(state, point) {
    const rect = selectionScreenRect(state);
    const radius = 14 * (window.devicePixelRatio || 1);
    const handles = {
        nw: { x: rect.left, y: rect.top },
        ne: { x: rect.right, y: rect.top },
        sw: { x: rect.left, y: rect.bottom },
        se: { x: rect.right, y: rect.bottom },
    };
    for (const [name, handle] of Object.entries(handles)) {
        if (Math.hypot(point.x - handle.x, point.y - handle.y) <= radius) return name;
    }
    return null;
}

function resizeSelection(original, handle, point, source) {
    const left = handle.includes("w") ? point.x : original.x;
    const right = handle.includes("e") ? point.x : original.x + original.width;
    const top = handle.includes("n") ? point.y : original.y;
    const bottom = handle.includes("s") ? point.y : original.y + original.height;
    return normalizedSelection({ x: left, y: top, width: right - left, height: bottom - top }, source);
}

function defaultSelection(source) {
    return { x: source.width * .1, y: source.height * .1, width: source.width * .8, height: source.height * .8 };
}

function normalizedSelection(selection, source) {
    const left = clamp(Math.min(selection.x, selection.x + selection.width), 0, source.width - 1);
    const top = clamp(Math.min(selection.y, selection.y + selection.height), 0, source.height - 1);
    const right = clamp(Math.max(selection.x, selection.x + selection.width), left + 1, source.width);
    const bottom = clamp(Math.max(selection.y, selection.y + selection.height), top + 1, source.height);
    return { x: left, y: top, width: right - left, height: bottom - top };
}

function rectFromPoints(a, b) {
    return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), width: Math.abs(a.x - b.x), height: Math.abs(a.y - b.y) };
}

function contains(rect, point) {
    return point.x >= rect.x && point.x <= rect.x + rect.width && point.y >= rect.y && point.y <= rect.y + rect.height;
}

function selectionScreenRect(state) {
    const selection = normalizedSelection(state.selection, state.source);
    const left = state.view.x + selection.x * state.view.scale;
    const top = state.view.y + selection.y * state.view.scale;
    const width = selection.width * state.view.scale;
    const height = selection.height * state.view.scale;
    return { left, top, width, height, right: left + width, bottom: top + height };
}

function drawHandles(ctx, rect) {
    const size = 9 * (window.devicePixelRatio || 1);
    ctx.fillStyle = "#fff";
    ctx.strokeStyle = "#0284c7";
    for (const point of [[rect.left, rect.top], [rect.right, rect.top], [rect.left, rect.bottom], [rect.right, rect.bottom]]) {
        ctx.fillRect(point[0] - size / 2, point[1] - size / 2, size, size);
        ctx.strokeRect(point[0] - size / 2, point[1] - size / 2, size, size);
    }
}

function ensureView(canvas, state) {
    if (state.view) return;
    const margin = 28 * (window.devicePixelRatio || 1);
    const scale = Math.max(.04, Math.min(
        (canvas.width - margin * 2) / state.source.width,
        (canvas.height - margin * 2) / state.source.height));
    state.view = {
        scale,
        x: (canvas.width - state.source.width * scale) / 2,
        y: (canvas.height - state.source.height * scale) / 2,
    };
}

function resizeCanvas(canvas) {
    const rect = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    const width = Math.max(1, Math.round(rect.width * dpr));
    const height = Math.max(1, Math.round(rect.height * dpr));
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
    }
    return canvas.getContext("2d");
}

function canvasPoint(canvas, event) {
    const rect = canvas.getBoundingClientRect();
    return {
        x: (event.clientX - rect.left) * canvas.width / Math.max(1, rect.width),
        y: (event.clientY - rect.top) * canvas.height / Math.max(1, rect.height),
    };
}

function screenToWorld(view, x, y) {
    return { x: (x - view.x) / view.scale, y: (y - view.y) / view.scale };
}

function clampWorld(point, source) {
    return { x: clamp(point.x, 0, source.width), y: clamp(point.y, 0, source.height) };
}

function drawChecker(ctx, width, height) {
    ctx.fillStyle = "#f8f9fa";
    ctx.fillRect(0, 0, width, height);
    const size = 16 * (window.devicePixelRatio || 1);
    ctx.fillStyle = "#e9ecef";
    for (let y = 0; y < height; y += size) {
        for (let x = (Math.round(y / size) % 2 === 0 ? 0 : size); x < width; x += size * 2)
            ctx.fillRect(x, y, size, size);
    }
}

function loadImage(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = () => reject(new Error("Image could not be loaded for cropping."));
        image.src = url;
    });
}

function isCanvas(value) {
    return value && typeof value.getContext === "function";
}

function round(value) {
    return Math.round(value * 1_000_000) / 1_000_000;
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, Number(value) || 0));
}
