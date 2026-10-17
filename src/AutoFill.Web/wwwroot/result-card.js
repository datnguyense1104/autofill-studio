// One result card per run, as in the letter app: what was filled, only the places that need a
// look (each inside its sentence), and the download at the bottom. Used by the main page right
// after "Xử lý" and by run.html for a run from the history.
import { contextOf, reviewStatus } from './slots.js';
import { T } from './strings.js';
import { api, autoGrow, download, h, postJson, putJson, toast } from './ui.js';

const STATUS_TEXT = { ok: '✓ Khớp', review: '⚠ Cần xem', blocked: '✖ Cần sửa' };

/**
 * @param {HTMLElement} el the card element (its content is replaced)
 * @param {string} runId a finished run (status NeedsReview or Done)
 * @param {{reason?: string, recipes?: object[], onRefill?: (recipeId: string) => void}} options
 *   reason: why this recipe was chosen; recipes + onRefill: switch recipe and fill again
 */
export async function renderRunCard(el, runId, options = {}) {
  el.className = 'card loading';
  const run = await api(`api/runs/${runId}`);
  if (run.status === 'failed') {
    el.className = 'card';
    el.replaceChildren(h('div', { className: 'card-head' }, h('h2', {}, run.recipe.name), h('p', { className: 'reason' }, `Không điền được: ${run.error}`)));
    return;
  }
  const outline = await api(`api/templates/${run.recipe.templateFileId}/outline`);
  const rows = new Map(run.values.map((v) => [v.slotId, v]));
  const card = { el, run, outline, rows, options, open: new Set() };
  el.className = 'card';
  draw(card);
}

const finalOf = (row) => row?.reviewedValue ?? row?.value ?? { text: '' };
const isWord = (card) => card.run.recipe.kind !== 'excel';

function rowLabel(card, loc, row) {
  return card.outline.sheets?.find((s) => s.name === loc.sheet)?.cells.find((c) => c.address === `${loc.labelColumn}${row}`)?.text ?? `Dòng ${row}`;
}

/** Saves the reviewer's value (or "Đúng rồi"), then redraws the card. */
async function save(card, slot, value, reviewed = true) {
  try {
    card.rows.set(slot.id, await putJson(`api/runs/${card.run.id}/values/${slot.id}`, { value, reviewed }));
    card.open.add(slot.id); // a fixed place stays visible instead of jumping into "đã khớp"
    draw(card);
  } catch (err) {
    toast(err.message, true);
  }
}

async function revise(card, slot, instruction) {
  card.el.classList.add('loading');
  try {
    card.rows.set(slot.id, await postJson(`api/runs/${card.run.id}/values/${slot.id}/revise`, { instruction }));
    card.open.add(slot.id);
    draw(card);
    return true;
  } catch (err) {
    toast(err.message, true);
    return false;
  } finally {
    card.el.classList.remove('loading');
  }
}

function contextNode(card, slot) {
  if (!isWord(card)) {
    const loc = slot.locations[0];
    return loc?.kind === 'cell' ? h('p', { className: 'meta' }, `Ô ${loc.sheet}!${loc.cell}`) : null;
  }
  const values = Object.fromEntries(card.run.slots.map((s) => [s.id, finalOf(card.rows.get(s.id))]));
  const pieces = contextOf(card.outline.paragraphs ?? [], card.run.slots, values, slot.id);
  if (!pieces) return null;
  return h('p', { className: 'context' }, ...pieces.map((p) => (p.current ? h('mark', {}, p.text || '…') : p.text)));
}

function editor(card, slot) {
  const value = finalOf(card.rows.get(slot.id));
  const rowsLoc = slot.locations.find((l) => l.kind === 'rows');
  if (rowsLoc) {
    const rows = { ...(value.rows ?? {}) };
    return h('div', { className: 'table-wrap', style: 'max-height:320px;overflow:auto' }, h('table', { className: 'rows-table' }, h('tbody', {}, ...rowsLoc.rows.map((r) => h('tr', {},
      h('td', {}, rowLabel(card, rowsLoc, r)),
      h('td', {}, h('input', { type: 'text', value: rows[r] ?? '', 'aria-label': `${slot.label} dòng ${r}`, onchange: (e) => { rows[r] = e.target.value; save(card, slot, { rows }); } })))))));
  }
  return autoGrow(h('textarea', { rows: 1, 'aria-label': slot.label, onchange: (e) => save(card, slot, { text: e.target.value }) }, value.text ?? ''));
}

