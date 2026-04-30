// Debounced textarea bridge with line-number gutter for Blazor.
// attach(elements, dotNetRef, debounceMs, initialValue)
//   elements: { textarea, gutter }
// returns handle: { setValue, flush, setReadOnly, dispose }

export function attach(elements, dotNetRef, debounceMs, initialValue) {
    const el = elements.textarea ?? elements; // back-compat
    const gutter = elements.gutter ?? null;

    if (initialValue !== undefined && initialValue !== null) {
        el.value = initialValue;
    }

    let timer = null;
    let lastSent = el.value;

    const renderGutter = () => {
        if (!gutter) return;
        const text = el.value;
        const lines = text.length === 0 ? 1 : text.split("\n").length;
        const width = Math.max(4, String(lines).length);
        let out = "";
        for (let i = 1; i <= lines; i++) {
            out += String(i).padStart(width, "0") + "\n";
        }
        gutter.textContent = out;
    };

    const syncScroll = () => {
        if (gutter) gutter.scrollTop = el.scrollTop;
    };

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
        renderGutter();
        if (timer) clearTimeout(timer);
        timer = setTimeout(fireNow, debounceMs);
    };

    const onBlur = () => fireNow();

    el.addEventListener("input", onInput);
    el.addEventListener("blur", onBlur);
    el.addEventListener("scroll", syncScroll);

    renderGutter();

    return {
        setValue(v) {
            // Programmatic switch — update without firing.
            if (timer) { clearTimeout(timer); timer = null; }
            el.value = v ?? "";
            lastSent = el.value;
            renderGutter();
        },
        flush() {
            fireNow();
        },
        setReadOnly(readOnly) {
            el.readOnly = !!readOnly;
        },
        dispose() {
            if (timer) { clearTimeout(timer); timer = null; }
            el.removeEventListener("input", onInput);
            el.removeEventListener("blur", onBlur);
            el.removeEventListener("scroll", syncScroll);
        }
    };
}
