// Left pane: the workspace's recipes. "+ Thêm mẫu" uploads template files; each is analysed on the
// server and saved as a recipe by itself (upload and done). A click on a recipe opens its details.
import { followJob, progressText } from './jobs.js';
import { $, api, formatDate, h, postJson, toast, uploadFile } from './ui.js';

let recipes = [];
let onRecipesChanged = () => {};
/** Templates being analysed: {name, note, failed} */
const pending = [];

function render() {
  const q = $('#recipe-search').value.trim().toLocaleLowerCase('vi');
  const shown = recipes.filter((r) => !q || `${r.name} ${r.useWhen}`.toLocaleLowerCase('vi').includes(q));
  $('#recipe-list').replaceChildren(...shown.map((r) => h('li', { className: 'tpl-item' },
    h('a', { className: 'tpl-row', href: `recipe.html?id=${r.id}`, style: 'text-decoration:none' },
      h('span', { className: 'tpl-name' }, r.name),
      r.useWhen ? h('span', { className: 'muted small' }, r.useWhen.length > 90 ? `${r.useWhen.slice(0, 90)}…` : r.useWhen) : null,
      h('span', { className: 'tpl-tags' }, h('span', { className: 'tag' }, `phiên bản ${r.currentVersion}`), h('span', { className: 'tag' }, formatDate(r.updatedAt)))))));
  $('#recipe-empty').hidden = recipes.length > 0 || pending.length > 0;
  $('#template-pending').replaceChildren(...pending.map((p) => h('li', {},
    h('span', { className: 'name' }, p.name),
    h('span', { className: `status ${p.failed ? 'blocked' : ''}` }, p.note),
    p.failed ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', 'aria-label': `Bỏ ${p.name}`, onclick: () => { pending.splice(pending.indexOf(p), 1); render(); } }, '✕') : null)));
}

/** @returns {Promise<object[]>} the recipes, for the run pane's picker */
export async function loadRecipes() {
  recipes = await api('api/recipes');
  render();
  return recipes;
}

/** One template file: upload → analysis job that saves the recipe → list refreshed. */
async function addTemplate(file) {
  const entry = { name: file.name, note: 'Đang tải lên…' };
  pending.push(entry);
  render();
  try {
    const { key, name } = await uploadFile(file);
    const { jobId } = await postJson('api/templates', { key, name, createRecipe: true });
    entry.note = 'AI đang phân tích (thường 1–5 phút)…';
    render();
    const started = Date.now();
    await followJob(jobId, (job) => {
      const minutes = Math.floor((Date.now() - started) / 60000);
      entry.note = `${progressText(job)}${minutes ? ` · ${minutes} phút` : ''}`;
      render();
    });
    pending.splice(pending.indexOf(entry), 1);
    onRecipesChanged(await loadRecipes());
    toast(`Đã tạo công thức từ "${file.name}"`);
  } catch (err) {
    Object.assign(entry, { note: err.message, failed: true });
    render();
  }
}

/** @param {(recipes: object[]) => void} changed called when a new recipe appears (refreshes the run pane) */
export function setupRecipesPane(changed) {
  onRecipesChanged = changed;
  $('#recipe-search').addEventListener('input', render);
  $('#template-files').addEventListener('change', (e) => {
    for (const f of e.target.files) addTemplate(f);
    e.target.value = '';
  });
}
