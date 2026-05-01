function resize(textarea) {
    if (!textarea) return;
    textarea.style.height = 'auto';
    textarea.style.height = `${textarea.scrollHeight}px`;
}

function attach(textarea) {
    if (!textarea || textarea.dataset.autosizeAttached === 'true') return;

    textarea.dataset.autosizeAttached = 'true';
    textarea.style.overflowY = 'hidden';
    textarea.style.resize = textarea.dataset.autosizeResize ?? 'none';

    textarea.addEventListener('input', () => resize(textarea));
    textarea.addEventListener('change', () => resize(textarea));

    if ('ResizeObserver' in window) {
        const observer = new ResizeObserver(() => resize(textarea));
        observer.observe(textarea);
        textarea.__autosizeObserver = observer;
    }
}

export function refresh(root) {
    const scope = root ?? document;
    const textareas = scope.querySelectorAll('textarea[data-autosize]');
    for (const textarea of textareas) {
        attach(textarea);
        resize(textarea);
    }
}
