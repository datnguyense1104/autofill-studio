import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  applyValues, colToIndex, describeLocation, formatElapsed, indexToCol, isEmptyValue, nextSlotId, parseAddress, ruleRows, segments, selectionToPhrase,
} from '../../src/AutoFill.Web/wwwroot/slots.js';

const phrase = (id, paragraph, start, length, wholeParagraph = false) => ({ id, label: id, locations: [{ kind: 'phrase', paragraph, start, length, wholeParagraph }] });

test('column letters and cell addresses', () => {
  assert.equal(colToIndex('A'), 1);
  assert.equal(colToIndex('ab'), 28);
  assert.equal(indexToCol(28), 'AB');
  assert.equal(indexToCol(702), 'ZZ');
  assert.deepEqual(parseAddress('b7'), { col: 'B', row: 7 });
  assert.equal(parseAddress('B0'), null);
  assert.equal(parseAddress('7B'), null);
});

test('a selection becomes a phrase without its edge spaces', () => {
  assert.deepEqual(selectionToPhrase('Kính mời ông Nguyễn Văn A đến', 12, 26), { start: 13, length: 12, text: 'Nguyễn Văn A' });
  assert.deepEqual(selectionToPhrase('abc', 3, 1), { start: 1, length: 2, text: 'bc' });
  assert.equal(selectionToPhrase('a   b', 1, 4), null);
});

test('new slot ids never clash', () => {
  assert.equal(nextSlotId([{ id: 's1' }, { id: 's3' }]), 's4');
  assert.equal(nextSlotId([]), 's1');
});

test('a paragraph is cut around its phrases', () => {
  const s = { id: 'x' };
  assert.deepEqual(segments('Ông Nguyễn Văn A, quốc tịch Úc', [{ slot: s, start: 4, length: 12 }]),
    [{ text: 'Ông ' }, { text: 'Nguyễn Văn A', slot: s }, { text: ', quốc tịch Úc' }]);
});

test('values are written into the preview like the Word writer does', () => {
  const paragraphs = [
    { index: 0, text: 'Kính mời ông Nguyễn Văn A.', kind: 'prose' },
    { index: 1, text: '1. Nguyễn Văn A, hộ chiếu C1', kind: 'numbered' },
    { index: 2, text: 'Thân cũ một.', kind: 'prose' },
    { index: 3, text: 'Thân cũ hai.', kind: 'prose' },
    { index: 4, text: 'Trân trọng.', kind: 'prose' },
  ];
  const slots = [
    phrase('name', 0, 13, 12),
    phrase('people', 1, 0, 28, true),
    { id: 'body', label: 'Thân', locations: [{ kind: 'section', from: 2, to: 3 }] },
  ];
  const out = applyValues(paragraphs, slots, {
    name: { text: 'Trần B' }, people: { text: '1. Trần B\n2. Lê C' }, body: { text: 'Thân mới.' },
  });
  const text = out.map((p) => p.pieces.map((x) => x.text).join(''));
  assert.deepEqual(text, ['Kính mời ông Trần B.', '1. Trần B', '2. Lê C', 'Thân mới.', 'Trân trọng.']);
  assert.ok(out[0].pieces[1].filled);

  // An empty list entry disappears.
  const empty = applyValues(paragraphs, slots, { name: { text: 'X' }, people: { text: '' }, body: { text: 'Y' } });
  assert.equal(empty.length, 3);
});

test('empty values, location labels, row rules, elapsed time', () => {
  assert.ok(isEmptyValue({ text: '  ' }));
  assert.ok(isEmptyValue({ rows: { 3: '', 4: ' ' } }));
  assert.ok(!isEmptyValue({ rows: { 3: 'Y' } }));
  assert.equal(describeLocation({ kind: 'phrase', paragraph: 0, start: 4, length: 3 }, [{ index: 0, text: 'Ông Lan ở' }]), 'P0: «Lan»');
  assert.equal(describeLocation({ kind: 'rows', sheet: 'B210', labelColumn: 'B', column: 'G', rows: [1, 2] }), 'B210: cột G, 2 dòng theo nhãn cột B');
  const cells = [
    { address: 'B10', text: 'Câu 1' }, { address: 'B11', text: '' }, { address: 'B12', text: 'Câu 2' },
    { address: 'B13', text: 'x', hasFormula: true }, { address: 'C12', text: 'khác cột' }, { address: 'B50', text: 'ngoài vùng' },
  ];
  assert.deepEqual(ruleRows(cells, 'b', 10, 40), [10, 12]);
  assert.equal(formatElapsed(65), '1:05');
});
