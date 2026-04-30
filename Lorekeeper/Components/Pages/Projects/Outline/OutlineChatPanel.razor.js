// Auto-scrolls a chat message container to the bottom as new content arrives,
// but only if the user hasn't scrolled up to read history. `force=true` always scrolls
// (used after the user sends a message, since they expect the view to follow).
const NEAR_BOTTOM_PX = 80;

export function scrollToBottom(element, force) {
    if (!element) return;
    if (!force) {
        const distanceFromBottom = element.scrollHeight - element.scrollTop - element.clientHeight;
        if (distanceFromBottom > NEAR_BOTTOM_PX) return;
    }
    element.scrollTop = element.scrollHeight;
}
