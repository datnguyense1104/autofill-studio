// Excel template preview: one sheet at a time as a grid; slot cells coloured; a click picks a cell.
import { colToIndex, indexToCol, parseAddress } from './slots.js';
import { h } from './ui.js';

const MAX_ROWS = 1200;
const MAX_COLS = 30;

/** sheet → address → slot, for cell slots and every row of row rules */
function slotCells(slots) {
  const map = new Map();
  const put = (sheet, address, slot, rule) => {
    if (!map.has(sheet)) map.set(sheet, new Map());
    map.get(sheet).set(address, { slot, rule });
  };
  for (const slot of slots) {
    for (const loc of slot.locations ?? []) {
      if (loc.kind === 'cell') put(loc.sheet, loc.cell, slot, false);
      if (loc.kind === 'rows') for (const r of loc.rows) put(loc.sheet, `${loc.column}${r}`, slot, true);
    }
  }
  return map;
}

/**
 * @param {HTMLElement} root
 * @param {{name:string, cells:{address:string, text:string, hasFormula:boolean}[]}[]} sheets
 * @param {{sheet?:string, selected?:string, onPick:(pick:{sheet:string, address:string, col:string, row:number})=>void, onSlotClick:(slot)=>void}} options
 */
export function drawExcel(root, sheets, slots, options) {
  const current = sheets.find((s) => s.name === options.sheet) ?? sheets[0];
  if (!current) { root.replaceChildren(h('p', { className: 'muted' }, 'File Excel không có sheet nào.')); return; }
  const marks = slotCells(slots).get(current.name) ?? new Map();
  const byAddress = new Map(current.cells.map((c) => [c.address, c]));
  let maxRow = 1;
  let maxCol = 1;
  for (const c of current.cells) {
    const a = parseAddress(c.address);
    if (a) { maxRow = Math.max(maxRow, a.row); maxCol = Math.max(maxCol, colToIndex(a.col)); }
  }
  for (const address of marks.keys()) {
    const a = parseAddress(address);
    if (a) { maxRow = Math.max(maxRow, a.row); maxCol = Math.max(maxCol, colToIndex(a.col)); }
  }
  maxRow = Math.min(maxRow + 2, MAX_ROWS);
  maxCol = Math.min(maxCol + 1, MAX_COLS);

  const tabs = sheets.length > 1 ? h('div', { className: 'filters' }, ...sheets.map((s) => h('button', {
    type: 'button', className: 'btn btn-ghost btn-small', 'aria-pressed': String(s.name === current.name),
    onclick: () => drawExcel(root, sheets, slots, { ...options, sheet: s.name, selected: null }),
  }, s.name))) : null;

  const head = h('tr', {}, h('th', {}), ...Array.from({ length: maxCol }, (_, i) => h('th', {}, indexToCol(i + 1))));
  const rows = [];
  for (let r = 1; r <= maxRow; r += 1) {
    const cells = [h('th', {}, String(r))];
    for (let c = 1; c <= maxCol; c += 1) {
      const address = `${indexToCol(c)}${r}`;
      const cell = byAddress.get(address);
      const mark = marks.get(address);
      const text = cell ? (cell.hasFormula ? `ƒ ${cell.text}` : cell.text) : '';
      cells.push(h('td', {
        title: mark ? `${mark.slot.label} (${address})` : address,
        className: [mark ? (mark.rule ? 'slot-rule' : 'slot-cell') : '', options.selected === address ? 'picked' : '', cell?.hasFormula ? 'formula' : ''].join(' ').trim(),
        onclick: () => {
          if (mark) options.onSlotClick(mark.slot);
          options.onPick({ sheet: current.name, address, col: indexToCol(c), row: r });
          drawExcel(root, sheets, slots, { ...options, sheet: current.name, selected: address });
        },
      }, text.length > 40 ? `${text.slice(0, 40)}…` : text));
    }
    rows.push(h('tr', {}, ...cells));
  }
  root.replaceChildren(...[tabs, h('div', { className: 'table-wrap grid-wrap' }, h('table', { className: 'grid' }, h('thead', {}, head), h('tbody', {}, ...rows)))].filter(Boolean));
}
