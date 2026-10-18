// A short guided tour, shown once per page (remembered in this browser) and from the "?" button.
import { h } from './ui.js';

/**
 * @param {string} id remembers that this tour was seen
 * @param {{selector?: string, title: string, text: string, before?: () => void}[]} steps a step without
 *   selector is centred; `before` makes its target visible (narrow screens show one pane at a time)
 */
export function setupTour(id, steps) {
  const key = `tour:${id}`;
  const start = () => run(steps, () => { try { localStorage.setItem(key, '1'); } catch { /* private mode */ } });
  document.querySelector('#tour-start')?.addEventListener('click', start);
  let seen = false;
  try { seen = localStorage.getItem(key) === '1'; } catch { /* private mode */ }
  if (!seen) setTimeout(start, 400);
}

function run(steps, onEnd) {
  let i = 0;
  const spot = h('div', { className: 'tour-spot' });
  const box = h('div', { className: 'tour-box', role: 'dialog', 'aria-modal': 'true' });
  const layer = h('div', { className: 'tour-layer' }, spot, box);
  const end = () => { layer.remove(); document.removeEventListener('keydown', onKey); removeEventListener('resize', onResize); onEnd(); };
  const onKey = (e) => { if (e.key === 'Escape') end(); };
  const onResize = () => show(); // rotating a phone or resizing the window moves the target
  document.addEventListener('keydown', onKey);
  addEventListener('resize', onResize);

  function show() {
    const step = steps[i];
    step.before?.();
    const target = step.selector ? document.querySelector(step.selector) : null;
    layer.classList.toggle('centered', !target);
    box.replaceChildren(
      h('h2', {}, step.title),
      h('p', {}, step.text),
      h('div', { className: 'tour-foot' },
        h('span', { className: 'muted small' }, `${i + 1}/${steps.length}`),
        h('div', { className: 'actions' },
          h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: end }, 'Bỏ qua'),
          i > 0 ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => { i -= 1; show(); } }, 'Trước') : null,
          h('button', { type: 'button', className: 'btn btn-primary btn-small', onclick: () => { if (++i >= steps.length) end(); else show(); } },
            i === steps.length - 1 ? 'Xong' : 'Tiếp'))));
    if (target) {
      target.scrollIntoView({ block: 'center' });
      place(target.getBoundingClientRect());
    } else {
      Object.assign(box.style, { top: '', left: '', width: '', transform: '' });
    }
    box.querySelector('.btn-primary').focus();
  }

  /** Spotlights the target (clipped to the screen) and puts the box where it fits whole. */
  function place(r) {
    const gap = 16;
    const top = Math.max(r.top, 8), bottom = Math.min(r.bottom, innerHeight - 8);
    Object.assign(spot.style, { top: `${top - 6}px`, left: `${r.left - 6}px`, width: `${r.width + 12}px`, height: `${bottom - top + 12}px` });
    const width = Math.min(400, innerWidth - 2 * gap);
    Object.assign(box.style, { width: `${width}px`, transform: 'none', top: '0px', left: '0px' });
    const height = box.offsetHeight;
    const clampLeft = (x) => Math.max(gap, Math.min(x, innerWidth - width - gap));
    const clampTop = (y) => Math.max(gap, Math.min(y, innerHeight - height - gap));
    let at;
    if (bottom + gap + height + gap <= innerHeight) at = { top: bottom + gap, left: clampLeft(r.left) };
    else if (top - gap - height >= gap) at = { top: top - gap - height, left: clampLeft(r.left) };
    else if (r.right + gap + width + gap <= innerWidth) at = { top: clampTop(top), left: r.right + gap };
    else if (r.left - gap - width >= gap) at = { top: clampTop(top), left: r.left - gap - width };
    else at = { top: innerHeight - height - gap, left: clampLeft(r.left) }; // over the target, at the screen's bottom edge
    Object.assign(box.style, { top: `${at.top}px`, left: `${at.left}px` });
  }
  document.body.append(layer);
  show();
}
