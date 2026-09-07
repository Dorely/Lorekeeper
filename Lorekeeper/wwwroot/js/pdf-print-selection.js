const observers = new WeakMap();

export function observe(root, reference) {
  const scroll = root.querySelector('.publication-pdf-preview-scroll');
  const existing = observers.get(root);
  if (existing?.scroll === scroll) return;
  dispose(root);
  if (!scroll) return;
  let frame = 0;
  let selected = Number(root.querySelector('.print-current')?.dataset.pageNumber || 1);
  let stopped = false;
  const select = page => {
    if (!page || page === selected || stopped) return;
    selected = page;
    reference.invokeMethodAsync('SetCurrentPage', page).catch(() => {});
  };
  const nearest = () => {
    frame = 0;
    const box = scroll.getBoundingClientRect();
    const center = (box.top + box.bottom) / 2;
    let closest = null;
    let distance = Infinity;
    for (const page of scroll.querySelectorAll('[data-page-number]')) {
      const rect = page.getBoundingClientRect();
      if (rect.bottom <= box.top || rect.top >= box.bottom) continue;
      const next = Math.abs((rect.top + rect.bottom) / 2 - center);
      const number = Number(page.dataset.pageNumber);
      if (next < distance - 1 || (Math.abs(next - distance) <= 1 && number === selected)) {
        closest = number;
        distance = next;
      }
    }
    select(closest);
  };
  const schedule = () => { if (!frame) frame = requestAnimationFrame(nearest); };
  const click = event => {
    const page = event.target.closest('[data-page-number]');
    if (page && scroll.contains(page)) select(Number(page.dataset.pageNumber));
  };
  const key = event => {
    if (event.key === 'Enter' || event.key === ' ') {
      if (event.target.closest('[data-page-number]')) {
        event.preventDefault();
        click(event);
      }
    }
  };
  const resize = new ResizeObserver(schedule);
  resize.observe(scroll);
  scroll.addEventListener('scroll', schedule, { passive: true });
  scroll.addEventListener('click', click);
  scroll.addEventListener('keydown', key);
  scroll.addEventListener('load', schedule, true);
  observers.set(root, { scroll, dispose() {
    stopped = true;
    cancelAnimationFrame(frame);
    resize.disconnect();
    scroll.removeEventListener('scroll', schedule);
    scroll.removeEventListener('click', click);
    scroll.removeEventListener('keydown', key);
    scroll.removeEventListener('load', schedule, true);
  }});
  schedule();
}

export function dispose(root) {
  observers.get(root)?.dispose();
  observers.delete(root);
}
