/* ═══════════════════════════════════════════════════════════════════════════
   Arma 3 Server Manager — Frontend SPA
   ═══════════════════════════════════════════════════════════════════════════ */
import {
  LOG_DOM_LIMIT,
  LogConnectionGate,
  advanceLogCursor,
  countMissingLogEntries,
  isNearLogBottom,
  trimLogContainer,
} from './log-viewer.js';

'use strict';

const API_BASE = (window.ARMA3_API_BASE || '').replace(/\/$/, '');
const REST_ONLY = !!window.ARMA3_REST_ONLY;
const apiUrl = (url) => `${API_BASE}${url}`;

// ─── API helpers ──────────────────────────────────────────────────────────────
async function api(method, url, body) {
  const opts = {
    method,
    headers: { 'Content-Type': 'application/json' },
    credentials: API_BASE ? 'include' : 'same-origin',
  };
  if (body !== undefined) opts.body = JSON.stringify(body);
  const res = await fetch(apiUrl(url), opts);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    const err = new Error(data.error || `HTTP ${res.status}`);
    err.status = res.status;
    err.code = data.code;
    err.data = data;
    throw err;
  }
  return data;
}

const GET    = (u)       => api('GET',    u);
const POST   = (u, b)    => api('POST',   u, b);
const PUT    = (u, b)    => api('PUT',    u, b);
const DELETE = (u)       => api('DELETE', u);

// ─── Toast notifications ──────────────────────────────────────────────────────
function toast(msg, type = 'success') {
  const container = document.getElementById('toast-container');
  const id = `t${Date.now()}`;
  const icon = type === 'success' ? 'circle-check' : type === 'warning' ? 'triangle-exclamation' : 'circle-xmark';
  const color = type === 'success' ? 'var(--accent)' : type === 'warning' ? 'var(--warning)' : 'var(--danger)';
  container.insertAdjacentHTML('beforeend', `
    <div id="${id}" class="toast align-items-center border-0 mb-2" role="alert" aria-live="assertive" data-bs-autohide="true" data-bs-delay="3500">
      <div class="d-flex toast-header">
        <i class="fa fa-${icon} me-2" style="color:${color}"></i>
        <strong class="me-auto" style="font-size:.85rem">${msg}</strong>
        <button type="button" class="btn-close btn-close-white ms-2" data-bs-dismiss="toast"></button>
      </div>
    </div>`);
  const el = document.getElementById(id);
  const t  = new bootstrap.Toast(el);
  t.show();
  el.addEventListener('hidden.bs.toast', () => el.remove());
}

// ─── Byte formatter ───────────────────────────────────────────────────────────
function fmtBytes(b) {
  b = Number(b) || 0;
  if (b >= 1e9) return (b / 1e9).toFixed(1) + ' GB';
  if (b >= 1e6) return (b / 1e6).toFixed(1) + ' MB';
  if (b >= 1e3) return (b / 1e3).toFixed(1) + ' KB';
  return b + ' B';
}

function fmtPercent(value) {
  if (value === null || value === undefined || value === '') return '—';
  const n = Number(value);
  if (!Number.isFinite(n)) return '—';
  return n > 0 && n < 10 ? n.toFixed(1) + '%' : Math.round(n) + '%';
}

function fmtDate(iso) {
  if (!iso) return '—';
  const d = new Date(iso);
  if (isNaN(d.getTime())) return '—';
  return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: '2-digit' }) +
    ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

function metricNumber(value) {
  if (value === null || value === undefined || value === '') return null;
  const n = Number(value);
  return Number.isFinite(n) ? n : null;
}

function setText(id, value) {
  const el = document.getElementById(id);
  if (el) el.textContent = value;
}

async function copyText(text) {
  if (!text) throw new Error('Nothing to copy');
  if (navigator.clipboard?.writeText && window.isSecureContext) {
    try {
      await navigator.clipboard.writeText(text);
      return;
    } catch { /* Fall back while the click event still owns focus. */ }
  }

  const input = document.createElement('textarea');
  input.value = text;
  input.setAttribute('readonly', '');
  input.style.position = 'fixed';
  input.style.left = '-9999px';
  input.style.opacity = '0';
  document.body.appendChild(input);
  input.focus();
  input.select();
  input.setSelectionRange(0, input.value.length);
  const copied = document.execCommand('copy');
  input.remove();
  if (!copied) throw new Error('Clipboard permission denied');
}

function renderUsageMetric(valueId, detailId, metric) {
  if (!metric) return;
  setText(valueId, fmtPercent(metric.percent));
  if (Number(metric.total) > 0) {
    setText(detailId, `${fmtBytes(metric.used)} / ${fmtBytes(metric.total)}`);
  }
}

// ─── State ────────────────────────────────────────────────────────────────────
const state = {
  view        : 'dashboard',
  serverRunning: false,
  currentConfig: 'server.cfg',
  currentFilePath: null,
  fileItems   : [],
  fileSortKey : 'name',
  fileSortDir : 'asc',
  socket      : null,
  cpuHistory  : [],
  memHistory  : [],
  chart       : null,
  steamCmdModal: null,
  factoryResetModal: null,
  restartAppModal: null,
  presetMods  : [],
};

const MAX_HISTORY = 30;
const VIEWS = ['dashboard', 'mods', 'files', 'config', 'logs', 'rcon', 'settings'];

// ════════════════════════════════════════════════════════════════════════════════
// BOOTSTRAP — Auth check on load
// ════════════════════════════════════════════════════════════════════════════════
document.addEventListener('DOMContentLoaded', async () => {
  document.addEventListener('click', async e => {
    const btn = e.target.closest('.btn-reset-steamcmd');
    if (!btn) return;
    e.preventDefault();
    await resetSteamCmd(btn);
  });

  document.addEventListener('click', async e => {
    const btn = e.target.closest('#btn-download-cdlcs');
    if (!btn) return;
    e.preventDefault();
    await downloadCreatorDlcs(btn);
  });

  window.addEventListener('hashchange', () => {
    const route = routeFromHash();
    if (!VIEWS.includes(route.view)) return;
    if (state.view !== route.view) loadView(route.view, false, route.filePath || null);
    else if (route.view === 'files') loadFiles(route.filePath || null, false);
  });

  document.querySelectorAll('a[href="/auth/steam"]').forEach(a => {
    a.href = apiUrl('/auth/steam');
  });

  // Show Steam auth errors from URL params
  const urlParams    = new URLSearchParams(window.location.search);
  const steamError   = urlParams.get('steam_error');
  if (steamError) {
    const msgs = {
      cancelled   : 'Steam login was cancelled.',
      invalid     : 'Steam could not verify your identity. Try again.',
      unauthorized: 'Your Steam account is not authorised to access this panel.',
      no_id       : 'Could not read Steam ID. Try again.',
      server_error: 'Server error during Steam login.',
    };
    const el = document.getElementById('login-error');
    el.textContent = msgs[steamError] || 'Steam login failed.';
    el.classList.remove('d-none');
    // Clean URL
    history.replaceState({}, '', '/');
  }

  const { authenticated } = await GET('/api/auth/check').catch(() => ({ authenticated: false }));
  if (authenticated) {
    showApp();
  } else {
    document.getElementById('login-screen').classList.remove('d-none');
  }

  // Login form
  document.getElementById('login-form').addEventListener('submit', async e => {
    e.preventDefault();
    const username = document.getElementById('login-user').value;
    const password = document.getElementById('login-pass').value;
    try {
      await POST('/api/auth/login', { username, password });
      document.getElementById('login-screen').classList.add('d-none');
      showApp();
    } catch (err) {
      const el = document.getElementById('login-error');
      el.textContent = err.message;
      el.classList.remove('d-none');
    }
  });
});

// ════════════════════════════════════════════════════════════════════════════════
// SHOW APP
// ════════════════════════════════════════════════════════════════════════════════
function showApp() {
  document.getElementById('login-screen').classList.add('d-none');
  document.getElementById('app').classList.remove('d-none');
  initSocket();
  initNav();
  initServerControls();
  initRconControls();
  const route = routeFromHash();
  const requestedView = route.view || localStorage.getItem('a3mgr.view') || 'dashboard';
  loadView(VIEWS.includes(requestedView) ? requestedView : 'dashboard', false, route.filePath || null);
}

// ─── Navigation ──────────────────────────────────────────────────────────────
function initNav() {
  if (initNav.bound) return;
  initNav.bound = true;

  document.querySelectorAll('[data-view]').forEach(link => {
    link.addEventListener('click', e => {
      e.preventDefault();
      loadView(link.dataset.view);
    });
  });

  document.getElementById('sidebar-toggle').addEventListener('click', () => {
    document.getElementById('sidebar').classList.toggle('collapsed');
  });

  document.getElementById('btn-logout').addEventListener('click', async () => {
    stopLogStream();
    await POST('/api/auth/logout').catch(() => {});
    location.reload();
  });

  window.addEventListener('offline', () => {
    if (state.view === 'logs') setLogStreamStatus('Offline', 'danger');
  });
  window.addEventListener('online', () => {
    if (state.view === 'logs') restartLogStream(document.getElementById('log-output'));
  });

}

function loadView(name, pushState = true, filePath = null) {
  if (!VIEWS.includes(name)) name = 'dashboard';
  document.querySelectorAll('.view').forEach(v => v.classList.add('d-none'));
  document.querySelectorAll('[data-view]').forEach(l => l.classList.remove('active'));

  // Close log stream when leaving the logs tab
  if (state.view === 'logs' && name !== 'logs') stopLogStream();

  document.getElementById(`view-${name}`).classList.remove('d-none');
  document.querySelector(`[data-view="${name}"]`)?.classList.add('active');
  document.getElementById('page-title').textContent =
    name.charAt(0).toUpperCase() + name.slice(1).replace('-', ' ');
  state.view = name;
  localStorage.setItem('a3mgr.view', name);
  if (pushState && location.hash !== `#${name}`) history.pushState({ view: name }, '', `#${name}`);

  switch (name) {
    case 'dashboard': initDashboard();  break;
    case 'mods':      loadMods();       break;
    case 'files':     loadFiles(filePath);  break;
    case 'config':    loadConfig('server.cfg'); break;
    case 'logs':      loadLogs();       break;
    case 'rcon':      loadRconPlayers(); break;
    case 'settings':  loadSettings();   break;
  }
}

