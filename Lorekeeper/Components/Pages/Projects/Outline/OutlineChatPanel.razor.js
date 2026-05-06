const NEAR_BOTTOM_PX = 48;
const USER_INTENT_MS = 700;

export function scrollToBottom(element, force) {
    if (!element) return;
    const state = ensureScroller(element);
    if (force) state.autoFollow = true;
    if (!force && !state.autoFollow) return;

    state.forcePending = state.forcePending || force;
    if (state.raf) return;

    state.raf = requestAnimationFrame(() => {
        state.raf = requestAnimationFrame(() => {
            state.raf = 0;
            if (!state.forcePending && !state.autoFollow) return;

            state.programmatic = true;
            element.scrollTop = element.scrollHeight;
            state.forcePending = false;
            state.autoFollow = true;

            requestAnimationFrame(() => {
                state.programmatic = false;
            });
        });
    });
}

export function attachScroller(element) {
    if (!element) return;
    const state = ensureScroller(element);
    if (state.attached) return;

    state.attached = true;
    state.onUserIntent = () => markUserIntent(state);
    state.onScroll = () => handleScrollIntent(element, state);
    state.onKeyDown = event => {
        if (['ArrowUp', 'ArrowDown', 'Home', 'End', 'PageUp', 'PageDown', ' '].includes(event.key)) {
            markUserIntent(state);
        }
    };

    element.addEventListener('wheel', state.onUserIntent, { passive: true });
    element.addEventListener('touchstart', state.onUserIntent, { passive: true });
    element.addEventListener('pointerdown', state.onUserIntent, { passive: true });
    element.addEventListener('keydown', state.onKeyDown);
    element.addEventListener('scroll', state.onScroll, { passive: true });
}

export function resumeAutoFollow(element) {
    if (!element) return;
    const state = ensureScroller(element);
    state.autoFollow = true;
    state.userIntentUntil = 0;
}

function ensureScroller(element) {
    if (element.__outlineScroller) return element.__outlineScroller;
    element.__outlineScroller = {
        attached: false,
        autoFollow: true,
        forcePending: false,
        programmatic: false,
        raf: 0,
        userIntentUntil: 0,
    };
    return element.__outlineScroller;
}

function markUserIntent(state) {
    state.userIntentUntil = Date.now() + USER_INTENT_MS;
}

function handleScrollIntent(element, state) {
    if (state.programmatic) return;

    if (isNearBottom(element)) {
        state.autoFollow = true;
        return;
    }

    if (Date.now() <= state.userIntentUntil) {
        state.autoFollow = false;
    }
}

function isNearBottom(element) {
    return element.scrollHeight - element.scrollTop - element.clientHeight <= NEAR_BOTTOM_PX;
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

