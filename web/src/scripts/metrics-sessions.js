export function buildMetricSessionQuery(filters = {}, cursor = null, limit = 25) {
  const query = new URLSearchParams({ limit: String(limit) });
  if (filters.from) query.set('from', new Date(`${filters.from}T00:00:00`).toISOString());
  if (filters.to) query.set('to', new Date(`${filters.to}T23:59:59.999`).toISOString());
  if (filters.status) query.set('status', filters.status);
  if (filters.query?.trim()) query.set('q', filters.query.trim());
  if (filters.sort === 'oldest') query.set('sort', 'oldest');
  if (cursor) query.set('cursor', cursor);
  return query.toString();
}

export function newestSessions(items, limit = 10) {
  return [...(items || [])]
    .sort((a, b) => new Date(b.startedAt).getTime() - new Date(a.startedAt).getTime())
    .slice(0, limit);
}

export function cpuCores(value) {
  const percent = Number(value);
  return Number.isFinite(percent) ? percent / 100 : null;
}

export function chartSeries(samples = []) {
  return {
    labels: samples.map(sample => sample.sampledAt),
    cpu: samples.map(sample => finiteOrNull(sample.cpuUsagePercent)),
    memory: samples.map(sample => finiteOrNull(sample.memoryPercent)),
    players: samples.map(sample => finiteOrNull(sample.activePlayers)),
    headless: samples.map(sample => finiteOrNull(sample.activeHeadlessClients)),
  };
}

function finiteOrNull(value) {
  if (value === null || value === undefined || value === '') return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}
