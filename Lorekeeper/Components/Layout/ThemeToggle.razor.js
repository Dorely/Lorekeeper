// Theme toggle browser state. Mirrors the pre-paint bootstrap in App.razor so the
// toggle and the initial render always agree on the effective theme.

const THEME_KEY = "Lorekeeper.ui.theme";

function readStoredTheme() {
    try {
        const stored = window.localStorage.getItem(THEME_KEY);
        if (stored === "light" || stored === "dark") return stored;
    } catch (_) {
        // Storage can be unavailable in private or restricted browsing modes.
    }
    return null;
}

function applyTheme(theme) {
    const root = document.documentElement;
    root.setAttribute("data-lk-theme", theme);
    root.setAttribute("data-bs-theme", theme);
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.setAttribute("content", theme === "dark" ? "#14181f" : "#425fce");
}

export function getIsDark() {
    const stored = readStoredTheme();
    const theme = stored ?? (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light");
    return theme === "dark";
}

export function setTheme(isDark) {
    const theme = isDark ? "dark" : "light";
    try {
        window.localStorage.setItem(THEME_KEY, theme);
    } catch (_) {
        // Storage can be unavailable in private or restricted browsing modes.
    }
    applyTheme(theme);
    return isDark;
}
