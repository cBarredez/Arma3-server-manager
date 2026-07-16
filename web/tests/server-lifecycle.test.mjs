import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { lifecycleNotice, lifecyclePresentation, normalizeServerStatus } from '../src/scripts/server-lifecycle.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

test('preparing is backend-owned busy state that permits stop but never another start', () => {
  const status = normalizeServerStatus({ phase: 'preparing', stage: 'updating_mods', operationId: 'op-1' });
  const controls = lifecyclePresentation(status);

  assert.equal(status.busy, true);
  assert.equal(controls.label, 'Updating mods…');
  assert.equal(controls.canStart, false);
  assert.equal(controls.canStop, true);
  assert.equal(controls.canRestart, false);
});

test('blocked state disables every lifecycle command and identifies external pids', () => {
  const status = normalizeServerStatus({ phase: 'blocked', conflictingPids: [42, 84], managed: false });
  const controls = lifecyclePresentation(status);

  assert.equal(controls.label, 'Conflict');
  assert.equal(controls.canStart, false);
  assert.equal(controls.canStop, false);
  assert.equal(controls.canRestart, false);
  assert.match(lifecycleNotice(status), /42, 84/);
});

test('running status retains its pid across repeated normalization', () => {
  const first = normalizeServerStatus({ phase: 'running', pid: 2302, runId: 'run-1', managed: true });
  const second = normalizeServerStatus(first);

  assert.equal(second.pid, 2302);
  assert.equal(second.running, true);
  assert.equal(lifecyclePresentation(second).canRestart, true);
});

test('legacy boolean status remains compatible while the API migrates', () => {
  assert.equal(normalizeServerStatus(true).phase, 'running');
  assert.equal(normalizeServerStatus(false).phase, 'stopped');
});

test('server status polling is global instead of being limited to the dashboard view', () => {
  const source = fs.readFileSync(path.join(root, 'src/scripts/app.js'), 'utf8');

  assert.match(source, /const pollStatus = \(\) => GET\('\/api\/server\/status'\)/);
  assert.match(source, /setInterval\(pollStatus, 2000\)/);
  assert.doesNotMatch(source, /if \(state\.view === 'dashboard'\) \{\s*GET\('\/api\/server\/status'\)/);
});
