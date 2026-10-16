// Recipe builder: upload a template → AI proposes slots → the user checks them on the preview → save.
import { drawExcel } from './excel-grid.js';
import { followJob, progressText } from './jobs.js';
import { drawSlots } from './slot-editor.js';
import { nextSlotId, ruleRows } from './slots.js';
import { T } from './strings.js';
import { setupTour } from './tour.js';
import { $, api, configureUploads, h, postJson, toast, uploadFile } from './ui.js';
import { drawWord, readSelection } from './word-preview.js';

const state = {
  recipeId: new URLSearchParams(location.search).get('id'),
  templateFileId: null,
  templateName: '',
  kind: null,
  outline: null, // {kind:'word', paragraphs} | {kind:'excel', sheets}
  slots: [],
  rules: [],
  dirty: false,
  pick: null, // Excel: the picked cell
  selection: null, // Word: {phrase} | {section}
  sheet: null,
  focus: null,
  analyzing: 0, // which analysis is the current one (a later one wins)
};

const markDirty = () => { state.dirty = true; $('#save-status').textContent = 'Có thay đổi chưa lưu'; };
addEventListener('beforeunload', (e) => { if (state.dirty) { e.preventDefault(); e.returnValue = T.confirmLeave; } });

// ---------------------------------------------------------------- drawing

/** Recipe checks: {type, slot, other?, pattern?, message?} — rules on slots that no longer exist are dropped. */
function drawRules() {
  const ids = new Set(state.slots.map((s) => s.id));
  state.rules = state.rules.filter((r) => ids.has(r.slot) && (!r.other || ids.has(r.other)));
  const slotOptions = (value) => state.slots.map((s) => h('option', { value: s.id, selected: s.id === value }, `${s.id} · ${s.label}`));
  $('#rules').replaceChildren(...state.rules.map((rule) => {
    const set = (key, value) => { rule[key] = value || undefined; markDirty(); };
    return h('div', { className: 'inline-form' },
      h('select', { 'aria-label': 'Ô', onchange: (e) => set('slot', e.target.value) }, ...slotOptions(rule.slot)),
      h('select', { 'aria-label': 'Loại kiểm tra', onchange: (e) => { rule.type = e.target.value; markDirty(); drawRules(); } },
        ...Object.entries(T.ruleTypes).map(([v, text]) => h('option', { value: v, selected: v === rule.type }, text))),
      rule.type === 'regex'
        ? h('input', { type: 'text', value: rule.pattern ?? '', placeholder: '^\\d{2}/\\d{2}/\\d{4}$', 'aria-label': 'Mẫu', oninput: (e) => set('pattern', e.target.value) })
        : h('select', { 'aria-label': 'Ô so sánh', onchange: (e) => set('other', e.target.value) }, ...slotOptions(rule.other)),
      h('input', { type: 'text', value: rule.message ?? '', placeholder: 'Câu cảnh báo (tùy chọn)', 'aria-label': 'Câu cảnh báo', oninput: (e) => set('message', e.target.value) }),
      h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => { state.rules = state.rules.filter((r) => r !== rule); markDirty(); drawRules(); } }, 'Xóa'));
  }));
  $('#add-check').disabled = state.slots.length === 0;
}

function addCheck() {
  const [first, second] = state.slots;
  state.rules.push({ type: 'dateAfter', slot: first.id, other: (second ?? first).id });
  markDirty();
  drawRules();
}

function draw() {
  drawRules();
  $('#slot-count').textContent = String(state.slots.length);
  const paragraphs = state.outline?.paragraphs ?? [];
  drawSlots($('#slots'), state.slots, {
    paragraphs, focus: state.focus,
    onChange: () => { markDirty(); drawPreview(); },
    onDelete: (slot) => { state.slots = state.slots.filter((s) => s !== slot); markDirty(); draw(); },
  });
  drawPreview();
}

function drawPreview() {
  const focus = (slot) => { state.focus = slot.id; draw(); };
  if (state.outline?.kind === 'word') {
    drawWord($('#preview'), state.outline.paragraphs, state.slots, { onSlotClick: focus });
    $('#preview-hint').textContent = 'Bôi chọn chữ để thêm chỗ cần điền; chọn nhiều đoạn để đặt làm phần thân.';
  } else if (state.outline?.kind === 'excel') {
    drawExcel($('#preview'), state.outline.sheets, state.slots, {
      sheet: state.sheet, selected: state.pick?.address, onSlotClick: focus,
      onPick: (pick) => { state.pick = pick; state.sheet = pick.sheet; showSelectionBar(); },
    });
    $('#preview-hint').textContent = 'Bấm vào ô để thêm ô cần điền hoặc tạo luật dòng.';
  }
  showSelectionBar();
}

