export function printWhenReady() {
    requestAnimationFrame(() => {
        setTimeout(() => window.print(), 150);
    });
}

export function printNow() {
    window.print();
}
