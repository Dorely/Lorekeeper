const states = new WeakMap();
const maskPaintColor = "rgb(31,111,235)";
const maskPaintOpacity = 0.46;

export async function loadMaskCanvas(canvas, imageUrl, tool, brushSize) {
    if (!isCanvas(canvas)) return;
    const image = await loadImage(imageUrl);
    const source = document.createElement("canvas");
    source.width = image.naturalWidth || image.width;
    source.height = image.naturalHeight || image.height;
    const sourceCtx = source.getContext("2d");
    sourceCtx.drawImage(image, 0, 0, source.width, source.height);

    const paint = document.createElement("canvas");
    paint.width = source.width;
    paint.height = source.height;

    let state = states.get(canvas);
    if (!state) {
        state = {
            source,
            paint,
            tool: tool || "brush",
            brushSize: Number(brushSize) || 32,
            view: null,
            drag: null,
        };
        states.set(canvas, state);
        attach(canvas, state);
    } else {
        state.source = source;
        state.paint = paint;
        state.tool = tool || "brush";
        state.brushSize = Number(brushSize) || 32;
        state.view = null;
        state.drag = null;
    }

    render(canvas, state);
}

export function setMaskTool(canvas, tool) {
    const state = states.get(canvas);
    if (state) state.tool = tool || "brush";
}

export function setMaskBrushSize(canvas, brushSize) {
    const state = states.get(canvas);
    if (state) state.brushSize = Number(brushSize) || state.brushSize;
}

export function clearMask(canvas) {
    const state = states.get(canvas);
    if (!state) return;
    state.paint.getContext("2d").clearRect(0, 0, state.paint.width, state.paint.height);
    render(canvas, state);
}

export function hasMaskPaint(canvas) {
    const state = states.get(canvas);
    if (!state) return false;
    const pixels = state.paint.getContext("2d", { willReadFrequently: true })
        .getImageData(0, 0, state.paint.width, state.paint.height)
        .data;
    for (let i = 3; i < pixels.length; i += 4) {
        if (pixels[i] > 0) return true;
    }
    return false;
}

export function exportMaskPng(canvas) {
    const state = states.get(canvas);
    if (!state) throw new Error("Mask canvas is not ready.");

    const mask = document.createElement("canvas");
    mask.width = state.source.width;
    mask.height = state.source.height;
    const ctx = mask.getContext("2d");
    const paintPixels = state.paint.getContext("2d")
        .getImageData(0, 0, mask.width, mask.height)
        .data;
    const maskPixels = ctx.createImageData(mask.width, mask.height);
    for (let i = 3; i < maskPixels.data.length; i += 4) {
        maskPixels.data[i] = paintPixels[i] > 0 ? 0 : 255;
    }
    ctx.putImageData(maskPixels, 0, 0);
    return mask.toDataURL("image/png");
}

function attach(canvas, state) {
    canvas.addEventListener("wheel", event => onWheel(canvas, state, event), { passive: false });
    canvas.addEventListener("pointerdown", event => onPointerDown(canvas, state, event));
    canvas.addEventListener("pointermove", event => onPointerMove(canvas, state, event));
    canvas.addEventListener("pointerup", event => onPointerUp(canvas, state, event));
    canvas.addEventListener("pointercancel", event => onPointerUp(canvas, state, event));
    canvas.addEventListener("pointerleave", event => {
        if (state.drag) onPointerUp(canvas, state, event);
    });
}

function onWheel(canvas, state, event) {
    event.preventDefault();
    ensureView(canvas, state);
    const point = canvasPoint(canvas, event);
    const before = screenToWorld(state.view, point.x, point.y);
    const factor = event.deltaY < 0 ? 1.12 : 0.88;
    state.view.scale = clamp(state.view.scale * factor, 0.04, 64);
    state.view.x = point.x - before.x * state.view.scale;
    state.view.y = point.y - before.y * state.view.scale;
    render(canvas, state);
}

function onPointerDown(canvas, state, event) {
    if (!state.source) return;
    event.preventDefault();
    canvas.setPointerCapture?.(event.pointerId);
    ensureView(canvas, state);

    if (state.tool === "pan") {
        const point = canvasPoint(canvas, event);
        state.drag = { type: "pan", start: point, view: { ...state.view } };
        return;
    }

    const point = worldPoint(canvas, state, event);
    state.drag = {
        type: "paint",
        lastPoint: point,
        erase: state.tool === "erase",
    };
    paintStroke(state, point, point, state.drag.erase);
    render(canvas, state);
}

