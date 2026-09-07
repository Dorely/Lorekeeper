export function defaultPaper() {
    const language = (navigator.language || '').toUpperCase();
    return language.endsWith('-CA') || language === 'EN-US' || language.startsWith('EN-US-') ? 'Letter' : 'A4';
}

export async function readImage(url, maxBytes = 64 * 1024 * 1024) {
    const parsed = new URL(url, window.location.href);
    if (parsed.origin !== window.location.origin || (!parsed.pathname.startsWith('/projects/'))) throw new Error('Print image must be a local project resource.');
    parsed.searchParams.delete('maxEdge');
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), 60000);
    let response;
    try { response = await fetch(parsed, { credentials: 'same-origin', signal: controller.signal }); }
    catch (error) { clearTimeout(timeout); throw error; }
    if (!response.ok) { clearTimeout(timeout); throw new Error(`Unable to read print image (${response.status}).`); }
    if (!response.headers.get('content-type')?.toLowerCase().startsWith('image/')) { clearTimeout(timeout); throw new Error('Print source is not an image.'); }
    const length = Number(response.headers.get('content-length') || 0);
    if (length > maxBytes) { clearTimeout(timeout); throw new Error('Print image exceeds the permitted size.'); }
    if (!response.body) { clearTimeout(timeout); throw new Error('Print image response has no body.'); }
    const reader = response.body.getReader(), chunks = []; let total = 0;
    try {
        for (;;) { const part = await reader.read(); if (part.done) break; total += part.value.byteLength; if (total > maxBytes) throw new Error('Print image exceeds the permitted size.'); chunks.push(part.value); }
    } finally { await reader.cancel().catch(() => {}); reader.releaseLock(); clearTimeout(timeout); }
    // IJSStreamReference interop creates the stream reference from this Blob.
    return new Blob(chunks, { type: response.headers.get('content-type') });
}

let activePrintFrame;
let cancelActivePrint;
export async function printDocument(url) {
    cancelPrintDocument();
    const parsed = new URL(url, window.location.href);
    if (parsed.origin !== window.location.origin || !/^\/projects\/[0-9a-f-]+\/print-sessions\/[0-9a-f]+\/document$/i.test(parsed.pathname)) throw new Error('Print document must be local.');
    const frame = document.createElement('iframe');
    frame.style.cssText = 'position:fixed;left:-10000px;top:0;width:800px;height:1000px;border:0';
    frame.setAttribute('aria-hidden', 'true');
    frame.title = 'Print document';
    activePrintFrame = frame;
    try {
        await new Promise((resolve, reject) => {
            let settled = false;
            const timeout = setTimeout(() => done(new Error('The system print dialog did not respond. Close it and try again.')), 600000);
            cancelActivePrint = () => done(new DOMException('Print cancelled.', 'AbortError'));
            const done = (error) => {
                if (settled) return;
                settled = true;
                clearTimeout(timeout);
                frame.removeEventListener('load', loaded);
                frame.contentWindow?.removeEventListener('afterprint', printed);
                error ? reject(error) : resolve();
            };
            const printed = () => done();
            const loaded = async () => {
                try {
                    const content = frame.contentWindow;
                    if (!content?.lorekeeperPrintReady) throw new Error('The print preview expired. Change a setting to prepare it again.');
                    await content.lorekeeperPrintReady;
                    if (settled) return;
                    if (content.lorekeeperPrintError) throw new Error('The print sheets could not be loaded.');
                    content.addEventListener('afterprint', printed, { once: true });
                    content.focus();
                    content.print();
                }
                catch (error) { done(error); }
            };
            frame.addEventListener('load', loaded, { once: true });
            frame.src = parsed.href;
            document.body.appendChild(frame);
        });
    } finally { if (activePrintFrame === frame) { frame.remove(); activePrintFrame = undefined; cancelActivePrint = undefined; } }
}

export function cancelPrintDocument() {
    cancelActivePrint?.(); cancelActivePrint = undefined;
    if (activePrintFrame) { activePrintFrame.remove(); activePrintFrame = undefined; }
}

let previousFocus;
export function trapFocus(element) {
    if (!element) return;
    previousFocus = document.activeElement;
    element.dataset.printFocus = 'true';
    element._printFocusHandler = event => {
        event.stopPropagation();
        if (event.key === 'Escape') { event.preventDefault(); element.querySelector('.btn-close')?.click(); return; }
        if (event.key !== 'Tab') return;
        const items = [...element.querySelectorAll('button:not([disabled]),input:not([disabled]),select:not([disabled]),textarea:not([disabled]),[href]')];
        if (!items.length) return;
        const first = items[0], last = items[items.length - 1];
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    };
    element.addEventListener('keydown', element._printFocusHandler);
    element.querySelector('.btn-close')?.focus();
}

export function releaseFocus(element) {
    if (element?._printFocusHandler) element.removeEventListener('keydown', element._printFocusHandler);
    if (previousFocus?.isConnected) previousFocus.focus();
    previousFocus = undefined;
}
