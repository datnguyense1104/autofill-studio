// Login and sign-up. A successful login goes to the app (or back to where the user came from).
const $ = (sel) => document.querySelector(sel);

function showTab(name) {
  for (const tab of ['login', 'register']) {
    $(`#tab-${tab}`).setAttribute('aria-selected', String(tab === name));
    $(`#${tab}-form`).hidden = tab !== name;
  }
}
$('#tab-login').addEventListener('click', () => showTab('login'));
$('#tab-register').addEventListener('click', () => showTab('register'));

function message(form, text, kind = 'error') {
  const el = form.querySelector('.form-message');
  el.textContent = text;
  el.className = `form-message ${kind}`;
}

async function post(url, body) {
  const res = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `Lỗi ${res.status}`);
  return data;
}

function handle(form, action) {
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const button = form.querySelector('button[type="submit"]');
    button.disabled = true;
    message(form, '');
    try {
      await action(Object.fromEntries(new FormData(form)));
    } catch (err) {
      message(form, err.message);
    } finally {
      button.disabled = false;
    }
  });
}

handle($('#login-form'), async (body) => {
  await post('api/auth/login', body);
  const next = new URLSearchParams(location.search).get('next');
  location.href = next?.startsWith('/') && !next.startsWith('//') ? next : '/';
});

handle($('#register-form'), async (body) => {
  const { message: text } = await post('api/auth/register', body);
  const form = $('#register-form');
  form.reset();
  message(form, text, 'ok');
});

// Already logged in: go straight to the app.
fetch('api/auth/me').then((res) => { if (res.ok) location.href = './'; });
