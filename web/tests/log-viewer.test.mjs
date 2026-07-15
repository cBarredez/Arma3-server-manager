import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  CircularLogBuffer,
  LOG_DOM_LIMIT,
  LogConnectionGate,
  advanceLogCursor,
  countMissingLogEntries,
  isNearLogBottom,
  shouldStreamLogs,
  virtualLogRange,
} from '../src/scripts/log-viewer.js';

test('only the newest log load can own an EventSource', () => {
  const closed = [];
  const source = id => ({ close: () => closed.push(id) });
  const gate = new LogConnectionGate();

  const first = gate.begin();
  assert.equal(gate.attach(first, source('first')), true);
  const second = gate.begin();
  assert.deepEqual(closed, ['first']);
  assert.equal(gate.attach(first, source('stale')), false);
  assert.equal(gate.attach(second, source('second')), true);
  gate.stop();

  assert.deepEqual(closed, ['first', 'stale', 'second']);
});

test('cursor advancement rejects duplicate and older events', () => {
  assert.equal(advanceLogCursor(120, 121), 121);
  assert.equal(advanceLogCursor(121, 121), 121);
  assert.equal(advanceLogCursor(121, 119), 121);
});

test('gap count reports only entries that expired before the oldest available ID', () => {
  assert.equal(countMissingLogEntries({ requestedId: 10, oldestAvailableId: 15 }), 4);
  assert.equal(countMissingLogEntries({ requestedId: 14, oldestAvailableId: 15 }), 0);
  assert.equal(countMissingLogEntries({ requestedId: 0, oldestAvailableId: 15 }), 0);
});

test('bottom proximity preserves a reader who has scrolled up', () => {
  assert.equal(isNearLogBottom({ scrollHeight: 1000, scrollTop: 650, clientHeight: 300 }), true);
  assert.equal(isNearLogBottom({ scrollHeight: 1000, scrollTop: 500, clientHeight: 300 }), false);
});

test('circular buffer retains only the newest entries and counts expiry', () => {
  const buffer = new CircularLogBuffer(3);
  assert.deepEqual(buffer.appendMany([1, 2, 3, 4, 5].map(id => ({ id, data: `line-${id}` }))), { added: 5, expired: 2 });
  assert.deepEqual(buffer.slice().map(entry => entry.id), [3, 4, 5]);
  assert.equal(buffer.expiredCount, 2);
  assert.deepEqual(buffer.append({ id: 5, data: 'duplicate' }), { added: false, expired: 0 });
});

test('clear can preserve the stream cursor while removing local history', () => {
  const buffer = new CircularLogBuffer(3);
  buffer.append({ id: 9, data: 'old' });
  buffer.clear({ preserveCursor: true });
  assert.equal(buffer.length, 0);
  assert.equal(buffer.append({ id: 9, data: 'duplicate' }).added, false);
  assert.equal(buffer.append({ id: 10, data: 'new' }).added, true);
});

test('virtual range never renders more than the DOM bound for 100,000 entries', () => {
  const buffer = new CircularLogBuffer(5_000);
  for (let id = 1; id <= 100_000; id++) buffer.append({ id, data: `line-${id}` });
  assert.equal(buffer.length, 5_000);
  assert.equal(buffer.expiredCount, 95_000);
  for (const scrollTop of [0, 20_000, 99_500]) {
    const range = virtualLogRange({ total: buffer.length, scrollTop, clientHeight: 600 });
    assert.ok(range.end - range.start <= LOG_DOM_LIMIT);
    assert.equal(range.offset, range.start * 20);
    assert.equal(range.totalHeight, 100_000);
  }
});

test('stream runs only for a visible live console', () => {
  assert.equal(shouldStreamLogs('logs', 'live', false), true);
  assert.equal(shouldStreamLogs('logs', 'players', false), false);
  assert.equal(shouldStreamLogs('logs', 'live', true), false);
  assert.equal(shouldStreamLogs('dashboard', 'live', false), false);
});

test('logs markup exposes accessible tabs and a virtual viewport', async () => {
  const html = await readFile(new URL('../src/pages/index.astro', import.meta.url), 'utf8');
  assert.match(html, /role="tablist"/);
  assert.equal((html.match(/role="tab"/g) || []).length, 3);
  assert.equal((html.match(/role="tabpanel"/g) || []).length, 3);
  assert.match(html, /id="log-virtual-spacer"/);
  assert.match(html, /aria-live="off"/);
});

test('application stylesheet is emitted through the hashed Astro asset pipeline', async () => {
  const layout = await readFile(new URL('../src/layouts/BaseLayout.astro', import.meta.url), 'utf8');
  assert.match(layout, /import '\.\.\/styles\/app\.css'/);
  assert.doesNotMatch(layout, /\/css\/style\.css/);
});
