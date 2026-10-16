// One card per slot in the builder: label, type, how the value is found, and that strategy's settings.
import { describeLocation } from './slots.js';
import { T } from './strings.js';
import { autoGrow, h } from './ui.js';

function field(label, input, help) {
  return h('label', { className: 'field' }, h('span', { className: 'field-label' }, label), help ? h('span', { className: 'muted small' }, help) : null, input);
}

function select(options, value, onchange) {
  return h('select', { onchange: (e) => onchange(e.target.value) },
    ...Object.entries(options).map(([v, text]) => h('option', { value: v, selected: v === value }, text)));
}

function text(value, oninput, placeholder) {
  return h('input', { type: 'text', value: value ?? '', placeholder: placeholder ?? '', oninput: (e) => oninput(e.target.value) });
}

/** Settings of the slot's strategy, stored in slot.strategyConfig. */
function configFields(slot, slots, changed) {
  const cfg = slot.strategyConfig ?? {};
  const set = (key, value) => {
    slot.strategyConfig = { ...(slot.strategyConfig ?? {}), [key]: value };
    if (value === '') delete slot.strategyConfig[key];
    changed();
  };
  const isRows = slot.locations.some((l) => l.kind === 'rows');
  const others = Object.fromEntries(slots.filter((s) => s.id !== slot.id).map((s) => [s.id, `${s.id} · ${s.label}`]));
  switch (slot.strategy) {
    case 'constant':
      // A textarea: a constant paragraph may have several lines.
      return [field('Giá trị', autoGrow(h('textarea', { rows: 1, oninput: (e) => set('value', e.target.value) }, cfg.value ?? '')))];
    case 'formula':
      return [field('Công thức', text(cfg.expression, (v) => set('expression', v), '{s1} * 2'), `Dùng {mã ô}: ${Object.keys(others).slice(0, 6).map((k) => `{${k}}`).join(' ')}`)];
    case 'translate':
      return [
        field('Dịch sang', text(cfg.language, (v) => set('language', v), 'tiếng Hà Lan')),
        field('Dịch từ', select({ '': isRows ? 'Nhãn của dòng' : '— chọn ô —', ...others }, cfg.fromSlot ?? '', (v) => set('fromSlot', v))),
      ];
    case 'lookup':
      return [h('div', { className: 'slot-grid' },
        field('File nguồn chứa', text(cfg.source, (v) => set('source', v), 'EET'), 'Một phần tên file, để trống = mọi bảng tính'),
        field('Sheet', text(cfg.sheet, (v) => set('sheet', v), 'để trống = mọi sheet')),
        field('Cột khóa', text(cfg.keyColumn, (v) => set('keyColumn', v.toUpperCase()), 'A')),
        field('Cột giá trị', text(cfg.valueColumn, (v) => set('valueColumn', v.toUpperCase()), 'D')),
        field('Khóa lấy từ', select({ '': isRows ? 'Nhãn của dòng' : 'Giá trị cố định bên dưới', ...others }, cfg.keyFrom ?? '', (v) => set('keyFrom', v))),
        isRows ? null : field('Khóa cố định', text(cfg.key, (v) => set('key', v)))),
      ];
    default:
      return [];
  }
}

/**
 * @param {HTMLElement} root
 * @param {object[]} slots edited in place
 * @param {{paragraphs?:object[], focus?:string, onChange:()=>void, onDelete:(slot)=>void}} options
 */
export function drawSlots(root, slots, options) {
  const redraw = () => drawSlots(root, slots, options);
  const cards = slots.map((slot) => {
    const changed = () => options.onChange();
    const card = h('article', { className: `item ${slot.confidence < 0.6 ? 'review' : 'ok'}${options.focus === slot.id ? ' focus' : ''}`, id: `slot-${slot.id}` },
      h('div', { className: 'item-head' },
        h('span', { className: 'item-label' }, `${slot.id} · `, slot.label || '(chưa đặt tên)'),
        h('span', { className: 'tpl-tags' },
          slot.confidence < 0.6 ? h('span', { className: 'tag warn' }, T.lowConfidence) : null,
          h('button', { type: 'button', className: 'btn btn-danger btn-small', onclick: () => options.onDelete(slot) }, 'Xóa'))),
      h('p', { className: 'meta' }, slot.locations.map((l) => describeLocation(l, options.paragraphs)).join(' · ')),
      slot.sampleValue ? h('p', { className: 'meta' }, `Giá trị trong mẫu: ${slot.sampleValue.length > 120 ? `${slot.sampleValue.slice(0, 120)}…` : slot.sampleValue}`) : null,
      h('div', { className: 'slot-grid' },
        field('Tên', text(slot.label, (v) => { slot.label = v; changed(); })),
        field('Kiểu', select(T.valueTypes, slot.valueType, (v) => { slot.valueType = v; changed(); redraw(); })),
        field('Cách lấy', select(T.strategies, slot.strategy, (v) => { slot.strategy = v; slot.strategyConfig = null; changed(); redraw(); })),
        field('Nhóm', text(slot.group, (v) => { slot.group = v; changed(); }), 'Điền cùng nhau')),
      h('p', { className: 'muted small', style: 'margin:0' }, T.strategyHelp[slot.strategy] ?? ''),
      ...configFields(slot, slots, changed),
      ['boolean', 'enum'].includes(slot.valueType)
        ? field('Giá trị cho phép (cách nhau bằng dấu phẩy)', text((slot.options ?? []).join(', '), (v) => { slot.options = v.split(',').map((x) => x.trim()).filter(Boolean); changed(); }, 'Y, N'))
        : null,
      field('Hướng dẫn cho AI', autoGrow(h('textarea', { rows: 1, oninput: (e) => { slot.instructions = e.target.value; changed(); } }, slot.instructions ?? ''))),
      h('label', { className: 'confirm' }, h('input', { type: 'checkbox', checked: slot.required !== false, onchange: (e) => { slot.required = e.target.checked; changed(); } }), 'Bắt buộc có giá trị trước khi xuất file'));
    return card;
  });
  root.replaceChildren(...(cards.length ? cards : [h('p', { className: 'muted' }, T.noSlots)]));
  if (options.focus) document.getElementById(`slot-${options.focus}`)?.scrollIntoView({ block: 'nearest' });
}