function evidence(card, row) {
  const found = (row?.evidence ?? []).filter((e) => e.quote);
  if (!found.length) return null;
  return h('p', { className: 'meta' }, 'Căn cứ trong tài liệu nguồn: ', ...found.flatMap((e, i) => [
    i ? ' · ' : '',
    h('button', { type: 'button', className: 'link-button', title: `${card.run.sources[e.source]?.name ?? ''} · trang ${e.page}`, onclick: () => openPage(card.run.sources[e.source], e) },
      `“${e.quote}”${e.found ? '' : ' (không thấy trong nguồn)'}`),
  ]));
}

function item(card, slot) {
  const row = card.rows.get(slot.id);
  const status = reviewStatus(slot, row);
  const raised = (row?.warnings?.length ?? 0) > 0;
  const resolved = status === 'ok' && Boolean(raised || row?.reviewedValue);
  const cls = status !== 'ok' ? status : resolved ? 'review resolved' : 'ok';
  return h('div', { className: `item ${cls}` },
    h('div', { className: 'item-head' },
      h('span', { className: 'item-label' }, slot.label, slot.locations.length > 1 ? h('span', { className: 'muted' }, ` · dùng ở ${slot.locations.length} chỗ`) : null),
      h('span', { className: `status ${status}` }, resolved ? '✓ Đã xem' : STATUS_TEXT[status])),
    contextNode(card, slot),
    editor(card, slot),
    raised ? h('ul', { className: 'reasons' }, ...row.warnings.map((w) => h('li', {}, w))) : null,
    evidence(card, row),
    slot.sampleValue ? h('p', { className: 'meta' }, 'Trong mẫu cũ: ', slot.sampleValue.length > 200 ? `${slot.sampleValue.slice(0, 200)}…` : slot.sampleValue) : null,
    h('div', { className: 'item-actions' },
      status === 'review' ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => save(card, slot, row?.reviewedValue ?? null, true) }, 'Đúng rồi') : null,
      h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => { const ask = prompt(T.reviseAsk); if (ask?.trim()) revise(card, slot, ask.trim()); } }, 'AI viết lại'),
      row?.reviewedValue ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => save(card, slot, null, false) }, 'Dùng lại giá trị AI') : null));
}

function missingNotice(card) {
  const items = (card.run.missing ?? []).map((m, i) => ({ ...m, i })).filter((m) => !m.decision);
  if (!items.length) return null;
  const targets = card.run.slots.filter((s) => s.locations.every((l) => l.kind !== 'rows') && ['extract', 'generate'].includes(s.strategy));
  return h('div', { className: 'notice' },
    h('p', {}, h('strong', {}, 'Có thể còn thiếu: '), 'thông tin quan trọng trong tài liệu nguồn chưa có trong văn bản.'),
    h('ul', {}, ...items.map((m) => {
      const pick = h('select', { 'aria-label': T.missingAsk }, ...targets.map((s) => h('option', { value: s.id }, s.label)));
      const decide = async (decision) => {
        await postJson(`api/runs/${card.run.id}/missing/${m.i}`, { decision });
        card.run.missing[m.i].decision = decision;
        draw(card);
      };
      return h('li', {},
        m.item, h('span', { className: 'muted' }, ` — “${m.quote}”`),
        h('div', { className: 'item-actions' },
          targets.length ? pick : null,
          targets.length ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: async () => {
            const slot = card.run.slots.find((s) => s.id === pick.value);
            if (await revise(card, slot, `Bổ sung thông tin còn thiếu: ${m.item} (nguồn ghi: «${m.quote}»)`)) await decide('added');
          } }, 'Bổ sung') : null,
          h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => decide('ignored') }, 'Không cần')));
    })));
}

