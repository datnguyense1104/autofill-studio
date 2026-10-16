import assert from 'node:assert/strict';
import { test } from 'node:test';
import { contentTypeFor, initialOf } from '../../src/AutoFill.Web/wwwroot/ui.js';

test('content type follows the extension, case-insensitive, and refuses other files', () => {
  assert.equal(contentTypeFor('Công hàm.PDF'), 'application/pdf');
  assert.equal(contentTypeFor('mau.xlsx'), 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet');
  assert.equal(contentTypeFor('mau.doc'), 'application/msword');
  assert.equal(contentTypeFor('virus.exe'), null);
  assert.equal(contentTypeFor('no-extension'), null);
  assert.equal(contentTypeFor(undefined), null);
});

test('avatar initial is the first letter, Vietnamese included', () => {
  assert.equal(initialOf('đạt nguyễn'), 'Đ');
  assert.equal(initialOf('  "Lan"'), 'L');
  assert.equal(initialOf('123'), '?');
  assert.equal(initialOf(null), '?');
});
