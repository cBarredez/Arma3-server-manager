export const REASON_LABELS = Object.freeze({
  missing_addon: 'Missing addon',
  unsigned_data: 'Unsigned data',
  different_data: 'Different addon version',
  hacked_data: 'Modified data',
  battleye: 'BattlEye',
  steam_check: 'Steam verification',
  dlc_content: 'DLC content',
  duplicate_id: 'Duplicate ID',
  network_timeout: 'Network timeout',
  manual_kick: 'Manual kick',
  banned: 'Banned',
  wrong_password: 'Wrong password',
  session_locked: 'Session locked',
  script: 'Server script',
  disconnected: 'Disconnected',
  unknown: 'Reason unavailable',
});

export function friendlyReason(code) {
  return REASON_LABELS[code] || (code ? String(code).replaceAll('_', ' ') : 'No rejection reason');
}

export function connectionIdentity(connection) {
  return connection?.steamUid || connection?.battlEyeGuid || connection?.networkId || '—';
}

export function buildPlayerQuery({ runId, outcome, reasonCode, search, cursor, limit = 50 }) {
  const query = new URLSearchParams({ runId: runId || '', limit: String(limit) });
  if (outcome) query.set('outcome', outcome);
  if (reasonCode) query.set('reasonCode', reasonCode);
  if (search?.trim()) query.set('q', search.trim());
  if (cursor) query.set('cursor', cursor);
  return query;
}