function showSelectionBar() {
  const word = state.outline?.kind === 'word';
  $('#add-phrase').hidden = !(word && state.selection?.phrase);
  $('#add-section').hidden = !(word && state.selection?.section);
  $('#add-cell').hidden = !(!word && state.pick);
  $('#add-rule').hidden = !(!word && state.pick);
  $('#selection-bar').hidden = [...$('#selection-bar').children].every((b) => b.hidden);
}

function showNotes(notes) {
  $('#analysis-notes').replaceChildren(...(notes?.length ? [h('div', { className: 'notice info' },
    h('p', {}, 'Ghi chú khi phân tích:'), h('ul', {}, ...notes.map((n) => h('li', {}, n))))] : []));
}

// ---------------------------------------------------------------- adding slots by hand

const newSlot = (fields) => ({
  id: nextSlotId(state.slots), label: '', instructions: '', valueType: 'text', strategy: 'extract', strategyConfig: null,
  options: [], required: true, group: '', sampleValue: null, confidence: 1, ...fields,
});

function overlapsPhrase(paragraph, start, length) {
  return state.slots.some((s) => s.locations.some((l) => l.kind === 'phrase' && l.paragraph === paragraph && l.start < start + length && start < l.start + l.length));
}

function addPhrase() {
  const { phrase } = state.selection ?? {};
  if (!phrase) return;
  if (overlapsPhrase(phrase.paragraph, phrase.start, phrase.length)) { toast('Đoạn chữ này chồng lên một chỗ cần điền khác', true); return; }
  const p = state.outline.paragraphs.find((x) => x.index === phrase.paragraph);
  const lead = p.text.length - p.text.trimStart().length;
  const whole = ['bullet', 'numbered', 'list'].includes(p.kind) && phrase.start <= lead + 4 && phrase.start + phrase.length === lead + p.text.trim().length;
  const slot = newSlot({
    label: phrase.text.length > 40 ? `${phrase.text.slice(0, 40)}…` : phrase.text, sampleValue: phrase.text,
    locations: [whole ? { kind: 'phrase', paragraph: p.index, start: lead, length: p.text.trim().length, wholeParagraph: true }
      : { kind: 'phrase', paragraph: phrase.paragraph, start: phrase.start, length: phrase.length, wholeParagraph: false }],
  });
  state.slots.push(slot);
  finishAdd(slot);
}

function addSection() {
  const { section } = state.selection ?? {};
  if (!section) return;
  if (state.slots.some((s) => s.locations.some((l) => l.kind === 'section' && l.from <= section.to && section.from <= l.to))) {
    toast('Vùng này chồng lên một phần thân khác', true);
    return;
  }
  // Phrases inside the section are rewritten with it.
  for (const s of state.slots) s.locations = s.locations.filter((l) => !(l.kind === 'phrase' && l.paragraph >= section.from && l.paragraph <= section.to));
  state.slots = state.slots.filter((s) => s.locations.length > 0);
  const texts = state.outline.paragraphs.filter((p) => p.index >= section.from && p.index <= section.to && p.text.trim()).map((p) => p.text.trim());
  const slot = newSlot({ label: 'Phần thân', valueType: 'longText', strategy: 'generate', sampleValue: texts.join('\n'), locations: [{ kind: 'section', ...section }] });
  state.slots.push(slot);
  finishAdd(slot);
}

function cellTaken(sheet, address) {
  return state.slots.some((s) => s.locations.some((l) => (l.kind === 'cell' && l.sheet === sheet && l.cell === address)
    || (l.kind === 'rows' && l.sheet === sheet && l.rows.some((r) => `${l.column}${r}` === address))));
}

function addCell() {
  const { sheet, address } = state.pick ?? {};
  if (!sheet) return;
  if (cellTaken(sheet, address)) { toast('Ô này đã là chỗ cần điền', true); return; }
  const cell = state.outline.sheets.find((s) => s.name === sheet)?.cells.find((c) => c.address === address);
  if (cell?.hasFormula) { toast('Ô có công thức, không điền được', true); return; }
  const slot = newSlot({ label: `Ô ${address}`, sampleValue: cell?.text || null, locations: [{ kind: 'cell', sheet, cell: address }] });
  state.slots.push(slot);
  finishAdd(slot);
}