// ════════════════════════════════════════════════════════════════════════════════
// SOCKET.IO
// ════════════════════════════════════════════════════════════════════════════════
function initSocket() {
  if (REST_ONLY || typeof io !== 'function') {
    const pollDashboard = () => {
      if (state.view === 'dashboard') {
        GET('/api/server/status').then(d => updateServerStatus(d.running)).catch(() => {});
        GET('/api/metrics').then(d => {
          renderMetrics(d);
          pushHistory(metricNumber(d.cpu?.load), metricNumber(d.memory?.percent));
        }).catch(() => {});
      }
    };
    pollDashboard();
    setInterval(pollDashboard, 2000);
    return;
  }

  state.socket = io({ transports: ['websocket'] });

  state.socket.on('server:status',  d => updateServerStatus(d.running));
  state.socket.on('server:started', d => {
    updateServerStatus(true);
    const pid = document.getElementById('dash-pid-text');
    if (pid) pid.textContent = `· PID ${d.pid}`;
    const info = document.getElementById('info-pid');
    if (info) info.textContent = d.pid;
    setActionBar(false);
    toast('Server started (PID ' + d.pid + ')');
  });
  state.socket.on('server:stopped', d => {
    updateServerStatus(false);
    const pid = document.getElementById('dash-pid-text');
    if (pid) pid.textContent = '';
    setActionBar(false);
    toast('Server stopped', d.code === 0 ? 'success' : 'error');
  });
  state.socket.on('server:error', d => {
    updateServerStatus(false);
    setActionBar(false);
    toast(d.message, 'error');
  });

  state.socket.on('server:log', entry => {
    if (state.view === 'logs' && !logConnections.hasSource) appendLog(entry);
  });

  state.socket.on('metrics:tick', d => {
    updateMetricCards(d);
    pushHistory(d.cpu, d.mem);
  });

  state.socket.on('mod:installing', d => {
    document.getElementById('install-progress').classList.remove('d-none');
    document.getElementById('install-log').textContent = `Installing ${d.name || d.workshopId}...\n`;
  });
  state.socket.on('mod:log', d => {
    const el = document.getElementById('install-log');
    if (el) el.textContent += d.data;
  });
  state.socket.on('mod:installed', d => {
    toast(`Mod installed: ${d.name}`);
    loadMods();
    document.getElementById('install-progress').classList.add('d-none');
  });
  state.socket.on('mod:error', d => toast(`Mod error: ${d.error}`, 'error'));

  state.socket.on('steamcmd:state', d => updateSteamCmdState(d));
  state.socket.on('steamcmd:log', entry => appendSteamCmdLog(entry));
  state.socket.on('steamcmd:done', d => {
    updateSteamCmdState(d);
    toast(d.exitCode === 0 ? 'SteamCMD session ready' : 'SteamCMD login failed', d.exitCode === 0 ? 'success' : 'error');
    if (state.view === 'mods') loadMods();
  });

  state.socket.on('install:log',  d => { if (state.view === 'dashboard') { /* could show in install log panel */ } });
  state.socket.on('install:done', d => toast(d.code === 0 ? 'Server installed/updated!' : 'Install failed (check logs)', d.code === 0 ? 'success' : 'error'));
  state.socket.on('update:done',  d => toast(d.code === 0 ? 'Server updated!' : 'Update failed', d.code === 0 ? 'success' : 'error'));
}

// ─── Server status helpers ────────────────────────────────────────────────────
function updateServerStatus(running, busy = false) {
  state.serverRunning = running;
  window.dispatchEvent(new CustomEvent('arma3:status', { detail: { running, busy } }));

  // ── Topbar badge ──────────────────────────────────────────────────────────
  const topDot  = document.querySelector('#server-status-badge .status-dot');
  const topText = document.getElementById('status-text');
  if (topDot)  topDot.className    = 'status-dot ' + (running ? 'online' : 'offline');
  if (topText) topText.textContent = running ? 'Online' : 'Offline';

  // Topbar mini-buttons
  const topStart   = document.getElementById('btn-start');
  const topStop    = document.getElementById('btn-stop');
  const topRestart = document.getElementById('btn-restart');
  if (topStart)   topStart.disabled   = running || busy;
  if (topStop)    topStop.disabled    = !running || busy;
  if (topRestart) topRestart.disabled = !running || busy;

  // ── Dashboard control card ────────────────────────────────────────────────
  const ring    = document.getElementById('dash-status-ring');
  const dashDot = document.getElementById('dash-status-dot');
  const dashTxt = document.getElementById('dash-status-text');
  const dashPid = document.getElementById('dash-pid-text');
  const dStart  = document.getElementById('dash-btn-start');
  const dStop   = document.getElementById('dash-btn-stop');
  const dRestart= document.getElementById('dash-btn-restart');

  if (ring) {
    ring.className = 'status-ring ' + (busy ? 'busy' : running ? 'online' : 'offline');
  }
  if (dashDot)  dashDot.className    = 'status-dot ' + (running ? 'online' : 'offline');
  if (dashTxt)  dashTxt.textContent  = busy ? 'Processing…' : running ? 'Online' : 'Offline';
  if (dashTxt)  dashTxt.style.color  = busy ? 'var(--warning)' : running ? 'var(--accent)' : 'var(--text-muted)';

  if (dStart)   dStart.disabled   = running || busy;
  if (dStop)    dStop.disabled    = !running || busy;
  if (dRestart) dRestart.disabled = !running || busy;

  // ── Info panel badge ──────────────────────────────────────────────────────
  const badge = document.getElementById('info-status');
  if (badge) {
    badge.textContent = running ? 'Online' : 'Offline';
    badge.className   = 'badge ' + (running ? 'bg-success' : 'bg-danger');
  }

  // PID label in control card
  if (dashPid) dashPid.textContent = '';
}

// ─── Action bar helper ────────────────────────────────────────────────────────
function setActionBar(visible, msg = 'Processing…') {
  const bar = document.getElementById('dash-action-bar');
  const txt = document.getElementById('dash-action-msg');
  if (!bar) return;
  if (visible) {
    bar.classList.remove('d-none');
    if (txt) txt.textContent = msg;
  } else {
    bar.classList.add('d-none');
  }
}

// ─── Server control buttons ───────────────────────────────────────────────────
function initServerControls() {
  // Shared handler used by both topbar and dashboard buttons
  async function doStart() {
    updateServerStatus(false, true);
    setActionBar(true, 'Starting server…');
    try { await POST('/api/server/start'); } catch (e) {
      updateServerStatus(false);
      setActionBar(false);
      toast(e.message, 'error');
    }
  }
  async function doStop() {
    updateServerStatus(true, true);
    setActionBar(true, 'Stopping server…');
    try { await POST('/api/server/stop'); } catch (e) {
      updateServerStatus(state.serverRunning);
      setActionBar(false);
      toast(e.message, 'error');
    }
  }
  async function doRestart() {
    updateServerStatus(true, true);
    setActionBar(true, 'Restarting server…');
    try { await POST('/api/server/restart'); } catch (e) {
      updateServerStatus(state.serverRunning);
      setActionBar(false);
      toast(e.message, 'error');
    }
  }

  // Topbar mini-buttons
  document.getElementById('btn-start').addEventListener('click',   doStart);
  document.getElementById('btn-stop').addEventListener('click',    doStop);
  document.getElementById('btn-restart').addEventListener('click', doRestart);

  // Dashboard big buttons (added lazily on initDashboard, but also listen here
  // in case dashboard is the first view)
  document.addEventListener('click', e => {
    if (e.target.closest('#dash-btn-start'))   doStart();
    if (e.target.closest('#dash-btn-stop'))    doStop();
    if (e.target.closest('#dash-btn-restart')) doRestart();
  });

  // Get initial status
  GET('/api/server/status').then(d => {
    updateServerStatus(d.running);
    const pid = document.getElementById('dash-pid-text');
    if (pid && d.pid) pid.textContent = `· PID ${d.pid}`;
    const infoPid  = document.getElementById('info-pid');
    const infoPort = document.getElementById('info-port');
    const joinAddress = document.getElementById('info-join-address');
    if (infoPid)  infoPid.textContent  = d.pid  || '—';
    if (infoPort) infoPort.textContent = d.port || '—';
    if (joinAddress) joinAddress.textContent = getJoinAddress(d);
  }).catch(() => {});
}

// ════════════════════════════════════════════════════════════════════════════════
// RCON CONSOLE
// ════════════════════════════════════════════════════════════════════════════════
function initRconControls() {
  if (initRconControls.bound) return;
  initRconControls.bound = true;

  function rconLog(line) {
    const out = document.getElementById('rcon-output');
    out.textContent += `${line}\n`;
    out.scrollTop = out.scrollHeight;
  }

  async function sendCommand() {
    const input = document.getElementById('rcon-command-input');
    const command = input.value.trim();
    if (!command) return;
    rconLog(`> ${command}`);
    input.value = '';
    try {
      const { response } = await POST('/api/server/rcon/command', { command });
      if (response) rconLog(response);
    } catch (e) { toast(e.message, 'error'); }
  }

  document.getElementById('btn-rcon-send').addEventListener('click', sendCommand);
  document.getElementById('rcon-command-input').addEventListener('keydown', e => {
    if (e.key === 'Enter') sendCommand();
  });
  document.getElementById('btn-rcon-refresh-players').addEventListener('click', loadRconPlayers);

  document.getElementById('rcon-players-table').addEventListener('click', async e => {
    const row = e.target.closest('tr[data-id]');
    if (!row) return;
    const playerId = Number(row.dataset.id);
    if (e.target.closest('.btn-rcon-kick')) {
      try { await POST('/api/server/rcon/kick', { playerId }); toast(`Kicked player ${playerId}`); loadRconPlayers(); }
      catch (err) { toast(err.message, 'error'); }
    }
    if (e.target.closest('.btn-rcon-ban')) {
      try { await POST('/api/server/rcon/ban', { playerId, minutes: 60 }); toast(`Banned player ${playerId} for 60 minutes`); loadRconPlayers(); }
      catch (err) { toast(err.message, 'error'); }
    }
  });
}

async function loadRconPlayers() {
  const tbody = document.querySelector('#rcon-players-table tbody');
  try {
    const players = await GET('/api/server/rcon/players');
    tbody.innerHTML = players.map(p => `
      <tr data-id="${p.id}">
        <td>${p.id}</td>
        <td>${p.name}${p.lobby ? ' <span class="badge bg-secondary">Lobby</span>' : ''}</td>
        <td>${p.ip}</td>
        <td>${p.ping}</td>
        <td class="text-truncate" style="max-width:180px">${p.guid}</td>
        <td class="text-end">
          <button class="btn btn-sm btn-outline-warning btn-rcon-kick" title="Kick"><i class="fa fa-user-slash"></i></button>
          <button class="btn btn-sm btn-outline-danger btn-rcon-ban" title="Ban 60 min"><i class="fa fa-gavel"></i></button>
        </td>
      </tr>`).join('') || '<tr><td colspan="6" class="text-center text-muted">No players connected</td></tr>';
  } catch (e) {
    tbody.innerHTML = `<tr><td colspan="6" class="text-center text-muted">${e.message}</td></tr>`;
  }
}