function draw(card) {
  const { run, options } = card;
  const statuses = new Map(run.slots.map((s) => [s.id, reviewStatus(s, card.rows.get(s.id))]));
  const count = (s) => [...statuses.values()].filter((x) => x === s).length;
  const attention = run.slots.filter((s) => statuses.get(s.id) !== 'ok' || card.open.has(s.id))
    .sort((a, b) => (statuses.get(a.id) === 'blocked' ? -1 : 0) - (statuses.get(b.id) === 'blocked' ? -1 : 0));
  const okSlots = run.slots.filter((s) => statuses.get(s.id) === 'ok' && !card.open.has(s.id));
  const blocked = count('blocked');
  const pending = count('review');
  const ext = isWord(card) ? 'Word' : 'Excel';

  const recipeSelect = options.recipes?.length > 1
    ? h('select', { 'aria-label': 'Công thức' }, ...options.recipes.map((r) => h('option', { value: r.id, selected: r.id === run.recipe.id }, r.name)))
    : null;
  const fileName = h('input', { type: 'text', className: 'filename', 'aria-label': 'Tên file tải về', value: `${run.recipe.name} - ${new Date().toLocaleDateString('vi-VN').replaceAll('/', '-')}` });
  const highlight = h('input', { type: 'checkbox', checked: true });
  const button = h('button', { type: 'button', className: 'btn btn-primary', disabled: blocked > 0 },
    blocked ? `Còn ${blocked} chỗ cần sửa` : pending ? `Tải file ${ext} (còn ${pending} chỗ chưa xem)` : `Tải file ${ext}`);
  button.addEventListener('click', async () => {
    button.disabled = true;
    try {
      const query = new URLSearchParams({ fileName: fileName.value, highlight: String(highlight.checked) });
      await download(`api/runs/${run.id}/export?${query}`, fileName.value);
    } catch (err) {
      toast(err.message, true);
    } finally {
      button.disabled = blocked > 0;
    }
  });

  card.el.replaceChildren(
    h('div', { className: 'card-head' },
      h('h2', {}, run.recipe.name),
      options.reason ? h('p', { className: 'reason' }, options.reason) : null,
      recipeSelect && options.onRefill ? h('div', { className: 'inline-form' },
        h('label', { className: 'muted' }, 'Công thức:'), recipeSelect,
        h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => options.onRefill(recipeSelect.value) }, 'Điền lại')) : null,
      h('p', { className: 'meta' }, `Dựa trên: ${run.sources.map((s) => s.name).join(', ')}`)),
    h('div', { className: 'card-body' },
      ...(run.notes ?? []).map((n) => h('div', { className: 'notice' }, h('p', {}, n))),
      missingNotice(card),
      h('div', { className: 'summary', role: 'status' },
        h('span', { className: 'pill ok' }, `✓ ${count('ok')} khớp`),
        h('span', { className: 'pill review' }, `⚠ ${pending} cần xem`),
        h('span', { className: 'pill blocked' }, `✖ ${blocked} cần sửa`)),
      attention.length
        ? h('div', { className: 'items' }, ...attention.map((s) => item(card, s)))
        : h('div', { className: 'notice info' }, h('p', {}, 'Tất cả các chỗ đều khớp tài liệu nguồn. Có thể tải file ngay.')),
      okSlots.length ? h('details', { className: 'ok-list' },
        h('summary', {}, `Xem ${okSlots.length} chỗ đã khớp`),
        h('div', { className: 'items' }, ...okSlots.map((s) => item(card, s)))) : null),
    h('div', { className: 'card-foot' },
      fileName,
      h('label', { className: 'confirm' }, highlight, 'Tô nền vàng chỗ đã điền'),
      button));
}

// ---------------------------------------------------------------- source page

let dialog = null;

/** The source page an evidence quote comes from, with the quote highlighted. */
async function openPage(source, ev) {
  if (!source) return;
  if (!dialog) {
    dialog = h('dialog', { className: 'modal' },
      h('div', { className: 'modal-head' }, h('h2', {}), h('button', { type: 'button', className: 'btn btn-ghost btn-small', 'aria-label': 'Đóng', onclick: () => dialog.close() }, '✕')),
      h('div', { className: 'modal-body' }, h('div', { className: 'page-text' })));
    document.body.append(dialog);
  }
  const text = dialog.querySelector('.page-text');
  dialog.querySelector('h2').textContent = `${source.name} · trang ${ev.page}`;
  text.textContent = 'Đang tải…';
  dialog.showModal();
  try {
    const data = await api(`api/sources/${source.fileId}`);
    const page = data.document.pages[ev.page - 1];
    const body = (page?.blocks ?? []).map((b) => b.text ?? (b.rows ?? []).map((r) => r.map((c) => (c.ref ? `${c.ref}: ${c.text}` : c.text)).join(' | ')).join('\n')).join('\n');
    const at = body.toLocaleLowerCase('vi').indexOf(ev.quote.toLocaleLowerCase('vi'));
    text.replaceChildren(...(at >= 0 ? [body.slice(0, at), h('mark', {}, body.slice(at, at + ev.quote.length)), body.slice(at + ev.quote.length)] : [body || '(trang trống)']));
    text.querySelector('mark')?.scrollIntoView({ block: 'center' });
  } catch (err) {
    text.textContent = err.message;
  }
}
