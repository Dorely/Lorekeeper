const PREVIEW_ID = "lk-appearance-preview";
const SAVED_ID = "lk-custom-theme";

function ensureStyle(id) {
    let style = document.getElementById(id);
    if (!style) {
        style = document.createElement("style");
        style.id = id;
        document.head.appendChild(style);
    }
    return style;
}

export function setPreview(css) {
    ensureStyle(PREVIEW_ID).textContent = css ?? "";
}

export function applySaved(css) {
    ensureStyle(SAVED_ID).textContent = css ?? "";
    document.getElementById(PREVIEW_ID)?.remove();
}

export function clearPreview() {
    document.getElementById(PREVIEW_ID)?.remove();
}

export function activeTheme() {
    return document.documentElement.getAttribute("data-lk-theme") ?? "light";
}