/** Row rule from the picked cell: its column is filled on every labelled row of a label column. */
function addRule() {
  const { sheet, col, row } = state.pick ?? {};
  if (!sheet) return;
  const cells = state.outline.sheets.find((s) => s.name === sheet).cells;
  const lastRow = Math.max(row, ...cells.map((c) => c.row));
  const form = h('form', { className: 'notice info', style: 'display:grid;gap:8px' },
    h('p', {}, `Luật dòng: điền cột ${col} ở mọi dòng có nhãn`),
    h('div', { className: 'slot-grid' },
      h('label', { className: 'field' }, h('span', { className: 'field-label' }, 'Cột nhãn'), h('input', { type: 'text', name: 'label', value: 'B', required: true })),
      h('label', { className: 'field' }, h('span', { className: 'field-label' }, 'Từ dòng'), h('input', { type: 'text', name: 'first', value: String(row), required: true })),
      h('label', { className: 'field' }, h('span', { className: 'field-label' }, 'Đến dòng'), h('input', { type: 'text', name: 'last', value: String(lastRow), required: true })),
      h('label', { className: 'field' }, h('span', { className: 'field-label' }, 'Nhóm'), h('input', { type: 'text', name: 'group', value: `Bảng ${sheet}` }))),
    h('div', { className: 'actions' },
      h('button', { type: 'submit', className: 'btn btn-primary btn-small' }, 'Tạo luật'),
      h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => form.remove() }, 'Hủy')));
  form.addEventListener('submit', (e) => {
    e.preventDefault();
    const data = Object.fromEntries(new FormData(form));
    const labelColumn = data.label.trim().toUpperCase();
    const rows = ruleRows(cells, labelColumn, Number(data.first), Number(data.last)).filter((r) => !cellTaken(sheet, `${col}${r}`));
    if (!rows.length) { toast(`Cột ${labelColumn} không có nhãn nào trong vùng dòng này`, true); return; }
    const slot = newSlot({ label: `Cột ${col}`, group: data.group.trim(), locations: [{ kind: 'rows', sheet, labelColumn, column: col, rows }] });
    state.slots.push(slot);
    form.remove();
    finishAdd(slot);
  });
  $('#analysis-notes').prepend(form);
}

function finishAdd(slot) {
  state.focus = slot.id;
  state.selection = null;
  getSelection()?.removeAllRanges();
  markDirty();
  draw();
}

// ---------------------------------------------------------------- template analysis

async function uploadSources(input) {
  const ids = [];
  for (const file of input.files) {
    const { key, name } = await uploadFile(file);
    ids.push((await postJson('api/sources', { key, name })).fileId);
  }
  return ids;
}

async function analyze(skipAi) {
  const file = $('#template-file').files[0];
  if (!file) { toast('Hãy chọn file mẫu', true); return; }
  if (state.slots.length && !confirm('Đổi file mẫu sẽ thay toàn bộ chỗ cần điền hiện có. Tiếp tục?')) return;
  const dialog = $('#busy');
  const token = ++state.analyzing;
  dialog.showModal();
  $('#busy-text').textContent = 'Đang tải file lên…';
  try {
    const { key, name } = await uploadFile(file);
    const body = { key, name, notes: $('#analyze-notes').value, skipAi };
    if (!skipAi) {
      $('#busy-text').textContent = 'Đang tải các file kèm theo…';
      body.filledFileIds = await uploadSources($('#filled-files'));
      body.exampleFileIds = await uploadSources($('#example-files'));
    }
    const { fileId, jobId } = await postJson('api/templates', body);
    state.pendingFileId = fileId;
    await finishAnalysis(jobId, token);
  } catch (err) {
    if (token === state.analyzing) { dialog.close(); toast(err.message, true); }
  }
}

/** The AI is taking long: mark by hand instead (the AI job finishes unused). */
async function skipWaiting() {
  if (!state.pendingFileId) return;
  const token = ++state.analyzing;
  try {
    const { jobId } = await postJson('api/templates', { fileId: state.pendingFileId, skipAi: true });
    await finishAnalysis(jobId, token);
  } catch (err) {
    $('#busy').close();
    toast(err.message, true);
  }
}

async function finishAnalysis(jobId, token) {
  const job = await followJob(jobId, (j) => { if (token === state.analyzing) $('#busy-text').textContent = progressText(j); });
  if (token !== state.analyzing) return;
  const { templateFileId, name, analysis } = job.result;
  Object.assign(state, { templateFileId, templateName: name, kind: analysis.kind, slots: analysis.slots, sheet: null, pick: null, focus: null });
  if (!$('#recipe-name').value) $('#recipe-name').value = name.replace(/\.[^.]+$/, '');
  if (!$('#recipe-use-when').value && analysis.useWhen) $('#recipe-use-when').value = analysis.useWhen;
  $('#recipe-missing').checked = analysis.kind === 'word';
  state.outline = await api(`api/templates/${templateFileId}/outline`);
  $('#busy').close();
  showEdit();
  showNotes(analysis.notes);
  markDirty();
  draw();
}

