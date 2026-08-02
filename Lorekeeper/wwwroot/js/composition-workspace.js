export function begin(stage, pointerId) {
    if (!stage) {
        throw new Error("The page canvas is not available.");
    }
    stage.setPointerCapture(pointerId);
    const rect = stage.getBoundingClientRect();
    return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
}

export function end(stage, pointerId) {
    if (stage?.hasPointerCapture(pointerId)) {
        stage.releasePointerCapture(pointerId);
    }
}
