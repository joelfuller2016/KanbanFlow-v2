import assert from 'node:assert/strict';
import test from 'node:test';
import { readActivityPages } from './audit-transport-checks.mjs';

test('activity paging permits non-monotonic IDs ordered by historical timestamps', async () => {
  const pages = [{ items: [{ id: 5 }], total: 2, nextBeforeId: 5 }, { items: [{ id: 20 }], total: 2, nextBeforeId: null }];
  assert.deepEqual((await readActivityPages(async () => pages.shift())).map(e => e.id), [5, 20]);
});

for (const [name, pages, error] of [
  ['repeated cursor', [{ items: [{ id: 5 }], total: 3, nextBeforeId: 5 }, { items: [{ id: 4 }], total: 3, nextBeforeId: 5 }], /cursor did not advance/],
  ['cursor cycle', [{ items: [{ id: 5 }], total: 4, nextBeforeId: 5 }, { items: [{ id: 4 }], total: 4, nextBeforeId: 4 }, { items: [{ id: 3 }], total: 4, nextBeforeId: 5 }], /cursor did not advance/],
  ['duplicate event', [{ items: [{ id: 5 }], total: 2, nextBeforeId: 5 }, { items: [{ id: 5 }], total: 2, nextBeforeId: 4 }], /duplicated/],
  ['empty continuation', [{ items: [], total: 2, nextBeforeId: 5 }], /without any events/]
]) {
  test(`activity paging rejects ${name} promptly`, async () => {
    let count = 0;
    await assert.rejects(readActivityPages(async () => { count++; return pages.shift(); }), error);
    assert.ok(count <= 3);
  });
}

test('activity paging has a total page budget even for unique cursors', async () => {
  let id = 10;
  await assert.rejects(readActivityPages(async () => ({ items: [{ id }], total: 100, nextBeforeId: --id }), 3), /exceeded 3 pages/);
  assert.equal(id, 7);
});