// ════════════════════════════════════════════════════════════════════════════════
// DASHBOARD
// ════════════════════════════════════════════════════════════════════════════════
function initDashboard() {
  // Fetch initial metrics
  GET('/api/metrics').then(renderMetrics).catch(() => {});

  document.getElementById('btn-copy-join-address').onclick = async () => {
    const address = document.getElementById('info-join-address').textContent.trim();
    if (!address || address === '—') return;
    try {
      await copyText(address);
      toast('Join address copied');
    } catch {
      toast('Could not copy join address', 'warning');
    }
  };

  // Active mods count
  GET('/api/mods').then(mods => {
    const active = mods.filter(m => m.active).length;
    document.getElementById('info-mods').textContent = `${active} active`;
  }).catch(() => {});

  initChart();
  loadSessions();
  document.getElementById('btn-refresh-sessions').onclick = () => loadSessions();

  document.getElementById('btn-install-server').onclick = async event => {
    const button = event.currentTarget;
    if (button.disabled) return;
    const original = button.innerHTML;
    button.disabled = true;
    button.innerHTML = '<i class="fa fa-spinner fa-spin me-1"></i>Starting...';
    try {
      await POST('/api/server/install');
      loadView('logs');
      toast('Server installation started…', 'success');
    } catch (e) { handleSteamLoginRequired(e); }
    finally {
      button.disabled = false;
      button.innerHTML = original;
    }
  };
}

async function loadSessions() {
  const body = document.getElementById('sessions-table-body');
  if (!body) return;
  try {
    const sessions = await GET('/api/metrics/sessions');
    if (!sessions.length) {
      body.innerHTML = '<tr><td colspan="6" class="text-muted">No sessions recorded yet.</td></tr>';
      return;
    }
    body.innerHTML = sessions.map(session => {
      const started = new Date(session.startedAt);
      const endMs = session.endedAt ? new Date(session.endedAt).getTime() : Date.now();
      const duration = formatDuration(endMs - started.getTime());
      const cpu = `${fmtPercent(session.avgCpuPercent)} / ${fmtPercent(session.peakCpuPercent)}`;
      const ram = `${fmtPercent(session.avgMemoryPercent)} / ${fmtPercent(session.peakMemoryPercent)}`;
      const ongoing = session.endedAt ? '' : ' <span class="badge bg-success ms-1">Live</span>';
      return `
        <tr>
          <td>${started.toLocaleString()}${ongoing}</td>
          <td>${duration}</td>
          <td>${cpu}</td>
          <td>${session.coresCapacity?.toFixed(1) ?? '—'}</td>
          <td>${ram}</td>
          <td class="text-end">
            <button class="btn btn-sm btn-outline-secondary" data-download-session="${escAttr(session.runId)}" title="Download CSV">
              <i class="fa fa-download"></i>
            </button>
          </td>
        </tr>`;
    }).join('');
    body.querySelectorAll('[data-download-session]').forEach(btn => {
      btn.onclick = () => downloadSessionCsv(btn.dataset.downloadSession);
    });
  } catch {
    body.innerHTML = '<tr><td colspan="6" class="text-danger">Failed to load sessions.</td></tr>';
  }
}

function formatDuration(ms) {
  if (!Number.isFinite(ms) || ms < 0) return '—';
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  return hours > 0 ? `${hours}h ${minutes}m` : minutes > 0 ? `${minutes}m ${seconds}s` : `${seconds}s`;
}

