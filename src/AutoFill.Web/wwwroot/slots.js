// Pure helpers for slots: no DOM, so `node --test` covers them.
// Locations match the server's: phrase {paragraph, start, length, wholeParagraph}, section {from, to},
// cell {sheet, cell}, rows {sheet, labelColumn, column, rows}.

/** "AB" → 28 (1-based). */
export function colToIndex(col) {
  return [...String(col).toUpperCase()].reduce((n, c) => n * 26 + (c.charCodeAt(0) - 64), 0);
}

/** 28 → "AB". */
export function indexToCol(index) {
  let s = '';
  for (let n = index; n > 0; n = Math.floor((n - 1) / 26)) s = String.fromCharCode(65 + ((n - 1) % 26)) + s;
  return s;
}

/** "B7" → {col: "B", row: 7}; null when not an address. */
export function parseAddress(address) {
  const m = String(address ?? '').toUpperCase().match(/^([A-Z]{1,3})([1-9][0-9]*)$/);
  return m ? { col: m[1], row: Number(m[2]) } : null;
}

/** A free slot id: s1, s2, ... */
export function nextSlotId(slots) {
  const used = new Set(slots.map((s) => s.id));
  for (let i = slots.length + 1; ; i += 1) if (!used.has(`s${i}`)) return `s${i}`;
}

/**
 * A text selection inside one paragraph → a phrase, without the spaces at its edges.
 * @returns {{start:number, length:number, text:string}|null}
 */
export function selectionToPhrase(text, from, to) {
  let start = Math.max(0, Math.min(from, to));
  let end = Math.min(text.length, Math.max(from, to));
  while (start < end && /\s/.test(text[start])) start += 1;
  while (end > start && /\s/.test(text[end - 1])) end -= 1;
  return end > start ? { start, length: end - start, text: text.slice(start, end) } : null;
}

/** The phrases of each paragraph: Map paragraph → [{slot, start, length, whole}] sorted by start. */
export function phrasesByParagraph(slots) {
  const map = new Map();
  for (const slot of slots) {
    for (const loc of slot.locations ?? []) {
      if (loc.kind !== 'phrase') continue;
      if (!map.has(loc.paragraph)) map.set(loc.paragraph, []);
      map.get(loc.paragraph).push({ slot, start: loc.start, length: loc.length, whole: Boolean(loc.wholeParagraph) });
    }
  }
  for (const list of map.values()) list.sort((a, b) => a.start - b.start);
  return map;
}

/** The section slot covering a paragraph, if any. */
export function sectionOf(slots, paragraph) {
  return slots.find((s) => (s.locations ?? []).some((l) => l.kind === 'section' && paragraph >= l.from && paragraph <= l.to)) ?? null;
}

/**
 * A paragraph cut into pieces for drawing: plain text and marked phrases.
 * Overlapping phrases (should not happen) keep the first.
 * @returns {{text:string, slot?:object}[]}
 */
export function segments(text, phrases = []) {
  const out = [];
  let at = 0;
  for (const p of phrases) {
    if (p.start < at) continue;
    if (p.start > at) out.push({ text: text.slice(at, p.start) });
    out.push({ text: text.slice(p.start, p.start + p.length), slot: p.slot });
    at = p.start + p.length;
  }
  if (at < text.length) out.push({ text: text.slice(at) });
  return out;
}

export function isEmptyValue(value) {
  if (!value) return true;
  if (value.rows) return Object.values(value.rows).every((v) => !String(v ?? '').trim());
  return !String(value.text ?? '').trim();
}

/**
 * The template's paragraphs with values written in, as the review screen shows them (the same
 * rules as the Word writer: phrases replaced, list entries removed or repeated, sections rewritten).
 * @param {{index:number, text:string, kind:string}[]} paragraphs from /api/templates/{id}/outline
 * @param {Record<string, {text?:string}>} values final value per slot id
 * @returns {{key:string, kind:string, pieces:{text:string, slot?:object, filled?:boolean}[]}[]}
 */
