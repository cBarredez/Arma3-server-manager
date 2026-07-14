import test from 'node:test';
import assert from 'node:assert/strict';
import {
  LogConnectionGate,
  advanceLogCursor,
  countMissingLogEntries,
  isNearLogBottom,
  trimLogContainer,
} from '../src/scripts/log-viewer.js';

class FakeSource {
  closed = 0;
  close() { this.closed += 1; }
}

test('only the newest log load can own an EventSource', () => {
  const gate = new LogConnectionGate();
  const firstGeneration = gate.begin();
  const first = new FakeSource();
  assert.equal(gate.attach(firstGeneration, first), true);
  assert.equal(gate.hasSource, true);

  const secondGeneration = gate.begin();
  assert.equal(first.closed, 1);
  const stale = new FakeSource();
  assert.equal(gate.attach(firstGeneration, stale), false);
  assert.equal(stale.closed, 1);

  const second = new FakeSource();
  assert.equal(gate.attach(secondGeneration, second), true);
  gate.stop();
  assert.equal(second.closed, 1);
  assert.equal(gate.hasSource, false);
});

test('cursor advancement rejects duplicate and older events', () => {
  assert.equal(advanceLogCursor(100, 101), 101);
  assert.equal(advanceLogCursor(101, 101), 101);
  assert.equal(advanceLogCursor(101, 99), 101);
  assert.equal(advanceLogCursor(101, 'invalid'), 101);
});

test('gap count reports only entries that expired before the oldest available ID', () => {
  assert.equal(countMissingLogEntries({ requestedId: 10, oldestAvailableId: 15 }), 4);
  assert.equal(countMissingLogEntries({ requestedId: 14, oldestAvailableId: 15 }), 0);
  assert.equal(countMissingLogEntries({ requestedId: 50, oldestAvailableId: 15 }), 0);
});

test('bottom proximity preserves a reader who has scrolled up', () => {
  assert.equal(isNearLogBottom({ scrollHeight: 1000, scrollTop: 600, clientHeight: 300 }), false);
  assert.equal(isNearLogBottom({ scrollHeight: 1000, scrollTop: 660, clientHeight: 300 }), true);
});

test('log container is trimmed to its configured DOM bound and reports removed height', () => {
  const children = Array.from({ length: 7 }, (_, id) => ({ id, offsetHeight: 12 }));
  const container = {
    get childElementCount() { return children.length; },
    get firstElementChild() { return children[0] || null; },
    removeChild(node) {
      assert.equal(node, children[0]);
      children.shift();
    },
  };

  assert.deepEqual(trimLogContainer(container, 3), { count: 4, height: 48 });
  assert.deepEqual(children.map(child => child.id), [4, 5, 6]);
});
