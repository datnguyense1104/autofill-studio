// Right pane, as in the letter app: drop the sources, press "Xử lý"; the system reads them,
// picks the recipe, fills it, and shows the result card on this page.
import { followJob, progressText } from './jobs.js';
import { renderRunCard } from './result-card.js';
import { formatElapsed } from './slots.js';
import { T } from './strings.js';
import { $, api, formatDate, h, postJson, toast, uploadFile } from './ui.js';

let recipes = [];
const state = { files: [], running: false };

// ---------------------------------------------------------------- files

function renderFiles() {
  $('#source-list').replaceChildren(...state.files.map((f, i) => h('li', {},
    h('span', { className: 'name' }, f.name),
    state.running ? null : h('button', { type: 'button', className: 'btn btn-ghost btn-small', 'aria-label': `Bỏ ${f.name}`, onclick: () => { state.files.splice(i, 1); renderFiles(); } }, '✕'))));
  $('#start').disabled = state.running || state.files.length === 0;
}

function addFiles(list) {
  for (const f of list) if (!state.files.some((x) => x.name === f.name && x.size === f.size)) state.files.push(f);
  renderFiles();
}

// ---------------------------------------------------------------- progress steps

/** One line of the progress list, with a running timer until done/fail. */
function step(text) {
  const started = Date.now();
  const icon = h('span', { className: 'icon' }, '●');
  const label = h('span', {}, text);
  const timer = h('span', { className: 'timer' }, '0:00');
  const li = h('li', { className: 'running' }, icon, label, timer);
  $('#steps').append(li);
  const tick = setInterval(() => { timer.textContent = formatElapsed((Date.now() - started) / 1000); }, 1000);
  const end = (cls, mark, note) => {
    clearInterval(tick);
    li.className = cls;
    icon.textContent = mark;
    if (note) label.textContent = `${text} — ${note}`;
  };
  return {
    update: (note) => { label.textContent = note ? `${text} — ${note}` : text; },
    done: (note) => end('done', '✓', note),
    fail: (note) => end('failed', '✖', note),
  };
}

/** Uploads one file and reads it (cached by content on the server). @returns the file id */
async function readSource(file, i, total) {
  const s = step(`Đọc ${i + 1}/${total}: ${file.name}`);
  try {
    const { key, name } = await uploadFile(file);
    const { fileId, jobId } = await postJson('api/sources', { key, name });
    const job = await followJob(jobId, (j) => s.update(j.status === 'running' ? progressText(j) : ''));
    s.done(job.result.cached ? 'đã đọc trước đó' : T.sourceRead(job.result.pages, job.result.aiPages));
    return fileId;
  } catch (err) {
    s.fail(err.message);
    throw err;
  }
}

/** The recipe for these sources: the only one, the one in the link, or the AI's best match. */
async function chooseRecipe(fileIds) {
  const wanted = new URLSearchParams(location.search).get('recipe');
  if (wanted && recipes.some((r) => r.id === wanted)) return { recipeId: wanted, reason: 'Chọn từ trang công thức' };
  if (recipes.length === 1) return { recipeId: recipes[0].id, reason: 'Công thức duy nhất trong không gian làm việc' };
  const s = step('Chọn công thức phù hợp');
  try {
    const { jobId } = await postJson('api/recipes/suggest', { sourceFileIds: fileIds });
    const job = await followJob(jobId);
    const best = job.result.suggestions[0];
    s.done(best ? best.name : 'không có công thức phù hợp');
    return best ? { recipeId: best.recipeId, reason: best.reason } : null;
  } catch (err) {
    s.fail(err.message);
    throw err;
  }
}