export function applyValues(paragraphs, slots, values) {
  const phrases = phrasesByParagraph(slots);
  const done = new Set();
  const out = [];
  for (const p of paragraphs) {
    const section = sectionOf(slots, p.index);
    if (section) {
      if (done.has(section.id)) continue;
      done.add(section.id);
      const lines = String(values[section.id]?.text ?? '').split('\n').map((l) => l.trim()).filter(Boolean);
      lines.forEach((line, i) => out.push({ key: `${section.id}:${i}`, kind: 'prose', pieces: [{ text: line, slot: section, filled: true }] }));
      if (!lines.length) out.push({ key: section.id, kind: 'prose', pieces: [{ text: '', slot: section, filled: true }] });
      continue;
    }
    const list = phrases.get(p.index) ?? [];
    const whole = list.find((x) => x.whole);
    if (whole) {
      const lead = p.text.match(/^\s*/)[0];
      const lines = String(values[whole.slot.id]?.text ?? '').split('\n').map((l) => l.trim()).filter(Boolean);
      lines.forEach((line, i) => out.push({ key: `${p.index}:${i}`, kind: p.kind, pieces: [{ text: lead }, { text: line, slot: whole.slot, filled: true }] }));
      continue;
    }
    const pieces = segments(p.text, list).map((s) => (s.slot ? { text: String(values[s.slot.id]?.text ?? '').trim(), slot: s.slot, filled: true } : s));
    out.push({ key: String(p.index), kind: p.kind, pieces });
  }
  return out;
}

/**
 * The sentence around a slot as the review shows it: its (first) paragraph with every value
 * written in, the slot itself flagged. Null for a section or a spreadsheet slot.
 * @returns {{text:string, current?:boolean}[]|null}
 */
export function contextOf(paragraphs, slots, values, slotId) {
  const slot = slots.find((s) => s.id === slotId);
  const loc = slot?.locations.find((l) => l.kind === 'phrase');
  const p = loc && paragraphs.find((x) => x.index === loc.paragraph);
  if (!p) return null;
  const [block] = applyValues([p], slots, values);
  if (!block) return null;
  return block.pieces.map((x) => ({ text: x.text, current: x.slot?.id === slotId }));
}

/**
 * Review status of a slot, as in the letter app: "blocked" (required and still empty: must be
 * fixed), "review" (the checks raised something and nobody looked yet), or "ok".
 */
export function reviewStatus(slot, row) {
  const value = row?.reviewedValue ?? row?.value;
  if (slot.required !== false && isEmptyValue(value)) return 'blocked';
  if ((row?.warnings?.length ?? 0) > 0 && !row.reviewed && !row.reviewedValue) return 'review';
  return 'ok';
}

/** Where a slot sits, in words, for its card. */
export function describeLocation(loc, paragraphs = []) {
  switch (loc.kind) {
    case 'phrase': {
      const text = paragraphs.find((p) => p.index === loc.paragraph)?.text ?? '';
      const phrase = text.slice(loc.start, loc.start + loc.length);
      return loc.wholeParagraph ? `Dòng danh sách P${loc.paragraph}` : `P${loc.paragraph}: «${phrase.length > 50 ? `${phrase.slice(0, 50)}…` : phrase}»`;
    }
    case 'section': return `Phần thân P${loc.from}–P${loc.to}`;
    case 'cell': return `${loc.sheet}!${loc.cell}`;
    case 'rows': return `${loc.sheet}: cột ${loc.column}, ${loc.rows.length} dòng theo nhãn cột ${loc.labelColumn}`;
    default: return '';
  }
}

/** Rows of a row rule: the rows of the label column that have a label, between two rows. */
export function ruleRows(cells, labelColumn, firstRow, lastRow) {
  const rows = new Set();
  for (const c of cells) {
    const a = parseAddress(c.address);
    if (a && a.col === labelColumn.toUpperCase() && a.row >= firstRow && a.row <= lastRow && String(c.text).trim() && !c.hasFormula) rows.add(a.row);
  }
  return [...rows].sort((x, y) => x - y);
}

/** Seconds → "1:05". */
export function formatElapsed(seconds) {
  const s = Math.max(0, Math.floor(seconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}
