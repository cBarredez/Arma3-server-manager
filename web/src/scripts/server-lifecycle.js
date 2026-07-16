const TRANSITIONAL_PHASES = new Set(['preparing', 'starting', 'stopping']);

export function normalizeServerStatus(value = {}) {
  if (typeof value === 'boolean') value = { running: value };
  const phase = value.phase || (value.running ? 'running' : 'stopped');
  return {
    ...value,
    phase,
    running: phase === 'running' || Boolean(value.running && !value.phase),
    busy: TRANSITIONAL_PHASES.has(phase),
    managed: value.managed !== false && phase !== 'blocked',
    conflictingPids: Array.isArray(value.conflictingPids) ? value.conflictingPids : [],
  };
}

export function lifecyclePresentation(value) {
  const status = normalizeServerStatus(value);
  const labels = {
    stopped: 'Offline',
    preparing: status.stage === 'updating_mods' ? 'Updating mods…' : status.stage === 'configuring' ? 'Configuring…' : 'Preparing…',
    starting: 'Starting…',
    running: 'Online',
    stopping: 'Stopping…',
    blocked: 'Conflict',
    faulted: 'Failed',
  };
  return {
    label: labels[status.phase] || status.phase,
    tone: status.phase === 'running' ? 'online' : status.busy ? 'busy' : status.phase === 'blocked' || status.phase === 'faulted' ? 'blocked' : 'offline',
    canStart: status.phase === 'stopped' || status.phase === 'faulted',
    canStop: status.phase === 'running' || status.phase === 'preparing' || status.phase === 'starting',
    canRestart: status.phase === 'running',
  };
}

export function lifecycleNotice(value) {
  const status = normalizeServerStatus(value);
  if (status.phase === 'blocked') {
    const pids = status.conflictingPids.length ? ` PID${status.conflictingPids.length > 1 ? 's' : ''}: ${status.conflictingPids.join(', ')}` : '';
    return `Unmanaged Arma server detected.${pids}`;
  }
  if (status.phase === 'faulted' && status.lastError) return status.lastError;
  return lifecyclePresentation(status).label;
}
