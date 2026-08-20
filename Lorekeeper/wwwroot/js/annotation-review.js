const key = projectId => `lorekeeper:review-rail:${projectId}:collapsed`;

export function readRailCollapsed(projectId) {
    const value = localStorage.getItem(key(projectId));
    return value === null ? null : value === "true";
}

export function writeRailCollapsed(projectId, collapsed) {
    localStorage.setItem(key(projectId), String(!!collapsed));
}

export function selectRailCard(rail, annotationId) {
    const card = rail?.querySelector(`[data-annotation-card-id="${CSS.escape(annotationId)}"]`);
    if (!card) return false;
    card.scrollIntoView({block: "nearest", behavior: "smooth"});
    return true;
}
