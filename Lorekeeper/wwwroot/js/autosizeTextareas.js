function stateFor(textarea) {
    if (!textarea.__autosizeState) {
        textarea.__autosizeState = {
            attached: false,
            force: false,
            lastValue: null,
            lastWidth: -1,
            observedWidth: -1,
            raf: 0,
        };
    }

    return textarea.__autosizeState;
}

function resize(textarea, force) {
    if (!textarea || !textarea.isConnected) return;

    const state = stateFor(textarea);
    const width = textarea.clientWidth;
    const value = textarea.value;
    if (!force && state.lastWidth === width && state.lastValue === value) return;

    const currentHeight = textarea.style.height;
    textarea.style.height = 'auto';

    const nextHeight = `${textarea.scrollHeight}px`;
    textarea.style.height = currentHeight === nextHeight ? currentHeight : nextHeight;
    textarea.style.overflowY = 'hidden';

    state.lastWidth = width;
    state.observedWidth = width;
    state.lastValue = value;
}

function scheduleResize(textarea, force = false) {
    if (!textarea) return;

    const state = stateFor(textarea);
    state.force = state.force || force;
    if (state.raf) return;

    state.raf = requestAnimationFrame(() => {
        state.raf = 0;
        const shouldForce = state.force;
        state.force = false;
        resize(textarea, shouldForce);
    });
}

function attach(textarea) {
    if (!textarea) return;

    const state = stateFor(textarea);
    if (state.attached) return;

    state.attached = true;
    state.observedWidth = textarea.clientWidth;
    textarea.style.overflowY = 'hidden';
    textarea.style.resize = textarea.dataset.autosizeResize ?? 'none';

    textarea.addEventListener('input', () => scheduleResize(textarea));
    textarea.addEventListener('change', () => scheduleResize(textarea));

    if ('ResizeObserver' in window) {
        const observer = new ResizeObserver(() => {
            const width = textarea.clientWidth;
            if (width === state.observedWidth) return;

            state.observedWidth = width;
            scheduleResize(textarea, true);
        });
        observer.observe(textarea);
        textarea.__autosizeObserver = observer;
    }
}

export function refresh(root) {
    const scope = root ?? document;
    if (!scope.querySelectorAll) return;

    const textareas = scope.querySelectorAll('textarea[data-autosize]');
    for (const textarea of textareas) {
        attach(textarea);
        scheduleResize(textarea);
    }
}
