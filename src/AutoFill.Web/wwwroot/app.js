// Main page: who is signed in, which workspace, and the two panes (recipes | run).
import { loadRecipes, setupRecipesPane } from './recipes-pane.js';
import { refreshRecipePicker, setupRunPane } from './run-pane.js';
import { setupTour } from './tour.js';
import { $, api, configureUploads, h, initialOf, postJson, toast } from './ui.js';

async function loadMe() {
  const me = await api('api/auth/me');
  $('#me').textContent = me.name || me.email;
  $('#me-initial').textContent = initialOf(me.name || me.email);
  $('#me-email').textContent = me.email;
  $('#admin-link').hidden = !me.isAdmin;
  configureUploads(me.uploadMode);

  // Only people in several workspaces need the switcher.
  const pick = $('#workspace');
  pick.replaceChildren(...me.workspaces.map((w) => h('option', { value: w.id, selected: w.id === me.workspaceId }, w.name)));
  $('#workspace-pick').hidden = me.workspaces.length < 2;
  return me;
}

$('#workspace').addEventListener('change', async (e) => {
  try {
    await postJson(`api/workspaces/${e.target.value}/select`);
    location.reload();
  } catch (err) {
    toast(err.message, true);
  }
});

$('#logout').addEventListener('click', async () => {
  await fetch('api/auth/logout', { method: 'POST' });
  location.href = 'login.html';
});

// Close the account menu on outside click / Escape.
const menu = document.querySelector('.user-menu');
document.addEventListener('click', (e) => { if (!menu.contains(e.target)) menu.open = false; });
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') menu.open = false; });

// Narrow screens: one pane at a time.
function setView(view) {
  $('#layout').dataset.view = view;
  $('#tab-run').setAttribute('aria-selected', String(view === 'run'));
  $('#tab-recipes').setAttribute('aria-selected', String(view === 'recipes'));
}
$('#tab-run').addEventListener('click', () => setView('run'));
$('#tab-recipes').addEventListener('click', () => setView('recipes'));

try {
  await loadMe();
  setupRecipesPane(refreshRecipePicker);
  setupRunPane(await loadRecipes());
  setupTour('main', [
    { title: 'Chào mừng đến AutoFill Studio', text: 'Hệ thống đọc tài liệu nguồn, điền vào file mẫu Word/Excel theo "công thức", rồi để anh/chị duyệt lại trước khi xuất file.' },
    { selector: '#recipes-pane .pane-head', before: () => setView('recipes'), title: 'Công thức', text: 'Bấm "+ Thêm mẫu" và chọn file Word/Excel mẫu (có thể là văn bản đã làm lần trước). AI tự tìm chỗ cần điền và lưu thành công thức, không cần làm gì thêm.' },
    { selector: '#source-drop', before: () => setView('run'), title: 'Tài liệu nguồn', text: 'Thả các file nguồn (PDF, bản scan, Word, Excel) vào đây. Bản scan được AI đọc lại nguyên văn.' },
    { selector: '#start', before: () => setView('run'), title: 'Xử lý', text: 'Bấm "Xử lý": hệ thống tự đọc tài liệu, tự chọn công thức phù hợp và điền. Kết quả hiện ngay bên dưới, chỉ những chỗ cần xem được liệt kê; xong bấm "Tải file".' },
  ]);
} catch (err) {
  if (err.status !== 401) toast(err.message, true);
}
