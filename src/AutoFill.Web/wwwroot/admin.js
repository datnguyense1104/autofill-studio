// Admin page: list accounts, activate pending ones, disable or re-activate others.
import { $, api, formatDate, h, postJson, toast } from './ui.js';

const STATE_TEXT = { pending: 'Chờ kích hoạt', active: 'Đang hoạt động', disabled: 'Đã khóa' };
let users = [];
let me = null;
let filter = 'all';

async function changeStatus(user, status, button) {
  const who = user.name || user.email;
  if (status === 'disabled' && !confirm(`Khóa tài khoản ${who}? Người này sẽ bị đăng xuất ngay.`)) return;
  button.disabled = true;
  try {
    await postJson(`api/admin/users/${user.id}/status`, { status });
    users = users.map((u) => (u.id === user.id ? { ...u, status } : u));
    render();
    toast(`Đã ${status === 'active' ? 'kích hoạt' : 'khóa'} ${who}`);
  } catch (err) {
    toast(err.message, true);
    button.disabled = false;
  }
}

function actionsFor(user) {
  if (user.id === me?.id) return h('span', { className: 'muted small' }, 'Tài khoản của anh/chị');
  if (user.status === 'active') {
    return h('button', { type: 'button', className: 'btn btn-ghost btn-small', onclick: (e) => changeStatus(user, 'disabled', e.target) }, 'Khóa');
  }
  return h('button', { type: 'button', className: 'btn btn-primary btn-small', onclick: (e) => changeStatus(user, 'active', e.target) },
    user.status === 'pending' ? 'Kích hoạt' : 'Mở khóa');
}

function render() {
  const shown = users.filter((u) => filter === 'all' || u.status === filter);
  $('#user-rows').replaceChildren(...shown.map((u) => h('tr', {},
    h('td', {}, u.name || h('span', { className: 'muted' }, '(chưa ghi tên)'), u.isAdmin ? h('span', { className: 'muted small' }, ' · quản trị') : null),
    h('td', {}, u.email),
    h('td', {}, formatDate(u.createdAt)),
    h('td', {}, h('span', { className: `state ${u.status}` }, STATE_TEXT[u.status] || u.status)),
    h('td', {}, actionsFor(u)),
  )));
  $('#empty').hidden = shown.length > 0;
  for (const btn of document.querySelectorAll('[data-filter]')) {
    const count = btn.dataset.filter === 'all' ? users.length : users.filter((u) => u.status === btn.dataset.filter).length;
    btn.setAttribute('aria-pressed', String(btn.dataset.filter === filter));
    btn.textContent = `${btn.textContent.replace(/ \(\d+\)$/, '')} (${count})`;
  }
}

for (const btn of document.querySelectorAll('[data-filter]')) {
  btn.addEventListener('click', () => { filter = btn.dataset.filter; render(); });
}

$('#logout').addEventListener('click', async () => {
  await fetch('api/auth/logout', { method: 'POST' });
  location.href = 'login.html';
});

try {
  me = await api('api/auth/me');
  users = await api('api/admin/users');
  render();
} catch (err) {
  if (err.status !== 401) toast(err.message, true);
}
