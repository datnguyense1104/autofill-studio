// A short guided tour, shown once per page (remembered in this browser) and from the "?" button.
import { h } from './ui.js';

/**
 * @param {string} id remembers that this tour was seen
 * @param {{selector?: string, title: string, text: string}[]} steps a step without selector is centred
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
  const end = () => { layer.remove(); document.removeEventListener('keydown', onKey); onEnd(); };
  const onKey = (e) => { if (e.key === 'Escape') end(); };
  document.addEventListener('keydown', onKey);

  function show() {
    const step = steps[i];
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
      const r = target.getBoundingClientRect();
      Object.assign(spot.style, { top: `${r.top - 6}px`, left: `${r.left - 6}px`, width: `${r.width + 12}px`, height: `${r.height + 12}px` });
      const below = r.bottom + 16 + 180 < innerHeight;
      Object.assign(box.style, {
        top: below ? `${r.bottom + 16}px` : 'auto', bottom: below ? 'auto' : `${innerHeight - r.top + 16}px`,
        left: `${Math.max(16, Math.min(r.left, innerWidth - 420))}px`, width: 'min(400px, calc(100vw - 32px))', transform: 'none',
      });
    } else {
      Object.assign(box.style, { top: '', bottom: '', left: '', width: '', transform: '' });
    }
    box.querySelector('.btn-primary').focus();
  }
  document.body.append(layer);
  show();
}