/** Starts a run, follows it, then shows (or replaces) its card. */
async function fill(recipeId, fileIds, reason, cardEl) {
  const name = recipes.find((r) => r.id === recipeId)?.name ?? '';
  const s = step(`Điền mẫu: ${name}`);
  const el = cardEl ?? h('article', { className: 'card loading' }, h('div', { className: 'card-head' }, h('h2', {}, name), h('p', { className: 'reason' }, 'Đang điền…')));
  if (!cardEl) $('#results').append(el);
  el.classList.add('loading');
  try {
    const { runId, jobId } = await postJson('api/runs', { recipeId, sourceFileIds: fileIds });
    await followJob(jobId, (j) => s.update(j.status === 'running' ? progressText(j) : ''));
    s.done();
    await renderRunCard(el, runId, { reason, recipes, onRefill: (id) => fill(id, fileIds, 'Chọn thủ công', el) });
    loadHistory().catch(() => {});
  } catch (err) {
    s.fail(err.message);
    el.classList.remove('loading');
    toast(err.message, true);
  }
}

async function run() {
  if (!recipes.length) { toast('Chưa có công thức nào: bấm "+ Thêm mẫu" ở cột bên trái trước', true); return; }
  state.running = true;
  renderFiles();
  $('#steps').replaceChildren();
  $('#results').replaceChildren();
  $('#progress').hidden = false;
  $('#no-match').hidden = true;
  try {
    const fileIds = [];
    for (const [i, file] of state.files.entries()) fileIds.push(await readSource(file, i, state.files.length));
    const choice = await chooseRecipe(fileIds);
    if (!choice) {
      $('#no-match-reason').textContent = 'AI không thấy công thức nào hợp với các tài liệu này. Hãy chọn tay, hoặc thêm mẫu mới ở cột bên trái.';
      $('#manual-recipe').replaceChildren(...recipes.map((r) => h('option', { value: r.id }, r.name)));
      $('#manual-fill').onclick = () => { $('#no-match').hidden = true; fill($('#manual-recipe').value, fileIds, 'Chọn thủ công'); };
      $('#no-match').hidden = false;
      return;
    }
    await fill(choice.recipeId, fileIds, choice.reason);
  } catch (err) {
    toast(err.message, true);
  } finally {
    state.running = false;
    renderFiles();
    $('#reset').hidden = false;
  }
}

// ---------------------------------------------------------------- history

async function loadHistory() {
  const runs = await api('api/runs?limit=20');
  $('#history').replaceChildren(...runs.map((r) => h('li', { className: 'tpl-item' },
    h('a', { className: 'tpl-row', href: `run.html?id=${r.id}`, style: 'text-decoration:none' },
      h('span', { className: 'tpl-name' }, r.recipe),
      h('span', { className: 'tpl-tags' }, h('span', { className: `tag ${r.status === 'NeedsReview' ? 'warn' : ''}` }, T.runStatus[r.status] ?? r.status),
        h('span', { className: 'tag' }, formatDate(r.createdAt)))))));
  $('#history-empty').hidden = runs.length > 0;
}

// ---------------------------------------------------------------- setup

/** Keeps the recipe list current (a template just became a recipe). */
export function refreshRecipePicker(list) {
  recipes = list;
}

export function setupRunPane(list) {
  recipes = list;
  const zone = $('#source-drop');
  $('#source-files').addEventListener('change', (e) => { addFiles(e.target.files); e.target.value = ''; });
  zone.addEventListener('dragover', (e) => { e.preventDefault(); zone.classList.add('dragover'); });
  zone.addEventListener('dragleave', () => zone.classList.remove('dragover'));
  zone.addEventListener('drop', (e) => { e.preventDefault(); zone.classList.remove('dragover'); addFiles(e.dataTransfer.files); });
  $('#start').addEventListener('click', run);
  $('#reset').addEventListener('click', () => {
    state.files = [];
    renderFiles();
    $('#results').replaceChildren();
    $('#steps').replaceChildren();
    $('#progress').hidden = true;
    $('#no-match').hidden = true;
    $('#reset').hidden = true;
  });
  renderFiles();
  loadHistory().catch((err) => toast(err.message, true));
}
