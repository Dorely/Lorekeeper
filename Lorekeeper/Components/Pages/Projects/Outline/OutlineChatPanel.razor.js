// Auto-scrolls a chat message container to the bottom as new content arrives,
// but only if the user hasn't scrolled up to read history. `force=true` always scrolls
// (used after the user sends a message, since they expect the view to follow).
const NEAR_BOTTOM_PX = 80;

export function scrollToBottom(element, force) {
    if (!element) return;
    if (!force) {
        const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
        if (distanceFromBottom > NEAR_BOTTOM_PX) return;
    }
    element.scrollTop = element.scrollHeight;
}

// Auto-grows a textarea up to ~half of its containing chat frame, so the user can
// always see what they're typing without having to scroll the message itself.
// Beyond the cap the textarea scrolls internally.
function resize(el) {
    if (!el) return;
    // Reset to measure true scrollHeight, then clamp.
    el.style.height = 'auto';
    const cap = computeCap(el);
    const next = Math.min(el.scrollHeight, cap);
    el.style.height = next + 'px';
    el.style.overflowY = el.scrollHeight > cap ? 'auto' : 'hidden';
}

function computeCap(el) {
    // Walk up to find the nearest .outline-chat container; cap at half of it.
    let frame = el.closest('.outline-chat');
    const frameH = frame ? frame.clientHeight : (window.innerHeight || 600);
    return Math.max(120, Math.floor(frameH * 0.5));
}

export function attachAutoSize(element) {
    if (!element) return;
    if (element.__autoSizeAttached) {
        resize(element);
        return;
    }
    element.__autoSizeAttached = true;
    element.__autoSizeHandler = () => resize(element);
    element.addEventListener('input', element.__autoSizeHandler);
    resize(element);
}

export function resetAutoSize(element) {
    if (!element) return;
    element.style.height = 'auto';
    element.style.overflowY = 'hidden';
    resize(element);
}

