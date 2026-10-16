// Left pane: the workspace's recipes. A click opens the builder; the run pane lists them too.
import { $, api, formatDate, h } from './ui.js';

let recipes = [];

function render() {
  const q = $('#recipe-search').value.trim().toLocaleLowerCase('vi');
  const shown = recipes.filter((r) => !q || `${r.name} ${r.useWhen}`.toLocaleLowerCase('vi').includes(q));
  $('#recipe-list').replaceChildren(...shown.map((r) => h('li', { className: 'tpl-item' },
    h('a', { className: 'tpl-row', href: `recipe.html?id=${r.id}`, style: 'text-decoration:none' },
      h('span', { className: 'tpl-name' }, r.name),
      r.useWhen ? h('span', { className: 'muted small' }, r.useWhen.length > 90 ? `${r.useWhen.slice(0, 90)}…` : r.useWhen) : null,
      h('span', { className: 'tpl-tags' }, h('span', { className: 'tag' }, `phiên bản ${r.currentVersion}`), h('span', { className: 'tag' }, formatDate(r.updatedAt)))))));
  $('#recipe-empty').hidden = recipes.length > 0;
}

/** @returns {Promise<object[]>} the recipes, for the run pane's picker */
export async function loadRecipes() {
  recipes = await api('api/recipes');
  render();
  return recipes;
}

$('#recipe-search').addEventListener('input', render);
