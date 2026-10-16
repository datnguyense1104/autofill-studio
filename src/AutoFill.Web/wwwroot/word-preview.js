// Word template preview: paragraphs drawn with their exact text (so a selection maps to the same
// offsets the server writes to), slots marked, selection → phrase or section.
import { phrasesByParagraph, sectionOf, segments, selectionToPhrase } from './slots.js';
import { h } from './ui.js';

const PART = { header: 'Đầu trang', footer: 'Chân trang' };

/**
 * @param {HTMLElement} root
 * @param {{index:number, text:string, kind:string, part:string, topLevel:boolean, cell?:string}[]} paragraphs
 * @param {object[]} slots
 * @param {{onSlotClick:(slot)=>void}} handlers
 */
export function drawWord(root, paragraphs, slots, { onSlotClick }) {
  const phrases = phrasesByParagraph(slots);
  let part = 'body';
  const blocks = [];
  for (const p of paragraphs) {
    if (p.part !== part) { part = p.part; blocks.push(h('h3', { className: 'muted small' }, PART[part] ?? '')); }
    if (p.kind === 'empty') continue;
    const section = sectionOf(slots, p.index);
    const pieces = segments(p.text, phrases.get(p.index) ?? []).map((s) => (s.slot
      ? h('mark', { 'data-slot': s.slot.id, title: s.slot.label, className: s.slot.confidence < 0.6 ? 'bad' : '', onclick: () => onSlotClick(s.slot) }, s.text)
      : s.text));
    blocks.push(h('p', {
      'data-paragraph': p.index,
      className: section ? 'in-section' : '',
      title: section ? `Phần thân: ${section.label}` : null,
      style: `white-space:pre-wrap;text-indent:0;${section ? 'border-left:4px solid var(--primary);padding-left:8px;background:var(--primary-soft)' : ''}`,
      onclick: section ? () => onSlotClick(section) : null,
    }, h('span', { className: 'badge', contenteditable: 'false', style: 'user-select:none;margin-right:6px' }, `P${p.index}${p.cell ? ` · ${p.cell}` : ''}`), h('span', { className: 'ptext' }, ...pieces)));
  }
  root.replaceChildren(h('div', { className: 'letter-body' }, ...blocks));
}

/** Character offset of (node, offset) inside a paragraph's text span. */
function offsetIn(span, node, offset) {
  const range = document.createRange();
  range.selectNodeContents(span);
  range.setEnd(node, offset);
  return range.toString().length;
}

/**
 * What the current selection in the preview can become.
 * @returns {{phrase?: {paragraph:number, start:number, length:number, text:string}, section?: {from:number, to:number}}|null}
 */
export function readSelection(root, paragraphs) {
  const sel = getSelection();
  if (!sel || sel.isCollapsed || !sel.rangeCount) return null;
  const range = sel.getRangeAt(0);
  const pOf = (node) => (node.nodeType === 1 ? node : node.parentElement)?.closest('p[data-paragraph]');
  const startP = pOf(range.startContainer);
  const endP = pOf(range.endContainer);
  if (!startP || !endP || !root.contains(startP) || !root.contains(endP)) return null;
  const from = Number(startP.dataset.paragraph);
  const to = Number(endP.dataset.paragraph);
  if (from === to) {
    const span = startP.querySelector('.ptext');
    if (!span.contains(range.startContainer) || !span.contains(range.endContainer)) return null;
    const text = paragraphs.find((p) => p.index === from)?.text ?? '';
    const phrase = selectionToPhrase(text, offsetIn(span, range.startContainer, range.startOffset), offsetIn(span, range.endContainer, range.endOffset));
    return phrase ? { phrase: { paragraph: from, ...phrase } } : null;
  }
  // Several paragraphs: a body section, if they are all body paragraphs outside tables.
  const range2 = paragraphs.filter((p) => p.index >= Math.min(from, to) && p.index <= Math.max(from, to));
  return range2.every((p) => p.topLevel) ? { section: { from: Math.min(from, to), to: Math.max(from, to) } } : null;
}
