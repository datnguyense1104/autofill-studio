// Main page: who is signed in, which workspace, and the two panes (recipes | run).
import { loadRecipes } from './recipes-pane.js';
import { setupRunPane } from './run-pane.js';
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
for (const [tab, view] of [['#tab-run', 'run'], ['#tab-recipes', 'recipes']]) {
  $(tab).addEventListener('click', () => {
    $('#layout').dataset.view = view;
    $('#tab-run').setAttribute('aria-selected', String(view === 'run'));
    $('#tab-recipes').setAttribute('aria-selected', String(view === 'recipes'));
  });
}

try {
  await loadMe();
  setupRunPane(await loadRecipes());
  setupTour('main', [
    { title: 'Chào mừng đến AutoFill Studio', text: 'Hệ thống đọc tài liệu nguồn, điền vào file mẫu Word/Excel theo "công thức", rồi để anh/chị duyệt lại trước khi xuất file.' },
    { selector: '#recipes-pane', title: 'Công thức', text: 'Mỗi công thức là một file mẫu và cách lấy giá trị cho từng chỗ cần điền. Bấm "Tạo mới" để tạo công thức đầu tiên: AI sẽ tự tìm chỗ cần điền trong mẫu.' },
    { selector: '#source-drop', title: 'Tài liệu nguồn', text: 'Thả các file nguồn (PDF, bản scan, Word, Excel) vào đây. Bản scan được AI đọc lại nguyên văn.' },
    { selector: '#pick-title', title: 'Chọn công thức', text: 'Chọn tay, hoặc bấm "Gợi ý" để AI chọn công thức hợp với tài liệu vừa thả.' },
    { selector: '#start-run', title: 'Điền và duyệt', text: 'Bấm "Điền mẫu", hệ thống điền ở nền rồi mở màn hình duyệt: mỗi giá trị có trích dẫn từ nguồn để kiểm tra.' },
  ]);
} catch (err) {
  if (err.status !== 401) toast(err.message, true);
}
