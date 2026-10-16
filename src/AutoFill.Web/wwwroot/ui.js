// Small helpers shared by the page modules. Nothing here touches the DOM at import time,
// so the pure helpers can be tested with `node --test`.

export const $ = (sel) => document.querySelector(sel);

/** Tiny DOM builder; strings become text nodes, so AI/user text is never parsed as HTML. */
export function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === false || v == null) continue;
    if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
    else if (k === 'className') el.className = v;
    else el.setAttribute(k, v === true ? '' : v);
  }
  for (const c of children.flat()) if (c != null && c !== false) el.append(c instanceof Node ? c : String(c));
  return el;
}

export async function api(url, options = {}) {
  const res = await fetch(url, options);
  const isJson = res.headers.get('content-type')?.includes('application/json');
  // Session ended (logged out elsewhere, or the account was disabled): back to login.
  if (res.status === 401) location.href = `login.html?next=${encodeURIComponent(location.pathname)}`;
  if (!res.ok) {
    const body = isJson ? await res.json().catch(() => ({})) : {};
    const err = new Error(body.error || `Lỗi ${res.status}`);
    err.status = res.status;
    throw err;
  }
  return isJson ? res.json() : res;
}

export const postJson = (url, body) =>
  api(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

export const putJson = (url, body) =>
  api(url, { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) });

/** A textarea that grows with its text (no inner scrollbar), as in the SNV app. */
export function autoGrow(textarea) {
  const fit = () => { textarea.style.height = 'auto'; textarea.style.height = `${textarea.scrollHeight + 2}px`; };
  textarea.classList.add('grow');
  textarea.addEventListener('input', fit);
  requestAnimationFrame(fit);
  return textarea;
}

/** Saves a downloaded response (fetch) as a file, using the server's file name. */
export async function download(url, fallbackName) {
  const res = await api(url);
  const disposition = res.headers.get('content-disposition') ?? '';
  const name = decodeURIComponent(disposition.match(/filename\*=UTF-8''([^;]+)/i)?.[1] ?? disposition.match(/filename="?([^";]+)/i)?.[1] ?? fallbackName);
  const link = h('a', { href: URL.createObjectURL(await res.blob()), download: name });
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 10_000);
}

/** Must match the server's accepted file types (FileKeys.ContentTypes). */
const CONTENT_TYPES = {
  pdf: 'application/pdf',
  docx: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  doc: 'application/msword',
  xlsx: 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
  xls: 'application/vnd.ms-excel',
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
};

/** @returns {string|null} the content type for an accepted file name, else null */
export function contentTypeFor(name) {
  const ext = String(name ?? '').toLowerCase().match(/\.([a-z]+)$/)?.[1];
  return (ext && CONTENT_TYPES[ext]) || null;
}

/** First letter of a person's name (or email), for the avatar. */
export function initialOf(name) {
  const letter = String(name ?? '').trim().match(/\p{L}/u)?.[0];
  return letter ? letter.toLocaleUpperCase('vi') : '?';
}

// Files go up first and are then referred to by key.
// 'direct': PUT straight to private storage through a one-off signed URL (large scans never pass
// through the server). 'server': posted to this server (disk storage, development).
let uploadMode = 'server';
export function configureUploads(mode) {
  uploadMode = mode;
}

/** @returns {Promise<{key: string, name: string}>} what the API takes in place of the file */
export async function uploadFile(file) {
  if (!contentTypeFor(file.name)) throw new Error(`Loại file không được hỗ trợ: ${file.name}`);
  if (uploadMode === 'direct') {
    const { key, url } = await postJson('api/uploads/direct', { name: file.name });
    const res = await fetch(url, { method: 'PUT', headers: { 'Content-Type': contentTypeFor(key), 'x-upsert': 'false' }, body: file });
    if (!res.ok) throw new Error(`Không tải được file lên (${res.status}): ${file.name}`);
    return { key, name: file.name };
  }
  const fd = new FormData();
  fd.append('file', file, file.name);
  const { key } = await api('api/uploads', { method: 'POST', body: fd });
  return { key, name: file.name };
}

let toastTimer;
export function toast(message, isError = false) {
  const el = $('#toast');
  el.textContent = message;
  el.className = `toast${isError ? ' error' : ''}`;
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { el.hidden = true; }, isError ? 8000 : 4000);
}

export const formatDate = (iso) => (iso ? new Date(iso).toLocaleString('vi-VN', { dateStyle: 'short', timeStyle: 'short' }) : '');
