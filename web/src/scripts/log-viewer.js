export const LOG_DOM_LIMIT = 2000;

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

export function isNearLogBottom(container, threshold = 56) {
  return container.scrollHeight - container.scrollTop - container.clientHeight <= threshold;
}

export function trimLogContainer(container, limit = LOG_DOM_LIMIT) {
  let count = 0;
  let height = 0;
  while (container.childElementCount > limit && container.firstElementChild) {
    const node = container.firstElementChild;
    height += Number(node.offsetHeight) || 0;
    container.removeChild(node);
    count += 1;
  }
  return { count, height };
}

export function countMissingLogEntries(gap) {
  const requested = Number(gap?.requestedId) || 0;
  const oldest = Number(gap?.oldestAvailableId) || 0;
  return requested > 0 && oldest > requested + 1 ? oldest - requested - 1 : 0;
}