function showEdit() {
  $('#step-upload').hidden = true;
  $('#step-edit').hidden = false;
  $('#template-info').textContent = `File mẫu: ${state.templateName} (${state.kind === 'excel' ? 'Excel' : 'Word'})`;
}

// ---------------------------------------------------------------- save

async function save() {
  const name = $('#recipe-name').value.trim();
  if (!name) { toast('Hãy đặt tên cho công thức', true); $('#recipe-name').focus(); return; }
  if (!state.slots.length) { toast(T.noSlots, true); return; }
  const unnamed = state.slots.find((s) => !s.label.trim());
  if (unnamed) { toast(`Ô ${unnamed.id} chưa có tên`, true); state.focus = unnamed.id; draw(); return; }
  const body = {
    name, templateFileId: state.templateFileId, slots: state.slots, rules: state.rules,
    notes: $('#recipe-notes').value, useWhen: $('#recipe-use-when').value, checkMissing: $('#recipe-missing').checked,
  };
  $('#save').disabled = true;
  try {
    const res = state.recipeId ? await postJson(`api/recipes/${state.recipeId}/versions`, body) : await postJson('api/recipes', body);
    state.recipeId = res.id;
    state.dirty = false;
    history.replaceState(null, '', `recipe.html?id=${res.id}`);
    $('#page-title').textContent = 'Sửa công thức';
    $('#save-status').textContent = `${T.saved} — phiên bản ${res.version}`;
    $('#try-run').href = `./?recipe=${res.id}`;
    $('#try-run').hidden = false;
    toast(`${T.saved} công thức "${name}"`);
  } catch (err) {
    toast(err.message, true);
  } finally {
    $('#save').disabled = false;
  }
}

// ---------------------------------------------------------------- start

async function loadExisting(id) {
  const recipe = await api(`api/recipes/${id}`);
  const v = recipe.version;
  Object.assign(state, { templateFileId: v.templateFileId, templateName: v.templateName, kind: v.kind, slots: v.slots, rules: v.rules });
  $('#recipe-name').value = recipe.name;
  $('#recipe-use-when').value = recipe.useWhen;
  $('#recipe-notes').value = v.notes;
  $('#recipe-missing').checked = v.checkMissing;
  $('#page-title').textContent = 'Sửa công thức';
  $('#save-status').textContent = `Phiên bản ${v.number}`;
  $('#try-run').href = `./?recipe=${id}`;
  $('#try-run').hidden = false;
  state.outline = await api(`api/templates/${v.templateFileId}/outline`);
  showEdit();
  draw();
}

$('#analyze').addEventListener('click', () => analyze(false));
$('#analyze-manual').addEventListener('click', () => analyze(true));
$('#busy-manual').addEventListener('click', skipWaiting);
$('#busy').addEventListener('cancel', (e) => e.preventDefault()); // closes only through its own button
$('#add-phrase').addEventListener('click', addPhrase);
$('#add-section').addEventListener('click', addSection);
$('#add-cell').addEventListener('click', addCell);
$('#add-rule').addEventListener('click', addRule);
$('#save').addEventListener('click', save);
$('#add-check').addEventListener('click', addCheck);
$('#change-template').addEventListener('click', () => { $('#step-upload').hidden = false; $('#step-upload').scrollIntoView(); });
for (const id of ['#recipe-name', '#recipe-use-when', '#recipe-notes', '#recipe-missing']) $(id).addEventListener('input', markDirty);
document.addEventListener('selectionchange', () => {
  if (state.outline?.kind !== 'word') return;
  state.selection = readSelection($('#preview'), state.outline.paragraphs);
  showSelectionBar();
});

try {
  const me = await api('api/auth/me');
  configureUploads(me.uploadMode);
  if (state.recipeId) await loadExisting(state.recipeId);
  setupTour('recipe', [
    { selector: '#step-upload', title: 'File mẫu', text: 'Tải file mẫu lên — có thể là văn bản đã làm lần trước, không cần tô màu. AI sẽ tìm chỗ thay đổi theo từng lần.' },
    { title: 'Soát chỗ cần điền', text: 'Sau khi phân tích, bản xem trước tô vàng các chỗ cần điền. Bôi chọn chữ để thêm chỗ mới, bấm vào chỗ tô màu để sửa cách lấy giá trị.' },
    { title: 'Lưu và chạy thử', text: 'Lưu công thức rồi bấm "Chạy thử" với một bộ tài liệu nguồn. Mỗi lần lưu là một phiên bản mới; lượt chạy cũ vẫn giữ phiên bản đã dùng.' },
  ]);
} catch (err) {
  if (err.status !== 401) toast(err.message, true);
}
