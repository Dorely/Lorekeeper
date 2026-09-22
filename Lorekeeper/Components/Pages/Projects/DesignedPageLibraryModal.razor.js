const states = new WeakMap();
export function attach(root, dotNet) {
    if (!root?.isConnected) return { dispose() {} };
    const previous = document.activeElement;
    const dialog = () => root.querySelector('[role="alertdialog"]') || root.querySelector('[role="dialog"]');
    const focusable = () => [...(dialog()?.querySelectorAll('button, input, select, a[href], [tabindex="0"]') || [])]
        .filter(element => !element.disabled && element.getClientRects().length);
    const focusFirst = () => (focusable()[0] || dialog())?.focus();
    const keydown = event => {
        if (!root.isConnected || !dialog()) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            void dotNet.invokeMethodAsync('DismissAsync');
        } else if (event.key === 'Tab') {
            const items = focusable();
            const first = items[0], last = items.at(-1);
            if (!first) { event.preventDefault(); dialog().focus(); }
            else if (event.shiftKey && (document.activeElement === first || !dialog().contains(document.activeElement))) {
                event.preventDefault(); last.focus();
            } else if (!event.shiftKey && (document.activeElement === last || !dialog().contains(document.activeElement))) {
                event.preventDefault(); first.focus();
            }
        }
    };
    const focusin = event => { if (root.isConnected && dialog() && !dialog().contains(event.target)) focusFirst(); };
    let currentDialog = dialog();
    let confirmationFocus = null;
    const observer = new MutationObserver(() => {
        const next = dialog();
        if (!root.isConnected || !next) return;
        if (next === currentDialog) return;
        if (next.getAttribute('role') === 'alertdialog') { confirmationFocus = document.activeElement; focusFirst(); }
        else if (confirmationFocus?.isConnected) confirmationFocus.focus();
        else focusFirst();
        currentDialog = next;
    });
    observer.observe(root, { childList: true, subtree: true });
    document.addEventListener('keydown', keydown, true);
    document.addEventListener('focusin', focusin);
    states.set(root, { previous, keydown, focusin, observer });
    root.querySelector('input[type="search"]')?.focus();
    return { dispose: () => detach(root) };
}
function detach(root) {
    const state = states.get(root);
    if (!state) return;
    document.removeEventListener('keydown', state.keydown, true);
    document.removeEventListener('focusin', state.focusin);
    state.observer.disconnect();
    if (state.previous?.isConnected) state.previous.focus();
    states.delete(root);
}
