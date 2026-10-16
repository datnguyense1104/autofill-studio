// Review screen: follow the run, then check every value against its evidence, fix, and export.
import { followJob, progressText } from './jobs.js';
import { applyValues, formatElapsed, isEmptyValue } from './slots.js';
import { T } from './strings.js';
import { setupTour } from './tour.js';
import { $, api, autoGrow, download, formatDate, h, postJson, putJson, toast } from './ui.js';

const runId = new URLSearchParams(location.search).get('id');
let run = null;
let outline = null;
const values = new Map(); // slotId → RunValue view

const finalOf = (v) => v?.reviewedValue ?? v?.value ?? { text: '' };
const isBlocked = (slot) => slot.required !== false && isEmptyValue(finalOf(values.get(slot.id)));
const needsLook = (slot) => {
  const v = values.get(slot.id);
  return (v?.warnings?.length ?? 0) > 0 && !v.reviewed;
};

// ---------------------------------------------------------------- progress

async function follow() {
  $('#progress').hidden = false;
  const started = Date.parse(run.createdAt);
  const steps = [];
  const timer = setInterval(() => drawSteps(), 1000);
  function drawSteps() {
    $('#steps').replaceChildren(...steps.map((s, i) => h('li', { className: i === steps.length - 1 ? 'running' : 'done' },
      h('span', { className: 'icon' }, i === steps.length - 1 ? '●' : '✓'), h('span', {}, s),
      i === steps.length - 1 ? h('span', { className: 'timer' }, formatElapsed((Date.now() - started) / 1000)) : null)));
  }
  try {
    await followJob(run.jobId, (job) => {
      const text = progressText(job);
      const base = text.replace(/ \d+\/\d+$/, '');
      if (steps.length && steps[steps.length - 1].replace(/ \d+\/\d+$/, '') === base) steps[steps.length - 1] = text;
      else steps.push(text);
      drawSteps();
    });
  } catch {
    // The run view says why it failed.
  } finally {
    clearInterval(timer);
  }
  $('#progress').hidden = true;
  await load();
}

// ---------------------------------------------------------------- preview

