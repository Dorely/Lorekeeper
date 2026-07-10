let initialized = false;
let rootInlineHeight = "";
let rootInlineOverflow = "";
let bodyInlineHeight = "";
let bodyInlineOverflow = "";
let printOverflowRevealed = false;

function revealPrintOverflow() {
    if (printOverflowRevealed) return;
    printOverflowRevealed = true;
    rootInlineHeight = document.documentElement.style.height;
    rootInlineOverflow = document.documentElement.style.overflow;
    bodyInlineHeight = document.body.style.height;
    bodyInlineOverflow = document.body.style.overflow;
    document.documentElement.style.height = "auto";
    document.documentElement.style.overflow = "visible";
    document.body.style.height = "auto";
    document.body.style.overflow = "visible";
}

function restoreScreenOverflow() {
    if (!printOverflowRevealed) return;
    document.documentElement.style.height = rootInlineHeight;
    document.documentElement.style.overflow = rootInlineOverflow;
    document.body.style.height = bodyInlineHeight;
    document.body.style.overflow = bodyInlineOverflow;
    printOverflowRevealed = false;
}

export function initializePrintView() {
    if (initialized) return;
    initialized = true;
    window.addEventListener("beforeprint", revealPrintOverflow);
    window.addEventListener("afterprint", restoreScreenOverflow);
}

export function disposePrintView() {
    if (!initialized) return;
    initialized = false;
    window.removeEventListener("beforeprint", revealPrintOverflow);
    window.removeEventListener("afterprint", restoreScreenOverflow);
    restoreScreenOverflow();
}

async function waitForImage(image) {
    if (!image.complete) {
        await new Promise(resolve => {
            image.addEventListener("load", resolve, { once: true });
            image.addEventListener("error", resolve, { once: true });
        });
    }

    if (typeof image.decode === "function") {
        try {
            await image.decode();
        } catch {
            // A completed image can reject decode after an error; printing should still continue.
        }
    }
}

async function waitForAssets() {
    if (document.fonts?.ready) {
        await document.fonts.ready;
    }

    await Promise.all(Array.from(document.images, waitForImage));
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
}

export async function printWhenReady() {
    await waitForAssets();
    window.print();
}

export async function printNow() {
    await waitForAssets();
    window.print();
}
