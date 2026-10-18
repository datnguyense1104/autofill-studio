// Left pane: the workspace's recipes. "+ Thêm mẫu" uploads template files; each is analysed on the
// server and saved as a recipe by itself (upload and done). A click on a recipe opens its details.
import { followJob, progressText } from './jobs.js';
import { $, api, formatDate, h, postJson, toast, uploadFile } from './ui.js';

let recipes = [];
let onRecipesChanged = () => {};
/** Templates being analysed: {name, note, failed, duplicate?: {recipeId, name, similarity, templateFileId}} */
const pending = [];

function render() {
  const q = $('#recipe-search').value.trim().toLocaleLowerCase('vi');
  const shown = recipes.filter((r) => !q || `${r.name} ${r.useWhen}`.toLocaleLowerCase('vi').includes(q));
  $('#recipe-list').replaceChildren(...shown.map((r) => h('li', { className: 'tpl-item' },
    h('a', { className: 'tpl-row', href: `recipe.html?id=${r.id}`, style: 'text-decoration:none' },
      h('span', { className: 'tpl-name' }, r.name),
      r.useWhen ? h('span', { className: 'muted small' }, r.useWhen.length > 90 ? `${r.useWhen.slice(0, 90)}…` : r.useWhen) : null,
      h('span', { className: 'tpl-tags' }, h('span', { className: 'tag' }, `phiên bản ${r.currentVersion}`), h('span', { className: 'tag' }, formatDate(r.updatedAt)))),
    h('button', { type: 'button', className: 'tpl-delete', title: 'Xoá công thức', 'aria-label': `Xoá công thức ${r.name}`, onclick: () => removeRecipe(r) }, '✕'))));
  $('#recipe-empty').hidden = recipes.length > 0 || pending.length > 0;
  $('#template-pending').replaceChildren(...pending.map((p) => h('li', {},
    h('span', { className: 'name' }, p.name),
    h('span', { className: `status ${p.failed ? 'blocked' : p.duplicate ? 'review' : ''}` }, p.note),
    p.duplicate ? h('span', { className: 'actions' },
      h('a', { className: 'btn btn-ghost btn-small', href: `recipe.html?id=${p.duplicate.recipeId}` }, 'Xem công thức đã có'),
      h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: () => drop(p) }, 'Bỏ qua'),
      h('button', { type: 'button', className: 'btn btn-primary btn-small', onclick: () => analyse(p, { fileId: p.duplicate.templateFileId, createRecipe: true, allowDuplicate: true }) }, 'Vẫn thêm')) : null,
    p.failed ? h('button', { type: 'button', className: 'btn btn-ghost btn-small', 'aria-label': `Bỏ ${p.name}`, onclick: () => drop(p) }, '✕') : null)));
}

/** Past runs of a deleted recipe stay in the history; only the recipe leaves the list. */
async function removeRecipe(recipe) {
  if (!confirm(`Xoá công thức "${recipe.name}"?\n\nCông thức sẽ không còn trong danh sách và không dùng để điền được nữa. Các lượt đã chạy vẫn còn trong Lịch sử.`)) return;
  try {
    await api(`api/recipes/${recipe.id}`, { method: 'DELETE' });
    onRecipesChanged(await loadRecipes());
    toast(`Đã xoá công thức "${recipe.name}"`);
  } catch (err) {
    toast(err.message, true);
  }
}

function drop(entry) {
  pending.splice(pending.indexOf(entry), 1);
  render();
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
    await analyse(entry, { key, name, createRecipe: true });
  } catch (err) {
    Object.assign(entry, { note: err.message, failed: true });
    render();
  }
}

/** Runs the analysis; stops early (before the AI) when the workspace already has a recipe of this form. */
async function analyse(entry, body) {
  Object.assign(entry, { note: 'Đang kiểm tra mẫu…', failed: false, duplicate: null });
  render();
  try {
    const { jobId } = await postJson('api/templates', body);
    const started = Date.now();
    const job = await followJob(jobId, (j) => {
      const minutes = Math.floor((Date.now() - started) / 60000);
      entry.note = `${progressText(j)}${minutes ? ` · ${minutes} phút` : ''}`;
      render();
    });
    const same = job.result.duplicate;
    if (same) {
      entry.duplicate = { ...same, templateFileId: job.result.templateFileId };
      entry.note = `Mẫu này giống công thức "${same.name}" đã có (${Math.round(same.similarity * 100)}%), nên chưa thêm.`;
      render();
      return;
    }
    drop(entry);
    onRecipesChanged(await loadRecipes());
    toast(`Đã tạo công thức từ "${entry.name}"`);
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
