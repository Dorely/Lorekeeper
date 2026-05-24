export function attachCoverTextDrag(stage, dotNet) {
    if (!stage) return;

    const state = ensureState(stage);
    state.dotNet = dotNet;
    if (state.attached) return;

    state.attached = true;
    state.onPointerDown = event => {
        const layer = event.target.closest('[data-cover-layer]');
        if (!layer || !stage.contains(layer)) return;

        event.preventDefault();
        state.activeLayer = layer.dataset.coverLayer;
        state.pointerId = event.pointerId;
        layer.setPointerCapture?.(event.pointerId);
        moveLayer(stage, state, event.clientX, event.clientY);
    };

    state.onPointerMove = event => {
        if (!state.activeLayer || event.pointerId !== state.pointerId) return;
        event.preventDefault();
        moveLayer(stage, state, event.clientX, event.clientY);
    };

    state.onPointerUp = event => {
        if (event.pointerId !== state.pointerId) return;
        state.activeLayer = null;
        state.pointerId = null;
    };

    stage.addEventListener('pointerdown', state.onPointerDown);
    window.addEventListener('pointermove', state.onPointerMove, { passive: false });
    window.addEventListener('pointerup', state.onPointerUp);
    window.addEventListener('pointercancel', state.onPointerUp);
}

export function detachCoverTextDrag(stage) {
    const state = stage?.__coverTextEditor;
    if (!state?.attached) return;

    stage.removeEventListener('pointerdown', state.onPointerDown);
    window.removeEventListener('pointermove', state.onPointerMove);
    window.removeEventListener('pointerup', state.onPointerUp);
    window.removeEventListener('pointercancel', state.onPointerUp);
    state.attached = false;
    state.activeLayer = null;
    state.pointerId = null;
}

function ensureState(stage) {
    if (stage.__coverTextEditor) return stage.__coverTextEditor;

    stage.__coverTextEditor = {
        attached: false,
        activeLayer: null,
        pointerId: null,
        dotNet: null,
        raf: 0,
        pending: null,
    };
    return stage.__coverTextEditor;
}

function moveLayer(stage, state, clientX, clientY) {
    const rect = stage.getBoundingClientRect();
    const xPercent = clamp(((clientX - rect.left) / rect.width) * 100, 0, 100);
    const yPercent = clamp(((clientY - rect.top) / rect.height) * 100, 0, 100);
    state.pending = { kind: state.activeLayer, xPercent, yPercent };

    if (state.raf) return;
    state.raf = requestAnimationFrame(() => {
        state.raf = 0;
        const pending = state.pending;
        state.pending = null;
        if (!pending?.kind || !state.dotNet) return;
        state.dotNet.invokeMethodAsync('MoveCoverLayerAsync', pending.kind, pending.xPercent, pending.yPercent);
    });
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}
