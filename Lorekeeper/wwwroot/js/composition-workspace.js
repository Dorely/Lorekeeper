export function begin(stage, pointerId) {
    stage.setPointerCapture(pointerId);
    const rect = stage.getBoundingClientRect();
    return { left: rect.left, top: rect.top, width: rect.width, height: rect.height };
}
