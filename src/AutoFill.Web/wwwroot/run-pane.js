// Right pane: drop sources (read in the background), pick or get a suggested recipe, start a run.
import { followJob, progressText } from './jobs.js';
import { T } from './strings.js';
import { $, api, formatDate, h, postJson, toast, uploadFile } from './ui.js';

/** One entry per dropped file: {name, fileId?, state: 'uploading'|'reading'|'ready'|'failed', note} */
const sources = [];

function renderSources() {
  $('#source-list').replaceChildren(...sources.map((s, i) => h('li', {},
    h('span', { className: 'name' }, s.name),
    h('span', { className: `status ${s.state === 'ready' ? 'ok' : s.state === 'failed' ? 'blocked' : ''}` }, s.note),
    h('button', { type: 'button', className: 'btn btn-ghost btn-small', 'aria-label': `Bỏ ${s.name}`, onclick: () => { sources.splice(i, 1); renderSources(); } }, '✕'))));
  updateButtons();
}

const readyIds = () => sources.filter((s) => s.state === 'ready').map((s) => s.fileId);

function updateButtons() {
  const ready = readyIds().length > 0;
  const busy = sources.some((s) => s.state === 'uploading' || s.state === 'reading');
  $('#suggest').disabled = !ready || busy;
  $('#start-run').disabled = !ready || busy || !$('#recipe-pick').value;
  $('#start-hint').hidden = !$('#start-run').disabled;
}

async function addSource(file) {
  const entry = { name: file.name, state: 'uploading', note: 'Đang tải lên…' };
  sources.push(entry);
  renderSources();
  try {
    const { key, name } = await uploadFile(file);
    const { fileId, jobId } = await postJson('api/sources', { key, name });
    Object.assign(entry, { fileId, state: 'reading', note: 'Đang đọc…' });
    renderSources();
    const job = await followJob(jobId, (j) => { entry.note = progressText(j); renderSources(); });
    const r = job.result;
    Object.assign(entry, { state: 'ready', note: T.sourceRead(r.pages, r.aiPages), notes: r.notes });
    for (const n of r.notes ?? []) if (n.includes('ký tự')) toast(`${entry.name}: ${n}`);
  } catch (err) {
    Object.assign(entry, { state: 'failed', note: err.message });
  }
  renderSources();
}

function setupDrop() {
  const zone = $('#source-drop');
  $('#source-files').addEventListener('change', (e) => { for (const f of e.target.files) addSource(f); e.target.value = ''; });
  zone.addEventListener('dragover', (e) => { e.preventDefault(); zone.classList.add('dragover'); });
  zone.addEventListener('dragleave', () => zone.classList.remove('dragover'));
  zone.addEventListener('drop', (e) => {
    e.preventDefault();
    zone.classList.remove('dragover');
    for (const f of e.dataTransfer.files) addSource(f);
  });
}

async function suggest() {
  const status = $('#suggest-status');
  status.hidden = false;
  status.textContent = 'Đang tìm công thức phù hợp…';
  $('#suggestions').replaceChildren();
  try {
    const { jobId } = await postJson('api/recipes/suggest', { sourceFileIds: readyIds() });
    const job = await followJob(jobId, (j) => { status.textContent = progressText(j); });
    const list = job.result.suggestions;
    status.textContent = list.length ? 'Bấm để chọn:' : 'Không có công thức nào phù hợp, hãy chọn tay hoặc tạo công thức mới.';
    $('#suggestions').replaceChildren(...list.map((s) => h('li', { className: 'tpl-item' },
      h('button', { type: 'button', className: 'tpl-row', onclick: () => { $('#recipe-pick').value = s.recipeId; updateButtons(); } },
        h('span', { className: 'tpl-name' }, `${s.name} · ${Math.round(s.score * 100)}%`),
        h('span', { className: 'muted small' }, s.reason)))));
    if (list[0] && !$('#recipe-pick').value) { $('#recipe-pick').value = list[0].recipeId; updateButtons(); }
  } catch (err) {
    status.textContent = err.message;
  }
}

async function startRun() {
  $('#start-run').disabled = true;
  try {
    const { runId } = await postJson('api/runs', { recipeId: $('#recipe-pick').value, sourceFileIds: readyIds() });
    location.href = `run.html?id=${runId}`;
  } catch (err) {
    toast(err.message, true);
    updateButtons();
  }
}

async function loadHistory() {
  const runs = await api('api/runs?limit=20');
  $('#history').replaceChildren(...runs.map((r) => h('li', { className: 'tpl-item' },
    h('a', { className: 'tpl-row', href: `run.html?id=${r.id}`, style: 'text-decoration:none' },
      h('span', { className: 'tpl-name' }, r.recipe),
      h('span', { className: 'tpl-tags' }, h('span', { className: `tag ${r.status === 'NeedsReview' ? 'warn' : ''}` }, T.runStatus[r.status] ?? r.status),
        h('span', { className: 'tag' }, formatDate(r.createdAt)))))));
  $('#history-empty').hidden = runs.length > 0;
}

/** @param {object[]} recipes from the recipes pane */
/** Rebuilds the recipe picker (a template just became a recipe), keeping the current choice. */
export function refreshRecipePicker(recipes) {
  const pick = $('#recipe-pick');
  const current = pick.value;
  pick.replaceChildren(h('option', { value: '' }, '— Chọn công thức —'), ...recipes.map((r) => h('option', { value: r.id }, r.name)));
  if (recipes.some((r) => r.id === current)) pick.value = current;
  updateButtons();
}

export function setupRunPane(recipes) {
  const pick = $('#recipe-pick');
  refreshRecipePicker(recipes);
  const wanted = new URLSearchParams(location.search).get('recipe');
  if (wanted && recipes.some((r) => r.id === wanted)) pick.value = wanted;
  pick.addEventListener('change', updateButtons);
  $('#suggest').addEventListener('click', suggest);
  $('#start-run').addEventListener('click', startRun);
  setupDrop();
  updateButtons();
  loadHistory().catch((err) => toast(err.message, true));
}
