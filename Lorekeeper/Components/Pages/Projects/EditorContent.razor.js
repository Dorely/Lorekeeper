// Debounced textarea bridge for Blazor.
// attach(el, dotNetRef, debounceMs, initialValue) -> handle { setValue, flush, dispose }

export function attach(el, dotNetRef, debounceMs, initialValue) {
    if (initialValue !== undefined && initialValue !== null) {
        el.value = initialValue;
    }

    let timer = null;
    let lastSent = el.value;

    const fireNow = () => {
        if (timer) {
            clearTimeout(timer);
            timer = null;
        }
        const v = el.value;
        if (v === lastSent) return;
        lastSent = v;
        try {
            dotNetRef.invokeMethodAsync("OnBodyDebounced", v);
        } catch (_) {
            // dotnet ref may have been disposed
        }
    };

    const onInput = () => {
        if (timer) clearTimeout(timer);
        timer = setTimeout(fireNow, debounceMs);
    };

    const onBlur = () => fireNow();

    el.addEventListener("input", onInput);
    el.addEventListener("blur", onBlur);

    return {
        setValue(v) {
            // Programmatic switch — update without firing.
            if (timer) { clearTimeout(timer); timer = null; }
            el.value = v ?? "";
            lastSent = el.value;
        },
        flush() {
            fireNow();
        },
        dispose() {
            if (timer) { clearTimeout(timer); timer = null; }
            el.removeEventListener("input", onInput);
            el.removeEventListener("blur", onBlur);
        }
    };
}