function onPointerMove(canvas, state, event) {
    if (!state.drag) return;
    event.preventDefault();

    if (state.drag.type === "pan") {
        const point = canvasPoint(canvas, event);
        state.view.x = state.drag.view.x + point.x - state.drag.start.x;
        state.view.y = state.drag.view.y + point.y - state.drag.start.y;
        render(canvas, state);
        return;
    }

    const point = worldPoint(canvas, state, event);
    paintStroke(state, state.drag.lastPoint, point, state.drag.erase);
    state.drag.lastPoint = point;
    render(canvas, state);
}

function onPointerUp(canvas, state, event) {
    if (!state.drag) return;
    event.preventDefault();
    state.drag = null;
    try { canvas.releasePointerCapture?.(event.pointerId); } catch { }
    render(canvas, state);
}

function render(canvas, state) {
    const ctx = resizeCanvas(canvas);
    drawStageBackground(ctx, canvas.width, canvas.height);
    if (!state.source) return;
    ensureView(canvas, state);
    ctx.save();
    ctx.imageSmoothingEnabled = false;
    ctx.setTransform(state.view.scale, 0, 0, state.view.scale, state.view.x, state.view.y);
    ctx.drawImage(state.source, 0, 0);
    ctx.globalAlpha = maskPaintOpacity;
    ctx.drawImage(state.paint, 0, 0);
    ctx.globalAlpha = 1;
    ctx.strokeStyle = "#38bdf8";
    ctx.lineWidth = 2 / state.view.scale;
    ctx.strokeRect(0.5, 0.5, state.source.width - 1, state.source.height - 1);
    ctx.restore();
}

function paintStroke(state, from, to, erase) {
    if (!from || !to) return;
    const ctx = state.paint.getContext("2d");
    const radius = Math.max(1, state.brushSize / 2);
    ctx.save();
    ctx.globalCompositeOperation = erase ? "destination-out" : "source-over";
    ctx.fillStyle = maskPaintColor;
    ctx.strokeStyle = maskPaintColor;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.lineWidth = radius * 2;

    if (from.x === to.x && from.y === to.y) {
        ctx.beginPath();
        ctx.arc(to.x, to.y, radius, 0, Math.PI * 2);
        ctx.fill();
    } else {
        ctx.beginPath();
        ctx.moveTo(from.x, from.y);
        ctx.lineTo(to.x, to.y);
        ctx.stroke();
    }
    ctx.restore();
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

function ensureView(canvas, state) {
    if (state.view) return;
    const margin = 28;
    const scale = Math.max(0.04, Math.min(
        (canvas.width - margin * 2) / state.source.width,
        (canvas.height - margin * 2) / state.source.height));
    state.view = {
        scale,
        x: (canvas.width - state.source.width * scale) / 2,
        y: (canvas.height - state.source.height * scale) / 2,
    };
}

function canvasPoint(canvas, event) {
    const rect = canvas.getBoundingClientRect();
    const scaleX = canvas.width / Math.max(1, rect.width);
    const scaleY = canvas.height / Math.max(1, rect.height);
    return {
        x: (event.clientX - rect.left) * scaleX,
        y: (event.clientY - rect.top) * scaleY,
    };
}

function worldPoint(canvas, state, event) {
    const point = canvasPoint(canvas, event);
    const world = screenToWorld(state.view, point.x, point.y);
    return {
        x: clamp(world.x, 0, state.source.width),
        y: clamp(world.y, 0, state.source.height),
    };
}

function screenToWorld(view, x, y) {
    return {
        x: (x - view.x) / view.scale,
        y: (y - view.y) / view.scale,
    };
}

function drawStageBackground(ctx, width, height) {
    ctx.fillStyle = "#f8f9fa";
    ctx.fillRect(0, 0, width, height);
    const size = 16;
    ctx.fillStyle = "#e9ecef";
    for (let y = 0; y < height; y += size) {
        for (let x = (y / size) % 2 === 0 ? 0 : size; x < width; x += size * 2) {
            ctx.fillRect(x, y, size, size);
        }
    }
}

function loadImage(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = reject;
        image.src = url;
    });
}

function isCanvas(value) {
    return value && typeof value.addEventListener === "function" && typeof value.getContext === "function";
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, Number(value) || 0));
}
