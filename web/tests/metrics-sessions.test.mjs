import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { buildMetricSessionQuery, chartSeries, cpuCores, newestSessions } from '../src/scripts/metrics-sessions.js';

test('dashboard keeps exactly the ten newest sessions', () => {
  const sessions = Array.from({ length: 14 }, (_, index) => ({
    runId: `run-${index}`,
    startedAt: new Date(Date.UTC(2026, 0, index + 1)).toISOString(),
  })).reverse();

  const newest = newestSessions(sessions, 10);

  assert.equal(newest.length, 10);
  assert.equal(newest[0].runId, 'run-13');
  assert.equal(newest.at(-1).runId, 'run-4');
});

test('session explorer query encodes filters and pagination', () => {
  const query = new URLSearchParams(buildMetricSessionQuery({
    from: '2026-07-01',
    to: '2026-07-15',
    status: 'ended',
    query: ' ab c ',
    sort: 'oldest',
  }, '25', 25));

  assert.equal(query.get('limit'), '25');
  assert.equal(query.get('status'), 'ended');
  assert.equal(query.get('q'), 'ab c');
  assert.equal(query.get('sort'), 'oldest');
  assert.equal(query.get('cursor'), '25');
  assert.match(query.get('from'), /^2026-07-01T/);
  assert.ok(Number.isFinite(Date.parse(query.get('to'))));
  assert.ok(Date.parse(query.get('to')) > Date.parse(query.get('from')));
});

test('chart series preserves unavailable population instead of inventing zeros', () => {
  const series = chartSeries([
    { sampledAt: '2026-07-15T12:00:00Z', cpuUsagePercent: 235, memoryPercent: 61, activePlayers: null, activeHeadlessClients: null },
    { sampledAt: '2026-07-15T12:00:05Z', cpuUsagePercent: 250, memoryPercent: 64, activePlayers: 20, activeHeadlessClients: 2 },
  ]);

  assert.deepEqual(series.cpu, [235, 250]);
  assert.deepEqual(series.players, [null, 20]);
  assert.deepEqual(series.headless, [null, 2]);
  assert.equal(cpuCores(235), 2.35);
});

test('dashboard exposes the accessible session explorer and recent-session action', async () => {
  const markup = await readFile(new URL('../src/pages/index.astro', import.meta.url), 'utf8');

  assert.match(markup, /id="btn-open-session-explorer"/);
  assert.match(markup, /id="session-explorer-modal"[^>]*aria-labelledby="session-explorer-title"/);
  assert.match(markup, /data-session-range="90"/);
  assert.match(markup, /id="chart-session-detail"[^>]*aria-label=/);
  assert.match(markup, /Peak Players \/ HC/);
});
