const STORAGE_KEY = "Lorekeeper.ui.pdfPreviewDisplay";
const MODES = ["paper", "dim", "night"];

export function getDisplayMode() {
    try {
        const stored = window.localStorage.getItem(STORAGE_KEY);
        return MODES.includes(stored) ? stored : "paper";
    } catch {
        return "paper";
    }
}

export function setDisplayMode(mode) {
    if (!MODES.includes(mode)) return;
    try {
        window.localStorage.setItem(STORAGE_KEY, mode);
    } catch {
        // Private browsing or storage denied; the choice simply won't persist.
    }
}
