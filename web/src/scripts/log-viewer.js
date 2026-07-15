export const LOG_BUFFER_LIMIT = 5000;
export const LOG_DOM_LIMIT = 160;
export const LOG_ROW_HEIGHT = 20;
export const LOG_OVERSCAN = 30;

export class CircularLogBuffer {
  constructor(capacity = LOG_BUFFER_LIMIT) {
    if (!Number.isInteger(capacity) || capacity < 1) throw new RangeError('capacity must be a positive integer');
    this.capacity = capacity;
    this.values = new Array(capacity);
    this.start = 0;
    this.count = 0;
    this.lastId = 0;
    this.expired = 0;
  }

  get length() { return this.count; }
  get expiredCount() { return this.expired; }
  get newest() { return this.count ? this.at(this.count - 1) : null; }

  at(index) {
    if (index < 0 || index >= this.count) return undefined;
    return this.values[(this.start + index) % this.capacity];
  }

  slice(from = 0, to = this.count) {
    const start = Math.max(0, Math.min(this.count, from));
    const end = Math.max(start, Math.min(this.count, to));
    return Array.from({ length: end - start }, (_, index) => this.at(start + index));
  }

  append(entry) {
    const id = Number(entry?.id) || 0;
    if (id > 0 && id <= this.lastId) return { added: false, expired: 0 };
    if (id > 0) this.lastId = id;

    let expired = 0;
    if (this.count < this.capacity) {
      this.values[(this.start + this.count) % this.capacity] = entry;
      this.count += 1;
    } else {
      this.values[this.start] = entry;
      this.start = (this.start + 1) % this.capacity;
      this.expired += 1;
      expired = 1;
    }
    return { added: true, expired };
  }

  appendMany(entries) {
    let added = 0;
    let expired = 0;
    for (const entry of entries || []) {
      const result = this.append(entry);
      if (result.added) added += 1;
      expired += result.expired;
    }
    return { added, expired };
  }

  clear({ preserveCursor = false } = {}) {
    this.values = new Array(this.capacity);
    this.start = 0;
    this.count = 0;
    this.expired = 0;
    if (!preserveCursor) this.lastId = 0;
  }
}

export class LogConnectionGate {
  constructor() {
    this.generation = 0;
    this.source = null;
  }

  begin() {
    this.generation += 1;
    this.closeCurrent();
    return this.generation;
  }

  attach(generation, source) {
    if (!this.isCurrent(generation)) {
      source.close();
      return false;
    }
    this.closeCurrent();
    this.source = source;
    return true;
  }

  owns(generation, source) {
    return this.isCurrent(generation) && this.source === source;
  }

  isCurrent(generation) {
    return generation === this.generation;
  }

  get hasSource() {
    return this.source !== null;
  }

  stop() {
    this.generation += 1;
    this.closeCurrent();
  }

  closeCurrent() {
    if (!this.source) return;
    this.source.close();
    this.source = null;
  }
}

export function advanceLogCursor(cursor, candidate) {
  const id = Number(candidate) || 0;
  return id > cursor ? id : cursor;
}

export function virtualLogRange({
  total,
  scrollTop,
  clientHeight,
  rowHeight = LOG_ROW_HEIGHT,
  overscan = LOG_OVERSCAN,
  maxRows = LOG_DOM_LIMIT,
}) {
  const safeTotal = Math.max(0, Number(total) || 0);
  const firstVisible = Math.max(0, Math.floor((Number(scrollTop) || 0) / rowHeight));
  const visibleRows = Math.max(1, Math.ceil((Number(clientHeight) || rowHeight) / rowHeight));
  let start = Math.max(0, firstVisible - overscan);
  let end = Math.min(safeTotal, firstVisible + visibleRows + overscan);
  if (end - start > maxRows) {
    const before = Math.min(overscan, Math.max(0, maxRows - visibleRows));
    start = Math.max(0, firstVisible - before);
    end = Math.min(safeTotal, start + maxRows);
    start = Math.max(0, end - maxRows);
  }
  return { start, end, offset: start * rowHeight, totalHeight: safeTotal * rowHeight };
}

export function isNearLogBottom(container, threshold = LOG_ROW_HEIGHT * 3) {
  return container.scrollHeight - container.scrollTop - container.clientHeight <= threshold;
}

export function shouldStreamLogs(view, panel, hidden) {
  return view === 'logs' && panel === 'live' && !hidden;
}

export function countMissingLogEntries(gap) {
  const requested = Number(gap?.requestedId) || 0;
  const oldest = Number(gap?.oldestAvailableId) || 0;
  return requested > 0 && oldest > requested + 1 ? oldest - requested - 1 : 0;
}

export function headlessClientLabel(source) {
  const match = /^headless-client-(\d+)$/i.exec(String(source || ''));
  return match ? `HC${match[1]}` : null;
}

export function headlessClientSummary(configured, pids) {
  const desired = Math.max(0, Math.min(5, Number.parseInt(configured, 10) || 0));
  const active = new Set((Array.isArray(pids) ? pids : []).map(Number).filter(pid => Number.isInteger(pid) && pid > 0)).size;
  return { desired, active, text: `HC processes active: ${active}/${desired}` };
}