async function downloadSessionCsv(runId) {
  try {
    const res = await fetch(apiUrl(`/api/metrics/sessions/${encodeURIComponent(runId)}/csv`), {
      credentials: API_BASE ? 'include' : 'same-origin',
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const blob = await res.blob();
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `session-${runId}.csv`;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  } catch (e) {
    toast(`Could not download session CSV: ${e.message}`, 'error');
  }
}

function getJoinAddress(status) {
  const port = status?.port || '2302';
  const host = status?.joinHost || window.location.hostname || 'localhost';
  return `${host}:${port}`;
}

function renderMetrics(d) {
  setText('m-cpu', fmtPercent(d.cpu?.load));
  renderUsageMetric('m-mem', 'm-mem-detail', d.memory);
  const temperature = metricNumber(d.temperature);
  setText('m-temp', temperature === null ? '—' : temperature + '°C');
  const armaDisk = d.disk && d.disk.length
    ? (d.disk.find(dk => dk.mount === '/arma3') || d.disk.find(dk => dk.mount === '/') || d.disk[0])
    : null;
  if (d.trackedDiskUsage) {
    // Real bytes used by the server's own data (mods, missions, etc.), tracked by the file-index watchdog —
    // more meaningful than raw host-disk usage, which also counts unrelated data on the same volume.
    setText('m-disk', fmtBytes(d.trackedDiskUsage.bytes));
    setText('m-disk-detail', armaDisk ? `${fmtBytes(armaDisk.available)} free on host` : '—');
  } else if (armaDisk) {
    // Fall back to host-disk percentage until the watchdog completes its first scan.
    renderUsageMetric('m-disk', 'm-disk-detail', { ...armaDisk, total: armaDisk.size });
  }
}

function updateMetricCards(d) {
  setText('m-cpu', fmtPercent(d.cpu));
  setText('m-mem', fmtPercent(d.mem));
}

function pushHistory(cpu, mem) {
  state.cpuHistory.push(cpu);
  state.memHistory.push(mem);
  if (state.cpuHistory.length > MAX_HISTORY) { state.cpuHistory.shift(); state.memHistory.shift(); }
  if (state.chart) {
    state.chart.data.labels = state.cpuHistory.map((_, i) => i);
    state.chart.data.datasets[0].data = [...state.cpuHistory];
    state.chart.data.datasets[1].data = [...state.memHistory];
    state.chart.update('none');
  }
}

async function initChart() {
  const ctx = document.getElementById('chart-metrics');
  if (!ctx || state.chart) return;
  const { default: Chart } = await import('chart.js/auto');
  if (state.chart || !ctx.isConnected) return;
  state.chart = new Chart(ctx, {
    type: 'line',
    data: {
      labels: [],
      datasets: [
        {
          label: 'CPU %',
          data: [],
          borderColor: '#388bfd',
          backgroundColor: 'rgba(56,139,253,.1)',
          tension: .4,
          fill: true,
          pointRadius: 0,
        },
        {
          label: 'RAM %',
          data: [],
          borderColor: '#3fb950',
          backgroundColor: 'rgba(63,185,80,.1)',
          tension: .4,
          fill: true,
          pointRadius: 0,
        },
      ],
    },
    options: {
      animation: false,
      responsive: true,
      plugins: {
        legend: { labels: { color: '#7d8590', font: { size: 11 } } },
      },
      scales: {
        x: { display: false },
        y: {
          min: 0, max: 100,
          grid: { color: '#21262d' },
          ticks: { color: '#7d8590', callback: v => v + '%' },
        },
      },
    },
  });
}

// ════════════════════════════════════════════════════════════════════════════════
// MODS
// ════════════════════════════════════════════════════════════════════════════════
async function loadMods() {
  const container = document.getElementById('mod-list-container');
  initSteamCmdControls();
  loadCreatorDlcs();
  loadModlists();
  loadPresetFiles();
  container.innerHTML = '<div class="text-muted text-center py-4"><i class="fa fa-spinner fa-spin me-2"></i>Loading…</div>';

  // Check Steam credential status and show warning if anonymous
  GET('/api/steam/status').then(s => {
    const warn = document.getElementById('steam-anon-warning');
    if (warn) warn.classList.toggle('d-none', !s.requiresLogin);
    updateSteamCredentialSummary(s);
    if (s.login) updateSteamCmdState(s.login);
  }).catch(() => {});
  try {
    const mods = await GET('/api/mods');
    renderModList(mods);
  } catch (e) {
    container.innerHTML = `<div class="text-danger text-center py-4">${e.message}</div>`;
  }

  // Single mod install
  document.getElementById('btn-install-mod').onclick = async () => {
    const id = document.getElementById('mod-id-input').value.trim();
    if (!id || !/^\d+$/.test(id)) { toast('Enter a valid Workshop ID', 'error'); return; }
    try {
      const result = await POST('/api/mods/install', { workshopId: id });
      toast(result.queued ? `Mod ${id} queued for install` : `Mod ${id} is already installed`);
      document.getElementById('mod-id-input').value = '';
    } catch (e) { handleSteamLoginRequired(e); }
  };

  // HTML preset
  document.getElementById('btn-load-preset').onclick = async () => {
    const fileInput = document.getElementById('preset-file');
    if (!fileInput.files.length) { toast('Select an HTML preset file first', 'warning'); return; }
    const form = new FormData();
    form.append('preset', fileInput.files[0]);
    try {
      const res = await fetch(apiUrl('/api/mods/preset'), { method: 'POST', body: form, credentials: API_BASE ? 'include' : 'same-origin' });
      const data = await res.json();
      if (!res.ok) throw new Error(data.error);
      showPresetPreview(data.mods, data.savedPath);
      loadPresetFiles();
    } catch (e) { toast(e.message, 'error'); }
  };

  document.getElementById('btn-refresh-mods').onclick = () => loadMods();
}

async function loadCreatorDlcs() {
  const container = document.getElementById('creator-dlc-container');
  if (!container) return;
  container.innerHTML = '<div class="text-muted py-2"><i class="fa fa-spinner fa-spin me-2"></i>Checking Creator DLCs...</div>';
  try {
    const dlcs = await GET('/api/creator-dlcs');
    renderCreatorDlcs(dlcs);
  } catch (e) {
    container.innerHTML = `<div class="text-danger py-2">${escHtml(e.message)}</div>`;
  }
  const refresh = document.getElementById('btn-refresh-cdlcs');
  if (refresh) refresh.onclick = loadCreatorDlcs;
}

function renderCreatorDlcs(dlcs) {
  const container = document.getElementById('creator-dlc-container');
  if (!dlcs.length) {
    container.innerHTML = '<div class="text-muted py-2">No Creator DLC definitions configured.</div>';
    return;
  }

  container.innerHTML = `
    <table class="mod-table">
      <thead><tr>
        <th>Name</th><th>Status</th><th class="text-center" style="width:90px">Active</th>
      </tr></thead>
      <tbody>
        ${dlcs.map(dlc => `
          <tr>
            <td>
              <div class="mod-name">${escHtml(dlc.name)}</div>
              <span class="mod-id">${escHtml(dlc.folder)}</span>
            </td>
            <td>
              <span class="badge ${dlc.available ? 'bg-success' : 'bg-secondary'}">
                ${dlc.available ? 'Available' : 'Not installed'}
              </span>
            </td>
            <td class="text-center">
              <div class="form-check form-switch d-flex justify-content-center mb-0">
                <input class="form-check-input" type="checkbox" data-cdlc-id="${escAttr(dlc.id)}" ${dlc.active ? 'checked' : ''} ${dlc.available ? '' : 'disabled'} title="Enable/Disable Creator DLC" />
              </div>
            </td>
          </tr>`).join('')}
      </tbody>
    </table>
    <div class="small text-muted mt-2">Only available DLC folders can be enabled. Enabled DLCs are added to the server startup -mod list.</div>`;

  container.querySelectorAll('[data-cdlc-id]').forEach(toggle => {
    toggle.onchange = async () => {
      try {
        await PUT(`/api/creator-dlcs/${encodeURIComponent(toggle.dataset.cdlcId)}`, { active: toggle.checked });
        toast(toggle.checked ? 'Creator DLC enabled' : 'Creator DLC disabled');
        loadStartupSettings();
      } catch (e) {
        toast(e.message, 'error');
        toggle.checked = !toggle.checked;
      }
    };
  });
}

async function loadPresetFiles() {
  const select = document.getElementById('saved-preset-select');
  if (!select) return;
  try {
    const files = await GET('/api/mods/preset-files');
    select.innerHTML = '<option value="">Saved presets on server</option>' + files.map(file =>
      `<option value="${escAttr(file.path)}">${escHtml(file.name)} (${fmtBytes(file.size)})</option>`).join('');

    const loadSaved = document.getElementById('btn-load-saved-preset');
    if (loadSaved) {
      loadSaved.onclick = async () => {
        if (!select.value) { toast('Select a saved preset first', 'warning'); return; }
        try {
          const data = await POST('/api/mods/preset-files/load', { path: select.value });
          showPresetPreview(data.mods, data.savedPath);
          toast('Server preset loaded');
        } catch (e) { toast(e.message, 'error'); }
      };
    }

    const deleteSaved = document.getElementById('btn-delete-saved-preset');
    if (deleteSaved) deleteSaved.onclick = async () => {
      if (!select.value) { toast('Select a saved preset first', 'warning'); return; }
      if (!confirm('Delete the selected HTML preset? Saved modlists stay available.')) return;
      try {
        await DELETE(`/api/mods/preset-files?path=${encodeURIComponent(select.value)}`);
        toast('Preset file deleted');
        loadPresetFiles();
      } catch (e) { toast(e.message, 'error'); }
    };
  } catch (e) {
    select.innerHTML = `<option value="">${escHtml(e.message)}</option>`;
  }
}

function routeFromHash() {
  const raw = location.hash.replace(/^#/, '');
  const separator = raw.indexOf('/');
  const view = separator < 0 ? raw : raw.slice(0, separator);
  const encodedPath = separator < 0 ? '' : raw.slice(separator + 1);
  let filePath = '';
  try { filePath = decodeURIComponent(encodedPath); } catch { filePath = ''; }
  return { view, filePath };
}

async function openSteamCmdModal(prefillUser) {
  initSteamCmdControls();
  const modalEl = document.getElementById('steamcmd-modal');
  state.steamCmdModal = state.steamCmdModal || new bootstrap.Modal(modalEl);
  state.steamCmdModal.show();
  try {
    const [steamStatus, loginState] = await Promise.all([
      GET('/api/steam/status'),
      GET('/api/steamcmd/login'),
    ]);
    const user = prefillUser || steamStatus.user;
    if (user && user !== 'anonymous') {
      document.getElementById('steamcmd-user').value = user;
    }
    updateSteamCredentialSummary(steamStatus);
    updateSteamCmdState(loginState);
    renderSteamCmdLogs(loginState.logs || []);
  } catch (e) { toast(e.message, 'error'); }
}

function handleSteamLoginRequired(e) {
  if (e.code !== 'steam_login_required') {
    toast(e.message, 'error');
    return;
  }
  toast('SteamCMD necesita iniciar sesion primero. Acepta Steam Guard y vuelve a intentar.', 'warning');
  openSteamCmdModal(e.data?.username);
}

function initSteamCmdControls() {
  const openBtn = document.getElementById('btn-open-steamcmd');
  if (!openBtn) return;

  openBtn.onclick = () => openSteamCmdModal();
  document.getElementById('btn-steamcmd-start').onclick = async () => {
    const username = document.getElementById('steamcmd-user').value.trim();
    const password = document.getElementById('steamcmd-pass').value;
    if (!username) { toast('Steam username required', 'warning'); return; }
    document.getElementById('steamcmd-log').textContent = '';
    try {
      const data = await POST('/api/steamcmd/login/start', { username, password });
      updateSteamCmdState(data);
      pollSteamCmdLogin();
    } catch (e) { toast(e.message, 'error'); }
  };

  document.getElementById('btn-steamcmd-send').onclick = async () => {
    const input = document.getElementById('steamcmd-code').value.trim();
    if (!input) return;
    try {
      await POST('/api/steamcmd/login/input', { input });
      document.getElementById('steamcmd-code').value = '';
      pollSteamCmdLogin();
    } catch (e) { toast(e.message, 'error'); }
  };

  document.getElementById('steamcmd-code').onkeydown = e => {
    if (e.key === 'Enter') document.getElementById('btn-steamcmd-send').click();
  };

  document.getElementById('btn-steamcmd-cancel').onclick = async () => {
    try { await POST('/api/steamcmd/login/cancel'); }
    catch (e) { toast(e.message, 'error'); }
  };
}

async function pollSteamCmdLogin() {
  for (let i = 0; i < 120; i++) {
    await new Promise(resolve => setTimeout(resolve, 1000));
    const data = await GET('/api/steamcmd/login').catch(() => null);
    if (!data) return;
    updateSteamCmdState(data);
    renderSteamCmdLogs(data.logs || []);
    if (!data.running) {
      if (data.exitCode === 0) {
        toast('SteamCMD session ready', 'success');
        GET('/api/steam/status').then(updateSteamCredentialSummary).catch(() => {});
      }
      return;
    }
  }
}

function updateSteamCredentialSummary(s) {
  const badge = document.getElementById('steamcmd-status-badge');
  const text = document.getElementById('steamcmd-status-text');
  if (!badge || !text) return;
  if (s.requiresLogin) {
    badge.textContent = 'Login required';
    badge.className = 'badge bg-warning text-dark me-2';
    text.textContent = s.user && s.user !== 'anonymous'
      ? `Login required for ${s.user} before installing.`
      : 'Login before installing or updating mods/server files.';
  } else if (s.hasCredentials) {
    badge.textContent = 'Configured';
    badge.className = 'badge bg-success me-2';
    text.textContent = `Using Steam account ${s.user}`;
  } else {
    badge.textContent = 'Required';
    badge.className = 'badge bg-warning text-dark me-2';
    text.textContent = 'Login before installing or updating mods/server files.';
  }
}

function updateSteamCmdState(d = {}) {
  const inputPanel = document.getElementById('steamcmd-input-panel');
  const modalState = document.getElementById('steamcmd-modal-state');
  const startBtn = document.getElementById('btn-steamcmd-start');
  const cancelBtn = document.getElementById('btn-steamcmd-cancel');
  const badge = document.getElementById('steamcmd-status-badge');
  const text = document.getElementById('steamcmd-status-text');

  if (inputPanel) inputPanel.classList.toggle('d-none', !d.awaitingInput);
  if (startBtn) startBtn.disabled = !!d.running;
  if (cancelBtn) cancelBtn.disabled = !d.running;

  if (modalState) {
    if (d.awaitingInput) modalState.textContent = 'Waiting for Steam Guard or SteamCMD input';
    else if (d.running) modalState.textContent = `Logging in${d.username ? ` as ${d.username}` : ''}...`;
    else if (d.exitCode === 0) modalState.textContent = 'SteamCMD session ready';
    else if (d.exitCode !== null && d.exitCode !== undefined) modalState.textContent = `SteamCMD exited with code ${d.exitCode}`;
    else modalState.textContent = 'Idle';
  }

  if (badge && text && (d.running || d.awaitingInput || d.exitCode !== null && d.exitCode !== undefined)) {
    if (d.awaitingInput) {
      badge.textContent = 'Steam Guard';
      badge.className = 'badge bg-warning text-dark me-2';
      text.textContent = 'SteamCMD is waiting for your code.';
    } else if (d.running) {
      badge.textContent = 'Running';
      badge.className = 'badge bg-info me-2';
      text.textContent = `SteamCMD login running${d.username ? ` for ${d.username}` : ''}.`;
    } else if (d.exitCode === 0) {
      badge.textContent = 'Ready';
      badge.className = 'badge bg-success me-2';
      text.textContent = 'SteamCMD authenticated successfully.';
    } else {
      badge.textContent = 'Failed';
      badge.className = 'badge bg-danger me-2';
      text.textContent = d.lastError || 'SteamCMD login did not complete.';
    }
  }
}

async function loadSettings() {
  try {
    const [account, auth] = await Promise.all([
      GET('/api/settings/account'),
      GET('/api/auth/check'),
    ]);
    setText('settings-steam-status', auth.steamId ? `Linked Steam ID: ${auth.steamId}` : 'No Steam session linked to this browser session.');
    const username = document.getElementById('settings-username');
    if (username) username.value = account.username || '';
  } catch (e) { toast(e.message, 'error'); }

  const steamLink = document.getElementById('btn-link-steam');
  if (steamLink) steamLink.href = apiUrl('/auth/steam');

  const saveBtn = document.getElementById('btn-save-account');
  if (saveBtn) saveBtn.onclick = async () => {
    const username = document.getElementById('settings-username').value.trim();
    const currentPassword = document.getElementById('settings-current-password').value;
    const newPassword = document.getElementById('settings-new-password').value;
    if (!currentPassword) { toast('Current password required', 'warning'); return; }
    try {
      const updated = await PUT('/api/settings/account', { username, currentPassword, newPassword });
      document.getElementById('settings-current-password').value = '';
      document.getElementById('settings-new-password').value = '';
      toast(`Account updated: ${updated.username}`, 'success');
    } catch (e) { toast(e.message, 'error'); }
  };

  setupFactoryReset();
  setupRestartApp();

}

function setupRestartApp() {
  const openBtn = document.getElementById('btn-open-restart-app');
  const modalEl = document.getElementById('restart-app-modal');
  const password = document.getElementById('restart-app-password');
  const confirmBtn = document.getElementById('btn-confirm-restart-app');
  if (!openBtn || !modalEl || !password || !confirmBtn) return;

  state.restartAppModal = state.restartAppModal || new bootstrap.Modal(modalEl, { backdrop: 'static', keyboard: false });
  openBtn.disabled = state.serverRunning;
  openBtn.title = state.serverRunning ? 'Stop the game server before restarting the panel' : '';
  openBtn.onclick = () => {
    password.value = '';
    document.getElementById('restart-app-form').classList.remove('d-none');
    document.getElementById('restart-app-progress').classList.add('d-none');
    modalEl.querySelectorAll('[data-bs-dismiss="modal"]').forEach(button => { button.disabled = false; });
    confirmBtn.disabled = false;
    state.restartAppModal.show();
    modalEl.addEventListener('shown.bs.modal', () => password.focus(), { once: true });
  };
  confirmBtn.onclick = async () => {
    if (confirmBtn.disabled) return;
    confirmBtn.disabled = true;
    modalEl.querySelectorAll('[data-bs-dismiss="modal"]').forEach(button => { button.disabled = true; });
    try {
      await POST('/api/system/restart', { currentPassword: password.value });
      document.getElementById('restart-app-form').classList.add('d-none');
      document.getElementById('restart-app-progress').classList.remove('d-none');
      waitForRestart();
    } catch (e) {
      toast(e.message, 'error');
      modalEl.querySelectorAll('[data-bs-dismiss="modal"]').forEach(button => { button.disabled = false; });
      confirmBtn.disabled = false;
    }
  };
}

async function waitForRestart() {
  let observedRestart = false;
  for (let attempt = 0; attempt < 60; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 2000));
    try {
      const response = await fetch(apiUrl('/api/health'), { cache: 'no-store' });
      if (!response.ok) { observedRestart = true; continue; }
      if (observedRestart) { window.location.assign('/'); return; }
    } catch { observedRestart = true; }
  }
  toast('The panel is still restarting. Reload the page in a moment.', 'warning');
}

function setupFactoryReset() {
  const openBtn = document.getElementById('btn-open-factory-reset');
  const modalEl = document.getElementById('factory-reset-modal');
  const password = document.getElementById('factory-reset-password');
  const confirmation = document.getElementById('factory-reset-confirmation');
  const confirmBtn = document.getElementById('btn-confirm-factory-reset');
  if (!openBtn || !modalEl || !password || !confirmation || !confirmBtn) return;

  state.factoryResetModal = state.factoryResetModal || new bootstrap.Modal(modalEl, { backdrop: 'static', keyboard: false });
  const updateReady = () => {
    confirmBtn.disabled = !password.value || confirmation.value !== 'RESET ALL ARMA3 DATA';
  };
  password.oninput = updateReady;
  confirmation.oninput = updateReady;
  openBtn.disabled = state.serverRunning;
  openBtn.title = state.serverRunning ? 'Stop the game server before factory reset' : '';
  openBtn.onclick = () => {
    password.value = '';
    confirmation.value = '';
    document.getElementById('factory-reset-form').classList.remove('d-none');
    document.getElementById('factory-reset-progress').classList.add('d-none');
    modalEl.querySelectorAll('[data-bs-dismiss="modal"]').forEach(button => { button.disabled = false; });
    updateReady();
    state.factoryResetModal.show();
    modalEl.addEventListener('shown.bs.modal', () => password.focus(), { once: true });
  };
  confirmBtn.onclick = async () => {
    if (confirmBtn.disabled) return;
    confirmBtn.disabled = true;
    modalEl.querySelectorAll('[data-bs-dismiss="modal"]').forEach(button => { button.disabled = true; });
    try {
      await POST('/api/system/factory-reset', {
        currentPassword: password.value,
        confirmation: confirmation.value,
      });
      document.getElementById('factory-reset-form').classList.add('d-none');
      document.getElementById('factory-reset-progress').classList.remove('d-none');
      waitForFactoryReset();
    } catch (e) {
      toast(e.message, 'error');
      modalEl.querySelectorAll('[data-bs-dismiss="modal"]').forEach(button => { button.disabled = false; });
      updateReady();
    }
  };
}

async function waitForFactoryReset() {
  let observedRestart = false;
  for (let attempt = 0; attempt < 90; attempt++) {
    await new Promise(resolve => setTimeout(resolve, 2000));
    try {
      const response = await fetch(apiUrl('/api/health'), { cache: 'no-store' });
      if (!response.ok) { observedRestart = true; continue; }
      if (observedRestart) { window.location.assign('/'); return; }
    } catch { observedRestart = true; }
  }
  toast('The reset is still running. Reload the page after the API restarts.', 'warning');
}

async function resetSteamCmd(btn) {
  if (!confirm('Reset SteamCMD cache and run first-time setup?')) {
    toast('SteamCMD reset cancelled', 'warning');
    return;
  }

  const original = btn.innerHTML;
  btn.disabled = true;
  btn.innerHTML = '<i class="fa fa-spinner fa-spin me-1"></i>Resetting...';
  toast('Starting SteamCMD reset...', 'warning');

  try {
    const res = await POST('/api/steamcmd/factory-reset');
    toast(res.message || 'SteamCMD factory setup started. Check logs for progress.', 'success');
  } catch (e) {
    toast(e.message, 'error');
  } finally {
    btn.disabled = false;
    btn.innerHTML = original;
  }
}

function appendSteamCmdLog(entry) {
  const el = document.getElementById('steamcmd-log');
  if (!el) return;
  el.appendChild(createLogLine(entry, false));
  el.scrollTop = el.scrollHeight;
}

function renderSteamCmdLogs(logs) {
  const el = document.getElementById('steamcmd-log');
  if (!el) return;
  el.textContent = '';
  logs.forEach(appendSteamCmdLog);
}

async function loadModlists() {
  const container = document.getElementById('modlist-container');
  if (!container) return;
  container.innerHTML = '<div class="text-muted py-2">Loading...</div>';
  try {
    const state = await GET('/api/modlists');
    renderModlists(state);
  } catch (e) {
    container.innerHTML = `<div class="text-danger py-2">${e.message}</div>`;
  }
  const refresh = document.getElementById('btn-refresh-modlists');
  if (refresh) refresh.onclick = loadModlists;
}

function renderModlists(stateData) {
  const container = document.getElementById('modlist-container');
  const lists = stateData.lists || [];
  if (!lists.length) {
    container.innerHTML = '<div class="text-muted py-2">No saved modlists yet. Import an A3 Launcher HTML preset and save it.</div>';
    return;
  }
  container.innerHTML = lists.map(list => {
    const active = stateData.activeModlistId === list.id;
    return `
      <div class="modlist-item ${active ? 'active' : ''}">
        <div class="modlist-main">
          <div class="mod-name">${escHtml(list.name)} ${active ? '<span class="badge bg-success ms-2">Active</span>' : ''}</div>
          <div class="mod-id">${list.mods.length} mods</div>
        </div>
        <div class="modlist-actions">
          <button class="btn btn-sm ${active ? 'btn-outline-warning' : 'btn-outline-primary'}" ${active ? 'data-deactivate-modlist' : 'data-activate-modlist'}="${list.id}" title="${active ? 'Deactivate' : 'Activate'} when server is stopped"><i class="fa fa-toggle-${active ? 'off' : 'on'} me-1"></i>${active ? 'Deactivate' : 'Activate'}</button>
          <button class="btn btn-sm btn-outline-secondary" data-install-missing="${list.id}" title="Install missing mods"><i class="fa fa-download me-1"></i>Missing</button>
          <button class="btn btn-sm btn-outline-danger" data-delete-modlist="${list.id}" title="Delete saved modlist"><i class="fa fa-trash"></i></button>
          <button class="btn btn-sm btn-danger" data-delete-modlist-mods="${list.id}" title="Delete this modlist and its installed mods"><i class="fa fa-broom me-1"></i>Mods</button>
        </div>
      </div>`;
  }).join('');

  container.querySelectorAll('[data-activate-modlist]').forEach(btn => {
    btn.onclick = async () => {
      try {
        const result = await PUT(`/api/modlists/${btn.dataset.activateModlist}/activate`, {});
        const missing = result.missing || [];
        toast(missing.length ? `Modlist active. ${missing.length} mods missing.` : 'Modlist active');
        loadModlists();
        loadMods();
      } catch (e) { toast(e.message, 'error'); }
    };
  });

  container.querySelectorAll('[data-deactivate-modlist]').forEach(btn => {
    btn.onclick = async () => {
      try {
        await PUT(`/api/modlists/${btn.dataset.deactivateModlist}/deactivate`, {});
        toast('Modlist deactivated');
        loadModlists();
        loadMods();
      } catch (e) { toast(e.message, 'error'); }
    };
  });

  container.querySelectorAll('[data-install-missing]').forEach(btn => {
    btn.onclick = async () => {
      try {
        const { queued } = await POST(`/api/modlists/${btn.dataset.installMissing}/install-missing`);
        toast(queued ? `${queued} missing mods queued` : 'All mods are already installed');
      } catch (e) { toast(e.message, 'error'); }
    };
  });

  container.querySelectorAll('[data-delete-modlist]').forEach(btn => {
    btn.onclick = async () => {
      if (!confirm('Delete this saved modlist? Installed mods stay on disk.')) return;
      try {
        await DELETE(`/api/modlists/${btn.dataset.deleteModlist}`);
        toast('Modlist deleted');
        loadModlists();
      } catch (e) { toast(e.message, 'error'); }
    };
  });

  container.querySelectorAll('[data-delete-modlist-mods]').forEach(btn => {
    btn.onclick = async () => {
      if (!confirm('Delete this modlist AND all installed mods that belong to it? Stop the game server first.')) return;
      try {
        const result = await DELETE(`/api/modlists/${btn.dataset.deleteModlistMods}?deleteMods=true`);
        toast(`Modlist deleted. ${result.deletedMods} installed mods removed.`);
        loadModlists();
        loadMods();
      } catch (e) { toast(e.message, 'error'); }
    };
  });
}

function renderModList(mods) {
  const container = document.getElementById('mod-list-container');
  if (!mods.length) {
    container.innerHTML = '<div class="text-muted text-center py-4">No mods installed.</div>';
    return;
  }
  const rows = mods.map(m => `
    <tr>
      <td>
        <div class="mod-name">${escHtml(m.name)}</div>
        ${m.workshopId
          ? `<a class="mod-id" href="https://steamcommunity.com/sharedfiles/filedetails/?id=${m.workshopId}" target="_blank" rel="noopener">
               <i class="fa fa-steam me-1" style="font-size:.75rem"></i>${m.workshopId}
             </a>`
          : '<span class="mod-id text-muted">local</span>'}
      </td>
      <td class="text-center">
        <div class="form-check form-switch d-flex justify-content-center mb-0">
          <input class="form-check-input" type="checkbox" data-mod-id="${m.id}" ${m.active ? 'checked' : ''} title="Enable/Disable" />
        </div>
      </td>
      <td class="text-end">
        <button class="btn btn-sm btn-outline-danger" data-del-mod="${m.id}" title="Remove mod">
          <i class="fa fa-trash"></i>
        </button>
      </td>
    </tr>`).join('');

  container.innerHTML = `
    <table class="mod-table">
      <thead><tr>
        <th>Name</th><th class="text-center" style="width:80px">Active</th><th style="width:60px"></th>
      </tr></thead>
      <tbody>${rows}</tbody>
    </table>`;

  // Toggle active
  container.querySelectorAll('[data-mod-id]').forEach(toggle => {
    toggle.addEventListener('change', async () => {
      try {
        await PUT(`/api/mods/${toggle.dataset.modId}`, { active: toggle.checked });
        toast(toggle.checked ? 'Mod enabled' : 'Mod disabled');
      } catch (e) { toast(e.message, 'error'); toggle.checked = !toggle.checked; }
    });
  });

  // Delete
  container.querySelectorAll('[data-del-mod]').forEach(btn => {
    btn.addEventListener('click', async () => {
      if (!confirm('Remove this mod?')) return;
      try {
        await DELETE(`/api/mods/${btn.dataset.delMod}`);
        toast('Mod removed');
        loadMods();
      } catch (e) { toast(e.message, 'error'); }
    });
  });
}

function showPresetPreview(mods, savedPath = '') {
  state.presetMods = mods;
  const panel = document.getElementById('preset-preview');
  panel.classList.remove('d-none');
  const presetName = document.getElementById('preset-name');
  if (presetName) presetName.textContent = savedPath ? `(${savedPath.split('/').pop()})` : '';
  const nameInput = document.getElementById('preset-save-name');
  if (nameInput && !nameInput.value) nameInput.value = `Modlist ${new Date().toLocaleDateString()}`;
  const list = document.getElementById('preset-mod-list');
  list.innerHTML = mods.map((m, i) => `
    <div class="preset-item">
      <input type="checkbox" class="form-check-input" id="pm${i}" data-workshop-id="${m.workshopId}" data-name="${escHtml(m.name)}" checked />
      <label for="pm${i}" class="flex-1" style="cursor:pointer">
        <span class="fw-500">${escHtml(m.name)}</span>
        <span class="text-muted ms-2 small">${m.workshopId}</span>
        <span class="badge ${m.installed ? 'bg-success' : 'bg-secondary'} ms-2">${m.installed ? 'Installed' : 'Missing'}</span>
      </label>
    </div>`).join('');

  document.getElementById('btn-preset-select-all').onclick = () => {
    list.querySelectorAll('input[type=checkbox]').forEach(cb => cb.checked = true);
  };

  document.getElementById('btn-save-modlist').onclick = async () => {
    const selected = [...list.querySelectorAll('input:checked')].map(cb => ({
      workshopId: cb.dataset.workshopId,
      name: cb.dataset.name,
    }));
    if (!selected.length) { toast('No mods selected', 'warning'); return; }
    try {
      const saved = await POST('/api/modlists', {
        name: document.getElementById('preset-save-name').value.trim(),
        mods: selected,
      });
      toast(`Modlist saved: ${saved.name}`);
      loadModlists();
    } catch (e) { toast(e.message, 'error'); }
  };

  document.getElementById('btn-install-preset').onclick = async () => {
    const selected = [...list.querySelectorAll('input:checked')].map(cb => ({
      workshopId: cb.dataset.workshopId,
      name: cb.dataset.name,
    }));
    if (!selected.length) { toast('No mods selected', 'warning'); return; }
    try {
      const { queued, alreadyInstalled } = await POST('/api/mods/install-batch', { mods: selected });
      toast(queued ? `${queued} missing mods queued; ${alreadyInstalled || 0} already installed` : 'All selected mods are already installed');
      panel.classList.add('d-none');
    } catch (e) { handleSteamLoginRequired(e); }
  };
}

// ════════════════════════════════════════════════════════════════════════════════
// FILE MANAGER
// ════════════════════════════════════════════════════════════════════════════════
async function loadFiles(dir, pushHistory = false, refresh = false) {
  const container = document.getElementById('file-list');
  container.innerHTML = '<div class="text-muted"><i class="fa fa-spinner fa-spin me-2"></i>Loading…</div>';
  try {
    const params = new URLSearchParams();
    if (dir) params.set('path', dir);
    if (refresh) params.set('refresh', 'true');
    const query = params.toString();
    const data = await GET('/api/files' + (query ? `?${query}` : ''));
    state.currentFilePath = data.path || '';
    state.fileItems = data.items || [];
    renderBreadcrumb(data.path || '', data.rootName || 'Arma 3 Server');
    renderCurrentFileView();
    if (pushHistory) {
      const route = data.path ? `#files/${encodeURIComponent(data.path)}` : '#files';
      if (location.hash !== route) history.pushState({ view: 'files', filePath: data.path || '' }, '', route);
    }
  } catch (e) {
    container.innerHTML = `<div class="text-danger">${e.message}</div>`;
  }

  // File upload
  document.getElementById('file-upload-input').onchange = async function () {
    await uploadFiles(this.files);
    this.value = '';
  };
  document.getElementById('btn-refresh-files').onclick = () => loadFiles(state.currentFilePath, false, true);
  initFileDropZone();
}

function renderCurrentFileView() {
  renderFileTable(state.fileItems, state.currentFilePath);
}

async function uploadFiles(files) {
  const fileList = [...files].filter(file => file && file.name);
  if (!fileList.length) return;

  const form = new FormData();
  fileList.forEach(file => form.append('files', file));

  try {
    const res = await fetch(apiUrl(`/api/files/upload?dir=${encodeURIComponent(state.currentFilePath || '')}`), {
      method: 'POST',
      body: form,
      credentials: API_BASE ? 'include' : 'same-origin',
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
    toast(fileList.length === 1 ? `Uploaded ${fileList[0].name}` : `Uploaded ${fileList.length} files`);
    loadFiles(state.currentFilePath);
  } catch (e) {
    toast(e.message, 'error');
  }
}

function initFileDropZone() {
  const zone = document.getElementById('file-drop-zone');
  if (!zone || zone.dataset.ready === 'true') return;
  zone.dataset.ready = 'true';

  ['dragenter', 'dragover'].forEach(eventName => {
    zone.addEventListener(eventName, e => {
      e.preventDefault();
      e.stopPropagation();
      zone.classList.add('drag-over');
    });
  });

  ['dragleave', 'dragend'].forEach(eventName => {
    zone.addEventListener(eventName, e => {
      e.preventDefault();
      e.stopPropagation();
      zone.classList.remove('drag-over');
    });
  });

  zone.addEventListener('drop', async e => {
    e.preventDefault();
    e.stopPropagation();
    zone.classList.remove('drag-over');
    await uploadFiles(e.dataTransfer?.files || []);
  });
}

function renderBreadcrumb(relPath, rootName = 'Arma 3 Server') {
  const parts = String(relPath || '').replace(/\\/g, '/').split('/').filter(Boolean);
  const list  = document.getElementById('breadcrumb-list');
  const root = parts.length
    ? `<li class="breadcrumb-item"><a href="#" data-nav-path="">${escHtml(rootName)}</a></li>`
    : `<li class="breadcrumb-item active">${escHtml(rootName)}</li>`;
  list.innerHTML = root + parts.map((p, i) => {
    const pPath = parts.slice(0, i + 1).join('/');
    return i < parts.length - 1
      ? `<li class="breadcrumb-item"><a href="#" data-nav-path="${escAttr(pPath)}">${escHtml(p)}</a></li>`
      : `<li class="breadcrumb-item active">${escHtml(p)}</li>`;
  }).join('');

  list.querySelectorAll('[data-nav-path]').forEach(a => {
    a.addEventListener('click', e => { e.preventDefault(); loadFiles(a.dataset.navPath, true); });
  });
}

function sortFileItems(items) {
  const dir = state.fileSortDir === 'desc' ? -1 : 1;
  const key = state.fileSortKey;
  return [...items].sort((a, b) => {
    if (a.isDir !== b.isDir) return a.isDir ? -1 : 1; // folders always first, regardless of sort column
    let av, bv;
    if (key === 'size') { av = a.size; bv = b.size; }
    else if (key === 'created') { av = a.created || ''; bv = b.created || ''; }
    else if (key === 'mtime') { av = a.modified || ''; bv = b.modified || ''; }
    else { av = a.name.toLowerCase(); bv = b.name.toLowerCase(); }
    if (av < bv) return -1 * dir;
    if (av > bv) return 1 * dir;
    return 0;
  });
}

function renderFileTable(items, currentPath) {
  const container = document.getElementById('file-list');
  if (!items.length) { container.innerHTML = '<div class="text-muted py-2">Empty directory</div>'; return; }

  const columns = [
    { key: 'name', label: 'Name' },
    { key: 'created', label: 'Created' },
    { key: 'mtime', label: 'Modified' },
    { key: 'size', label: 'Size' },
  ];
  const arrow = (key) => state.fileSortKey === key ? `<span class="sort-arrow">${state.fileSortDir === 'desc' ? '▼' : '▲'}</span>` : '';
  const head = columns.map(c => `<th data-sort="${c.key}">${c.label}${arrow(c.key)}</th>`).join('') + '<th></th>';

  const rows = sortFileItems(items).map(item => {
    const icon = item.isDir ? 'folder' : getFileIcon(item.name);
    return `
      <tr class="file-item ${item.isDir ? 'is-dir' : ''}" data-path="${escAttr(item.path)}" data-is-dir="${item.isDir}">
        <td><i class="fa fa-${icon} fi-icon me-2"></i>${escHtml(item.name)}</td>
        <td>${fmtDate(item.created)}</td>
        <td>${fmtDate(item.modified)}</td>
        <td>${fmtBytes(item.size)}</td>
        <td class="fi-actions">
          ${!item.isDir ? `<button class="btn btn-sm btn-outline-secondary btn-icon" data-edit="${escAttr(item.path)}" title="Edit"><i class="fa fa-pen-to-square"></i></button>` : ''}
          <button class="btn btn-sm btn-outline-secondary btn-icon" data-rename="${escAttr(item.path)}" data-name="${escAttr(item.name)}" title="Rename"><i class="fa fa-pencil"></i></button>
          <button class="btn btn-sm btn-outline-danger btn-icon" data-del="${escAttr(item.path)}" title="Delete"><i class="fa fa-trash"></i></button>
        </td>
      </tr>`;
  }).join('');

  container.innerHTML = `
    <div class="file-table-wrap">
      <table class="mod-table file-table">
        <colgroup><col><col class="file-date-col"><col class="file-date-col"><col class="file-size-col"><col class="file-actions-col"></colgroup>
        <thead><tr>${head}</tr></thead>
        <tbody>${rows}</tbody>
      </table>
    </div>`;

  container.querySelectorAll('th[data-sort]').forEach(th => {
    th.addEventListener('click', () => {
      const key = th.dataset.sort;
      if (state.fileSortKey === key) state.fileSortDir = state.fileSortDir === 'asc' ? 'desc' : 'asc';
      else { state.fileSortKey = key; state.fileSortDir = 'asc'; }
      renderFileTable(items, currentPath);
    });
  });

  container.querySelectorAll('tr.file-item.is-dir').forEach(el => {
    el.addEventListener('click', e => {
      if (!e.target.closest('.fi-actions')) loadFiles(el.dataset.path, true);
    });
  });

  container.querySelectorAll('[data-edit]').forEach(btn => {
    btn.addEventListener('click', e => { e.stopPropagation(); openFileEditor(btn.dataset.edit); });
  });

  container.querySelectorAll('[data-rename]').forEach(btn => {
    btn.addEventListener('click', async e => {
      e.stopPropagation();
      const oldName = btn.dataset.name;
      const newName = window.prompt('Rename to:', oldName);
      if (!newName || newName === oldName) return;
      try {
        await PUT('/api/files/rename', { path: btn.dataset.rename, newName });
        toast('Renamed to ' + newName);
        loadFiles(currentPath);
      } catch (e2) { toast(e2.message, 'error'); }
    });
  });

  container.querySelectorAll('[data-del]').forEach(btn => {
    btn.addEventListener('click', async e => {
      e.stopPropagation();
      if (!confirm(`Delete "${btn.dataset.del.split('/').pop()}"?`)) return;
      try {
        await DELETE(`/api/files?path=${encodeURIComponent(btn.dataset.del)}`);
        toast('Deleted');
        loadFiles(currentPath);
      } catch (e2) { toast(e2.message, 'error'); }
    });
  });
}

let fileCodeEditor = null;
let configCodeEditor = null;
let syntaxEditorModule = null;

async function mountCodeEditor(hostId, value, filename) {
  syntaxEditorModule ??= await import('./syntax-editor.js');
  return syntaxEditorModule.mountSyntaxEditor(document.getElementById(hostId), value, filename);
}

async function openFileEditor(filePath) {
  const panel = document.getElementById('file-editor-panel');
  try {
    const { content } = await GET(`/api/files/content?path=${encodeURIComponent(filePath)}`);
    const filename = filePath.split('/').pop();
    document.getElementById('editor-filename').textContent = filename;
    panel.classList.remove('d-none');
    fileCodeEditor?.destroy();
    fileCodeEditor = await mountCodeEditor('file-editor', content, filename);
    fileCodeEditor.focus();

    document.getElementById('btn-save-file').onclick = async () => {
      try {
        await PUT('/api/files/content', { path: filePath, content: fileCodeEditor.getValue() });
        toast('File saved');
      } catch (e) { toast(e.message, 'error'); }
    };
  } catch (e) { toast(e.message, 'error'); }
}

function getFileIcon(name) {
  const ext = name.split('.').pop().toLowerCase();
  const icons = { pbo: 'box-archive', cfg: 'gear', txt: 'file-lines', log: 'file-lines',
    json: 'file-code', sqf: 'file-code', hpp: 'file-code', cpp: 'file-code',
    zip: 'file-zipper', sh: 'scroll', html: 'file-code' };
  return icons[ext] || 'file';
}

// ════════════════════════════════════════════════════════════════════════════════
// CONFIG EDITOR
// ════════════════════════════════════════════════════════════════════════════════
async function loadConfig(file) {
  state.currentConfig = file;
  loadStartupSettings();
  try {
    const { content } = await GET(`/api/config?file=${encodeURIComponent(file)}`);
    configCodeEditor?.destroy();
    configCodeEditor = await mountCodeEditor('config-editor', content, file);
  } catch (e) { toast(e.message, 'error'); }

  // Tab switching
  document.querySelectorAll('[data-config]').forEach(tab => {
    tab.onclick = e => {
      e.preventDefault();
      document.querySelectorAll('[data-config]').forEach(t => t.classList.remove('active'));
      tab.classList.add('active');
      loadConfig(tab.dataset.config);
    };
  });

  document.getElementById('btn-save-config').onclick = async () => {
    try {
      await PUT('/api/config', { file: state.currentConfig, content: configCodeEditor.getValue() });
      toast('Config saved');
    } catch (e) { toast(e.message, 'error'); }
  };
}

// ════════════════════════════════════════════════════════════════════════════════
// LOGS
// ════════════════════════════════════════════════════════════════════════════════
async function loadStartupSettings() {
  const command = document.getElementById('startup-command');
  if (!command) return;
  try {
    const { settings, command: startupCommand } = await GET('/api/startup');
    fillStartupForm(settings);
    command.textContent = startupCommand;
  } catch (e) {
    command.textContent = 'Error loading startup settings: ' + e.message;
  }

  document.getElementById('btn-save-startup').onclick = async () => {
    try {
      const { settings, command: startupCommand } = await PUT('/api/startup', readStartupForm());
      fillStartupForm(settings);
      document.getElementById('startup-command').textContent = startupCommand;
      toast('Startup settings saved');
    } catch (e) { toast(e.message, 'error'); }
  };

  document.getElementById('btn-copy-startup-command').onclick = async () => {
    try {
      await copyText(document.getElementById('startup-command').textContent);
      toast('Startup command copied');
    } catch {
      toast('Could not copy command', 'warning');
    }
  };

  document.getElementById('btn-refresh-missions').onclick = loadMissions;
  document.getElementById('btn-apply-mission').onclick = async () => {
    const select = document.getElementById('startup-mission');
    if (!select.value) return toast('Select a mission first', 'warning');
    try {
      await PUT('/api/startup/mission', { template: select.value });
      toast('Startup mission updated');
      if (state.currentConfig === 'server.cfg') await loadConfig('server.cfg');
    } catch (e) { toast(e.message, 'error'); }
  };
  await loadMissions();

}

async function loadMissions() {
  const select = document.getElementById('startup-mission');
  if (!select) return;
  try {
    const { selected, missions } = await GET('/api/missions');
    select.innerHTML = '<option value="">Select a mission from mpmissions</option>' + missions.map(mission =>
      `<option value="${escAttr(mission.template)}">${escHtml(mission.template)}${mission.packed ? ' (PBO)' : ''}</option>`).join('');
    select.value = selected || '';
  } catch (e) { toast(e.message, 'error'); }
}

async function downloadCreatorDlcs(btn) {
  if (btn.disabled) return;
  if (!confirm('Download Creator DLC server files with SteamCMD? The task will use the Steam account already linked in SteamCMD.')) return;
  btn.disabled = true;
  try {
    toast('Starting Creator DLC download...', 'warning');
    const res = await POST('/api/server/download-creator-dlcs');
    const extra = Array.isArray(res.configuredDlcAppIds) && res.configuredDlcAppIds.length
      ? ` Extra App IDs: ${res.configuredDlcAppIds.join(', ')}`
      : '';
    toast(`Creator DLC download started.${extra}`, 'success');
    loadView('logs');
    setTimeout(loadCreatorDlcs, 2000);
  } catch (e) {
    handleSteamLoginRequired(e);
  } finally {
    btn.disabled = false;
  }
}

function fillStartupForm(s) {
  setVal('startup-binary', s.serverBinary);
  setVal('startup-ip', s.ip);
  setVal('startup-port', s.port);
  setVal('startup-profiles', s.profilesDir);
  setVal('startup-max-players', s.maxPlayers ?? '');
  setVal('startup-password', s.serverPassword || '');
  setVal('startup-extra', s.extraParams || '');
  setVal('startup-server-mods', s.serverMods || '');
  setVal('startup-optional-mods', s.optionalClientMods || '');
  setVal('startup-extra-ports', (s.extraPorts || []).join(','));
  setVal('startup-headless', s.headlessClients || 0);
  setVal('startup-steam-flags', s.steamCmdFlags || '');
  setChecked('startup-auto-update', s.automaticUpdates);
  setChecked('startup-lowercase', s.lowerCaseMods);
  setChecked('startup-validate', s.validateServerFiles);
  setChecked('startup-disable-battleye', s.disableBattleEye);
}

function readStartupForm() {
  const toInt  = (id, fallback = 0) => { const n = parseInt(getVal(id), 10); return isNaN(n) ? fallback : n; };
  const toIntN = (id)               => { const n = parseInt(getVal(id), 10); return isNaN(n) ? null : n; };
  return {
    serverBinary       : getVal('startup-binary'),
    ip                 : getVal('startup-ip'),
    port               : toInt('startup-port', 2302),
    profilesDir        : getVal('startup-profiles'),
    serverCfg          : '',   // empty → backend fills from detected paths
    basicCfg           : '',
    maxPlayers         : toIntN('startup-max-players'),
    serverPassword     : getVal('startup-password'),
    extraParams        : getVal('startup-extra'),
    serverMods         : getVal('startup-server-mods'),
    optionalClientMods : getVal('startup-optional-mods'),
    extraPorts         : getVal('startup-extra-ports').split(',').map(s => parseInt(s.trim(), 10)).filter(n => !isNaN(n) && n > 0),
    headlessClients    : toInt('startup-headless', 0),
    steamCmdFlags      : getVal('startup-steam-flags'),
    automaticUpdates   : getChecked('startup-auto-update'),
    lowerCaseMods      : getChecked('startup-lowercase'),
    validateServerFiles: getChecked('startup-validate'),
    disableBattleEye   : getChecked('startup-disable-battleye'),
  };
}

function setVal(id, value) { const el = document.getElementById(id); if (el) el.value = value ?? ''; }
function getVal(id) { return document.getElementById(id)?.value ?? ''; }
function setChecked(id, value) { const el = document.getElementById(id); if (el) el.checked = !!value; }
function getChecked(id) { return !!document.getElementById(id)?.checked; }

// ─── log SSE state ────────────────────────────────────────────────────────────
const logConnections = new LogConnectionGate();
const LOG_STALE_AFTER_MS = 40_000;
let logCursor = 0;
let logLastActivity = 0;
let logWatchdog = null;
let logRenderFrame = null;
let logRenderQueue = [];
let logUnseenCount = 0;
let logGapRecovery = false;
let logAuthCheckPending = false;

function setLogStreamStatus(label, style) {
  const status = document.getElementById('log-stream-status');
  if (!status) return;
  status.textContent = label;
  status.className = `badge text-bg-${style}`;
}

function clearLogWatchdog() {
  if (logWatchdog) window.clearInterval(logWatchdog);
  logWatchdog = null;
}

function cancelPendingLogRender() {
  if (logRenderFrame) window.cancelAnimationFrame(logRenderFrame);
  logRenderFrame = null;
  logRenderQueue = [];
}

function stopLogStream(label = 'Paused', style = 'secondary') {
  logConnections.stop();
  clearLogWatchdog();
  cancelPendingLogRender();
  setLogStreamStatus(label, style);
}

async function loadLogs() {
  const generation = logConnections.begin();
  clearLogWatchdog();
  cancelPendingLogRender();
  setLogStreamStatus('Connecting', 'secondary');
  const out = document.getElementById('log-output');
  out.replaceChildren();
  logCursor = 0;
  logUnseenCount = 0;
  updateNewLogCount();
  hideLogNotice();

  document.getElementById('btn-clear-logs').onclick = () => {
    out.replaceChildren();
    logUnseenCount = 0;
    updateNewLogCount();
  };
  document.getElementById('log-new-lines').onclick = () => scrollLogsToLive(out);
  out.onscroll = () => {
    if (isNearLogBottom(out)) {
      logUnseenCount = 0;
      updateNewLogCount();
    }
  };

  // Load historical logs first
  try {
    const entries = await GET('/api/logs?limit=300');
    if (!logConnections.isCurrent(generation)) return;
    if (entries.length) {
      const fragment = document.createDocumentFragment();
      entries.forEach(entry => fragment.appendChild(createLogLine(entry)));
      out.appendChild(fragment);
      trimLogContainer(out);
      logCursor = advanceLogCursor(0, entries.at(-1)?.id);
    }
  } catch (e) {
    if (!logConnections.isCurrent(generation)) return;
    showLogNotice('Could not load log history: ' + e.message, 'danger');
  }

  if (document.getElementById('log-autoscroll')?.checked) out.scrollTop = out.scrollHeight;
  if (logConnections.isCurrent(generation)) startLogStream(out, generation);
}

function startLogStream(out, generation) {
  setLogStreamStatus('Connecting', 'secondary');
  const url = apiUrl(`/api/logs/stream?since=${logCursor}`);
  const source = new EventSource(url, { withCredentials: true });
  if (!logConnections.attach(generation, source)) return;
  logLastActivity = Date.now();

  source.onopen = () => {
    if (!logConnections.owns(generation, source)) return;
    touchLogStream();
    setLogStreamStatus('Live', 'success');
  };

  source.onmessage = e => {
    if (!logConnections.owns(generation, source)) return;
    touchLogStream();
    try {
      const entry = JSON.parse(e.data);
      const entryId = Number(entry.id) || 0;
      if (entryId && entryId <= logCursor) return;
      logCursor = advanceLogCursor(logCursor, entryId);
      queueLogLine(out, entry);
    } catch { showLogNotice('A malformed log event was ignored.', 'warning'); }
  };

  source.addEventListener('status', e => {
    if (!logConnections.owns(generation, source)) return;
    touchLogStream();
    try {
      const { running, pid } = JSON.parse(e.data);
      updateServerStatus(running);
      if (pid) {
        const el = document.getElementById('dash-pid-text');
        if (el) el.textContent = `· PID ${pid}`;
      }
    } catch { /* ignore */ }
  });

  source.addEventListener('heartbeat', () => {
    if (!logConnections.owns(generation, source)) return;
    touchLogStream();
    setLogStreamStatus('Live', 'success');
  });

  source.addEventListener('gap', e => {
    if (!logConnections.owns(generation, source)) return;
    touchLogStream();
    try { recoverFromLogGap(out, JSON.parse(e.data), generation, source); }
    catch { showLogNotice('The server reported an unreadable log gap.', 'warning'); }
  });

  source.onerror = () => {
    if (!logConnections.owns(generation, source)) return;
    setLogStreamStatus(navigator.onLine ? 'Reconnecting' : 'Offline', navigator.onLine ? 'warning' : 'danger');
    checkLogStreamAuthentication(generation, source);
  };

  clearLogWatchdog();
  logWatchdog = window.setInterval(() => {
    if (!logConnections.owns(generation, source)) return;
    if (Date.now() - logLastActivity <= LOG_STALE_AFTER_MS) return;
    showLogNotice('No heartbeat received; the live stream was restarted.', 'warning');
    restartLogStream(out);
  }, 5_000);
}

function restartLogStream(out) {
  if (!out || state.view !== 'logs') return;
  const generation = logConnections.begin();
  clearLogWatchdog();
  startLogStream(out, generation);
}

function touchLogStream() {
  logLastActivity = Date.now();
}

async function checkLogStreamAuthentication(generation, source) {
  if (logAuthCheckPending || !navigator.onLine) return;
  logAuthCheckPending = true;
  try {
    const session = await GET('/api/auth/check');
    if (logConnections.owns(generation, source) && !session.authenticated) {
      stopLogStream('Session expired', 'danger');
      showLogNotice('Your session expired. Sign in again to resume server logs.', 'danger');
    }
  } catch { /* A network failure is already represented by the stream status. */ }
  finally { logAuthCheckPending = false; }
}

async function recoverFromLogGap(out, gap, generation, source) {
  if (logGapRecovery || !logConnections.owns(generation, source)) return;
  logGapRecovery = true;
  const missing = countMissingLogEntries(gap);
  setLogStreamStatus('Gap detected', 'warning');
  showLogNotice(
    missing > 0
      ? `${missing.toLocaleString()} log lines expired before they could be recovered. Showing the newest history.`
      : 'The previous log cursor is no longer available. Showing the newest history.',
    'warning');

  const recoveryGeneration = logConnections.begin();
  clearLogWatchdog();
  cancelPendingLogRender();
  try {
    const entries = await GET(`/api/logs?limit=${LOG_DOM_LIMIT}`);
    if (!logConnections.isCurrent(recoveryGeneration)) return;
    out.replaceChildren();
    const fragment = document.createDocumentFragment();
    entries.forEach(entry => fragment.appendChild(createLogLine(entry)));
    out.appendChild(fragment);
    trimLogContainer(out);
    logCursor = advanceLogCursor(0, entries.at(-1)?.id);
    scrollLogsToLive(out);
    startLogStream(out, recoveryGeneration);
  } catch (error) {
    if (logConnections.isCurrent(recoveryGeneration)) {
      setLogStreamStatus('Reconnecting', 'warning');
      showLogNotice('Could not recover the latest log history: ' + error.message, 'danger');
      startLogStream(out, recoveryGeneration);
    }
  } finally {
    logGapRecovery = false;
  }
}

function queueLogLine(out, entry) {
  logRenderQueue.push({ out, entry });
  if (logRenderFrame) return;
  logRenderFrame = window.requestAnimationFrame(flushLogRenderQueue);
}

function flushLogRenderQueue() {
  logRenderFrame = null;
  const pending = logRenderQueue;
  logRenderQueue = [];
  if (!pending.length) return;
  const out = pending[0].out;
  const stickToBottom = document.getElementById('log-autoscroll')?.checked && isNearLogBottom(out);
  const previousScrollTop = out.scrollTop;
  const fragment = document.createDocumentFragment();
  pending.forEach(({ entry }) => fragment.appendChild(createLogLine(entry)));
  out.appendChild(fragment);
  const trimmed = trimLogContainer(out);
  if (stickToBottom) {
    out.scrollTop = out.scrollHeight;
    logUnseenCount = 0;
  } else {
    if (trimmed.height) out.scrollTop = Math.max(0, previousScrollTop - trimmed.height);
    logUnseenCount += pending.length;
  }
  updateNewLogCount();
}

function scrollLogsToLive(out) {
  out.scrollTop = out.scrollHeight;
  logUnseenCount = 0;
  updateNewLogCount();
}

function updateNewLogCount() {
  const button = document.getElementById('log-new-lines');
  const count = document.getElementById('log-new-lines-count');
  if (!button || !count) return;
  count.textContent = logUnseenCount.toLocaleString();
  button.classList.toggle('d-none', logUnseenCount === 0);
}

function showLogNotice(message, style = 'warning') {
  const notice = document.getElementById('log-stream-notice');
  if (!notice) return;
  notice.textContent = message;
  notice.className = `log-stream-notice log-stream-notice-${style}`;
}

function hideLogNotice() {
  const notice = document.getElementById('log-stream-notice');
  if (!notice) return;
  notice.textContent = '';
  notice.className = 'log-stream-notice d-none';
}

function appendLog(entry) {
  if (state.view !== 'logs') return;
  const out = document.getElementById('log-output');
  queueLogLine(out, entry);
}

function createLogLine(entry, includeTime = true) {
  const severity = logSeverity(entry);
  const labels = { error: 'ERR', warning: 'WARN', success: 'OK', system: 'SYS', output: 'OUT', rpt: 'RPT' };
  const line = document.createElement('span');
  line.className = 'log-line';
  line.dataset.source = entry.source || 'manager';
  if (entry.runId) line.dataset.runId = entry.runId;
  line.title = `Source: ${entry.source || 'manager'}`;
  if (includeTime) {
    const time = document.createElement('span');
    time.className = 'log-time';
    time.textContent = `${new Date(entry.ts).toLocaleTimeString()} `;
    line.appendChild(time);
  }
  const level = document.createElement('span');
  level.className = `log-level log-${severity}`;
  level.textContent = `[${labels[severity]}]`;
  line.append(level, document.createTextNode(` ${stripAnsi(entry.data)}\n`));
  return line;
}

function stripAnsi(value) {
  return String(value || '').replace(/[\u001b\u009b][[\]()#;?]*(?:(?:(?:[a-zA-Z\d]*(?:;[-a-zA-Z\d\/#&.:=?%@~_]+)*)?\u0007)|(?:(?:\d{1,4}(?:[;:]\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))/g, '');
}

function logSeverity(entry) {
  const text = stripAnsi(entry?.data).toLowerCase();
  if (/\b(error|failed|failure|fatal|critical|cannot|exception)\b/.test(text)) return 'error';
  if (/steamcmd\.sh\[\d+\]:\s+(starting|restarting steamcmd by request)/.test(text)) return 'system';
  if (/\b(warn|warning|advert|busy|retry|no connection|timeout|deprecated)\b/.test(text)) return 'warning';
  if (entry?.type === 'stderr') return 'warning';
  if (/\b(success|successful|downloaded|ready|started|connected|complete|completed|ok)\b/.test(text)) return 'success';
  if (entry?.type === 'rpt') return 'rpt';
  if (entry?.type === 'system') return 'system';
  return 'output';
}

// ─── XSS helpers ─────────────────────────────────────────────────────────────
function escHtml(str) {
  return String(str).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
function escAttr(str) { return escHtml(str); }
