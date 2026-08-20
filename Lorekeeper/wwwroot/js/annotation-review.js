const key = projectId => `lorekeeper:review-rail:${projectId}:collapsed`;
const railObservers = new WeakMap();

export function readRailCollapsed(projectId) {
    const value = localStorage.getItem(key(projectId));
    return value === null ? null : value === "true";
}

export function writeRailCollapsed(projectId, collapsed) {
    localStorage.setItem(key(projectId), String(!!collapsed));
}

export function positionRailCards(rail) {
    const body = rail?.querySelector(".annotation-rail-body");
    if (!body) return;
    const workspace = rail.closest(".annotation-workspace") || document;
    let occupiedBottom = 0;
    for (const card of body.querySelectorAll(".annotation-card:not(.annotation-card--outdated)")) {
        card.style.marginTop = "0px";
        const target = [...workspace.querySelectorAll(`[data-annotation-id="${CSS.escape(card.dataset.annotationCardId)}"]`)]
            .find(candidate => candidate.offsetParent !== null);
        if (!target) continue;
        const desired = Math.max(0, target.getBoundingClientRect().top - body.getBoundingClientRect().top + body.scrollTop);
        const top = Math.max(desired, occupiedBottom);
        card.style.marginTop = `${Math.max(0, top - occupiedBottom)}px`;
        occupiedBottom = top + card.getBoundingClientRect().height + 8;
    }
}

export function startRailPositioning(rail) {
    if (!rail || railObservers.has(rail)) return;
    const workspace = rail.closest(".annotation-workspace");
    if (!workspace) return;
    let frame = null;
    const schedule = () => {
        if (frame !== null) cancelAnimationFrame(frame);
        frame = requestAnimationFrame(() => {
            frame = null;
            positionRailCards(rail);
        });
    };
    workspace.addEventListener("scroll", schedule, true);
    window.addEventListener("resize", schedule);
    const resizeObserver = new ResizeObserver(schedule);
    resizeObserver.observe(workspace);
    railObservers.set(rail, {workspace, schedule, resizeObserver, get frame() { return frame; }});
    schedule();
}

export function stopRailPositioning(rail) {
    const observer = railObservers.get(rail);
    if (!observer) return;
    observer.workspace.removeEventListener("scroll", observer.schedule, true);
    window.removeEventListener("resize", observer.schedule);
    observer.resizeObserver.disconnect();
    if (observer.frame !== null) cancelAnimationFrame(observer.frame);
    railObservers.delete(rail);
}

export function selectRailCard(rail, annotationId) {
    const card = rail?.querySelector(`[data-annotation-card-id="${CSS.escape(annotationId)}"]`);
    if (!card) return false;
    card.scrollIntoView({block: "nearest", behavior: "smooth"});
    return true;
}