function drawPreview() {
  const focus = (slot) => document.getElementById(`value-${slot.id}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' });
  if (outline?.kind === 'word') {
    const finals = Object.fromEntries(run.slots.map((s) => [s.id, finalOf(values.get(s.id))]));
    const blocks = applyValues(outline.paragraphs.filter((p) => p.kind !== 'empty'), run.slots, finals);
    $('#preview').replaceChildren(h('div', { className: 'letter-body' }, ...blocks.map((b) => h('p', { style: 'white-space:pre-wrap;text-indent:0' },
      ...b.pieces.map((x) => (x.slot
        ? h('mark', { className: isBlocked(x.slot) || needsLook(x.slot) ? 'bad' : '', title: x.slot.label, onclick: () => focus(x.slot) }, x.text || `[${x.slot.label}]`)
        : x.text))))));
    return;
  }
  // Excel: what goes into which cell.
  const rows = [];
  for (const slot of run.slots) {
    const v = finalOf(values.get(slot.id));
    for (const loc of slot.locations) {
      if (loc.kind === 'cell') rows.push([`${loc.sheet}!${loc.cell}`, slot, v.text ?? '']);
      if (loc.kind === 'rows') for (const r of loc.rows) rows.push([`${loc.sheet}!${loc.column}${r}`, slot, v.rows?.[r] ?? '']);
    }
  }
  $('#preview').replaceChildren(h('div', { className: 'table-wrap grid-wrap' }, h('table', { className: 'grid' },
    h('thead', {}, h('tr', {}, h('th', {}, 'Ô'), h('th', {}, 'Chỗ cần điền'), h('th', {}, 'Giá trị'))),
    h('tbody', {}, ...rows.slice(0, 2000).map(([address, slot, text]) => h('tr', { onclick: () => focus(slot) },
      h('th', {}, address), h('td', {}, slot.label), h('td', { className: isBlocked(slot) || needsLook(slot) ? 'warn' : 'slot-cell', title: text }, text)))))));
}

// ---------------------------------------------------------------- values

function rowLabel(loc, row) {
  return outline?.sheets?.find((s) => s.name === loc.sheet)?.cells.find((c) => c.address === `${loc.labelColumn}${row}`)?.text ?? `Dòng ${row}`;
}

async function saveValue(slot, value, reviewed = true) {
  try {
    values.set(slot.id, await putJson(`api/runs/${runId}/values/${slot.id}`, { value, reviewed }));
    drawAll();
  } catch (err) {
    toast(err.message, true);
  }
}

async function revise(slot, instruction) {
  const card = document.getElementById(`value-${slot.id}`);
  card?.classList.add('loading');
  try {
    values.set(slot.id, await postJson(`api/runs/${runId}/values/${slot.id}/revise`, { instruction }));
    drawAll();
    toast(`AI đã viết lại "${slot.label}"`);
    return true;
  } catch (err) {
    toast(err.message, true);
    return false;
  } finally {
    card?.classList.remove('loading');
  }
}

function valueEditor(slot) {
  const v = finalOf(values.get(slot.id));
  const rowsLoc = slot.locations.find((l) => l.kind === 'rows');
  if (rowsLoc) {
    const rows = { ...(v.rows ?? {}) };
    return h('table', { className: 'rows-table' }, h('tbody', {}, ...rowsLoc.rows.map((r) => h('tr', {},
      h('td', {}, rowLabel(rowsLoc, r)),
      h('td', {}, h('input', {
        type: 'text', value: rows[r] ?? '', 'aria-label': `${slot.label} dòng ${r}`,
        onchange: (e) => { rows[r] = e.target.value; saveValue(slot, { rows }); },
      }))))));
  }
  return autoGrow(h('textarea', { rows: 1, 'aria-label': slot.label, onchange: (e) => saveValue(slot, { text: e.target.value }) }, v.text ?? ''));
}

function evidenceList(v) {
  if (!v?.evidence?.length) return null;
  return h('ul', { className: 'evidence' }, ...v.evidence.map((e) => {
    const source = run.sources[e.source];
    return h('li', {}, h('button', { type: 'button', className: e.found ? '' : 'lost', onclick: () => openPage(source, e) },
      `${source?.name ?? '?'} · trang ${e.page}: «${e.quote}»${e.found ? '' : ' (không tìm thấy trong nguồn)'}`));
  }));
}

function valueCard(slot) {
  const v = values.get(slot.id);
  const blocked = isBlocked(slot);
  const look = needsLook(slot);
  const state = blocked ? 'blocked' : look ? 'review' : 'ok';
  const statusText = blocked ? 'Thiếu giá trị' : look ? 'Cần xem lại' : v?.reviewed ? 'Đã xem' : 'Ổn';
  return h('article', { className: `item ${state}`, id: `value-${slot.id}` },
    h('div', { className: 'item-head' },
      h('span', { className: 'item-label' }, slot.label),
      h('span', { className: `status ${state}` }, statusText)),
    valueEditor(slot),
    v?.warnings?.length ? h('ul', { className: 'reasons' }, ...v.warnings.map((w) => h('li', {}, w))) : null,
    evidenceList(v),
    h('p', { className: 'meta' }, [
      v ? `Nguồn: ${v.origin === 'ai' ? 'AI' : v.origin === 'rule' ? 'luật/tra bảng' : 'không có'}` : '',
      v?.origin === 'ai' ? `độ tin cậy ${Math.round((v.confidence ?? 0) * 100)}%` : '',
      v?.note ?? '',
      v?.reviewedValue ? 'đã sửa tay' : '',
    ].filter(Boolean).join(' · ')),
    h('div', { className: 'item-actions' },
      h('label', { className: 'confirm' }, h('input', { type: 'checkbox', checked: Boolean(v?.reviewed), onchange: (e) => saveValue(slot, v?.reviewedValue ?? null, e.target.checked) }), 'Đã xem'),
      h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => { const ask = prompt(T.reviseAsk); if (ask?.trim()) revise(slot, ask.trim()); } }, 'AI viết lại'),
      v?.reviewedValue ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => saveValue(slot, null, false) }, 'Dùng lại giá trị AI') : null));
}

function drawMissing() {
  const items = (run.missing ?? []).map((m, i) => ({ ...m, i })).filter((m) => !m.decision);
  if (!items.length) { $('#missing').replaceChildren(); return; }
  const targets = run.slots.filter((s) => s.locations.every((l) => l.kind !== 'rows') && ['extract', 'generate'].includes(s.strategy));
  $('#missing').replaceChildren(h('div', { className: 'notice', style: 'margin-bottom:12px' },
    h('p', {}, h('strong', {}, 'Có thể còn thiếu'), ' — thông tin quan trọng trong nguồn chưa có trong văn bản:'),
    h('ul', {}, ...items.map((m) => {
      const pick = h('select', { 'aria-label': T.missingAsk }, ...targets.map((s) => h('option', { value: s.id }, s.label)));
      const decide = async (decision) => {
        await postJson(`api/runs/${runId}/missing/${m.i}`, { decision });
        run.missing[m.i].decision = decision;
        drawMissing();
      };
      return h('li', { style: 'margin-bottom:8px' },
        h('div', {}, m.item, h('span', { className: 'muted small' }, ` — ${run.sources[m.source]?.name ?? ''} trang ${m.page}: «${m.quote}»`)),
        h('div', { className: 'item-actions' },
          targets.length ? pick : null,
          targets.length ? h('button', {
            type: 'button', className: 'btn btn-primary btn-small',
            onclick: async () => {
              const slot = run.slots.find((s) => s.id === pick.value);
              if (await revise(slot, `Bổ sung thông tin còn thiếu: ${m.item} (nguồn ghi: «${m.quote}»)`)) await decide('added');
            },
          }, 'Bổ sung') : null,
          h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => decide('ignored') }, 'Không cần')));
    }))));
}

function drawSummary() {
  const blocked = run.slots.filter(isBlocked).length;
  const look = run.slots.filter((s) => !isBlocked(s) && needsLook(s)).length;
  const ok = run.slots.length - blocked - look;
  $('#summary').replaceChildren(...[
    h('span', { className: 'pill ok' }, `${ok} ổn`),
    look ? h('span', { className: 'pill review' }, `${look} cần xem lại`) : null,
    blocked ? h('span', { className: 'pill blocked' }, `${blocked} thiếu giá trị`) : null,
    h('span', { className: 'badge' }, `${run.tokens.input + run.tokens.output} token`),
  ].filter(Boolean));
  $('#export').disabled = blocked > 0;
  $('#export-hint').textContent = blocked > 0 ? `${T.exportBlocked}.` : run.status === 'Done' ? 'Đã xuất file; có thể sửa và xuất lại.' : '';
}

function drawAll() {
  drawSummary();
  drawMissing();
  drawPreview();
  $('#values').replaceChildren(...[...run.slots].sort((a, b) => Number(isBlocked(b)) - Number(isBlocked(a)) || Number(needsLook(b)) - Number(needsLook(a))).map(valueCard));
}

// ---------------------------------------------------------------- source page

async function openPage(source, evidence) {
  if (!source) return;
  const dialog = $('#page-dialog');
  $('#page-title').textContent = `${source.name} · trang ${evidence.page}`;
  $('#page-text').textContent = 'Đang tải…';
  dialog.showModal();
  try {
    const data = await api(`api/sources/${source.fileId}`);
    const page = data.document.pages[evidence.page - 1];
    const text = (page?.blocks ?? []).map((b) => b.text ?? (b.rows ?? []).map((r) => r.map((c) => (c.ref ? `${c.ref}: ${c.text}` : c.text)).join(' | ')).join('\n')).join('\n');
    const at = text.toLocaleLowerCase('vi').indexOf(evidence.quote.toLocaleLowerCase('vi'));
    $('#page-text').replaceChildren(...(at >= 0
      ? [text.slice(0, at), h('mark', {}, text.slice(at, at + evidence.quote.length)), text.slice(at + evidence.quote.length)]
      : [text || '(trang trống)']));
    $('#page-text').querySelector('mark')?.scrollIntoView({ block: 'center' });
  } catch (err) {
    $('#page-text').textContent = err.message;
  }
}

// ---------------------------------------------------------------- start

async function exportFile() {
  const pending = run.slots.filter((s) => !isBlocked(s) && needsLook(s)).length;
  if (pending > 0 && !confirm(T.exportWarn(pending))) return;
  $('#export').disabled = true;
  try {
    await download(`api/runs/${runId}/export`, 'ket-qua');
    run.status = 'Done';
    toast('Đã xuất file');
  } catch (err) {
    toast(err.message, true);
  } finally {
    drawSummary();
  }
}

async function load() {
  run = await api(`api/runs/${runId}`);
  $('#run-title').textContent = run.recipe.name;
  $('#run-sub').textContent = `${run.sources.map((s) => s.name).join(', ')} · ${formatDate(run.createdAt)} · phiên bản ${run.recipe.version} · ${T.runStatus[run.status] ?? run.status}`;
  if (run.status === 'Queued' || run.status === 'Running') { await follow(); return; }
  if (run.status === 'failed') {
    $('#failed').hidden = false;
    $('#failed-text').textContent = `Lượt chạy không thành công: ${run.error}`;
    return;
  }
  values.clear();
  for (const v of run.values) values.set(v.slotId, v);
  outline = await api(`api/templates/${run.recipe.templateFileId}/outline`);
  $('#run-notes').replaceChildren(...(run.notes?.length ? [h('div', { className: 'notice', style: 'margin-top:12px' }, h('ul', {}, ...run.notes.map((n) => h('li', {}, n))))] : []));
  $('#review').hidden = false;
  drawAll();
  setupTour('review', [
    { selector: '#summary', title: 'Tổng quan', text: 'Số ô ổn, cần xem lại, và còn thiếu. Ô thiếu giá trị bắt buộc chặn việc xuất file.' },
    { selector: '#values', title: 'Từng giá trị', text: 'Mỗi ô có trích dẫn từ nguồn: bấm vào trích dẫn để mở trang nguồn. Sửa trực tiếp, hoặc nhờ AI viết lại; đánh dấu "Đã xem" khi đã kiểm tra.' },
    { selector: '#export', title: 'Xuất file', text: 'Xuất file Word/Excel đã điền. Có thể sửa và xuất lại bất cứ lúc nào.' },
  ]);
}

$('#export').addEventListener('click', exportFile);
$('#page-close').addEventListener('click', () => $('#page-dialog').close());
$('#rerun').addEventListener('click', async () => {
  try {
    const { runId: id } = await postJson('api/runs', { recipeId: run.recipe.id, sourceFileIds: run.sources.map((s) => s.fileId) });
    location.href = `run.html?id=${id}`;
  } catch (err) {
    toast(err.message, true);
  }
});

try {
  if (!runId) throw new Error('Thiếu mã lượt chạy');
  await load();
} catch (err) {
  if (err.status !== 401) toast(err.message, true);
}
