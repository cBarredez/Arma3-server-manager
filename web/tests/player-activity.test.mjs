import test from 'node:test';
import assert from 'node:assert/strict';
import { buildPlayerQuery, connectionIdentity, friendlyReason } from '../src/scripts/player-activity.js';

test('player query carries session filters and safely encodes search text', () => {
  const query = buildPlayerQuery({ runId: 'run-1', outcome: 'rejected', reasonCode: 'missing_addon', search: 'Alpha & <script>', cursor: '50' });
  assert.equal(query.get('runId'), 'run-1');
  assert.equal(query.get('outcome'), 'rejected');
  assert.equal(query.get('reasonCode'), 'missing_addon');
  assert.equal(query.get('q'), 'Alpha & <script>');
  assert.equal(query.get('cursor'), '50');
});

test('connection identity uses stable identifiers in priority order', () => {
  assert.equal(connectionIdentity({ steamUid: 'steam', battlEyeGuid: 'be', networkId: 'net' }), 'steam');
  assert.equal(connectionIdentity({ battlEyeGuid: 'be', networkId: 'net' }), 'be');
  assert.equal(connectionIdentity({ networkId: 'net' }), 'net');
  assert.equal(connectionIdentity({}), '—');
});

test('reason labels retain unknown future reason codes', () => {
  assert.equal(friendlyReason('steam_check'), 'Steam verification');
  assert.equal(friendlyReason('future_reason'), 'future reason');
  assert.equal(friendlyReason(null), 'No rejection reason');
});
