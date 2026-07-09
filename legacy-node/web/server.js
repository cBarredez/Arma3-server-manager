'use strict';

const express      = require('express');
const http         = require('http');
const { Server }   = require('socket.io');
const session      = require('express-session');
const helmet       = require('helmet');
const rateLimit    = require('express-rate-limit');
const multer       = require('multer');
const si           = require('systeminformation');
const cheerio      = require('cheerio');
const { spawn }    = require('child_process');
const path         = require('path');
const fs           = require('fs-extra');
const { v4: uuid } = require('uuid');

// ─── Configuration ────────────────────────────────────────────────────────────
const cfg = {
  port          : parseInt(process.env.WEB_PORT    || '8080', 10),
  username      : process.env.WEB_USERNAME         || 'admin',
  password      : process.env.WEB_PASSWORD         || 'changeme123',
  sessionSecret : process.env.SESSION_SECRET       || uuid(),
  arma3Dir      : process.env.ARMA3_DIR            || '/arma3',
  steamUser     : process.env.STEAM_USER           || 'anonymous',
  steamPass     : process.env.STEAM_PASS           || '',
  serverPort    : parseInt(process.env.SERVER_PORT || '2302', 10),
  // Steam OpenID
  baseUrl       : process.env.BASE_URL             || '',       // e.g. http://your-server:8080
  steamOwnerIds : (process.env.STEAM_OWNER_IDS     || '').split(',').map(s => s.trim()).filter(Boolean),
};

// ─── Paths (auto-detected at startup via initPaths) ───────────────────────────
// Real Arma 3 servers (game hosters / standard install) layout:
//   steamcmd/   → inside arma3 dir
//   server.cfg  → at arma3 dir root
//   serverprofile/ → profiles
//   mpmissions/ → missions
//   @modname/   → mods as @ folders at arma3 dir root
//   steamapps/workshop/content/107410/<id>/ → workshop downloads
let ARMA3_BIN, STEAMCMD, CONFIG_DIR, PROFILES_DIR, MISSIONS_DIR, KEYS_DIR, WORKSHOP_DIR, MODS_STATE, MODLISTS_STATE, STARTUP_STATE, STEAMCMD_AUTH_STATE;

async function initPaths() {
  // Keys and workshop are always inside arma3Dir
  KEYS_DIR     = path.join(cfg.arma3Dir, 'keys');
  WORKSHOP_DIR = path.join(cfg.arma3Dir, 'steamapps', 'workshop', 'content', '107410');

  // SteamCMD — game hosters put it inside the server dir; fall back to /steamcmd
  const steamInDir = path.join(cfg.arma3Dir, 'steamcmd', 'steamcmd.sh');
  const steamGlob  = path.join(process.env.STEAMCMD_DIR || '/steamcmd', 'steamcmd.sh');
  STEAMCMD = (await fs.pathExists(steamInDir)) ? steamInDir : steamGlob;

  // Server binary
  ARMA3_BIN = path.join(cfg.arma3Dir, 'arma3server_x64');
  if (!await fs.pathExists(ARMA3_BIN)) {
    const alt = path.join(cfg.arma3Dir, 'arma3server');
    if (await fs.pathExists(alt)) ARMA3_BIN = alt;
  }

  // Config dir — server.cfg at arma3 root (standard) or inside 'config/' subdir
  if (process.env.CONFIG_DIR) {
    CONFIG_DIR = process.env.CONFIG_DIR;
  } else {
    const inSub = path.join(cfg.arma3Dir, 'config', 'server.cfg');
    CONFIG_DIR  = (await fs.pathExists(inSub)) ? path.join(cfg.arma3Dir, 'config') : cfg.arma3Dir;
  }

  // Profiles dir — 'serverprofile' (standard game hosters) or 'profiles'
  if (process.env.PROFILES_DIR) {
    PROFILES_DIR = process.env.PROFILES_DIR;
  } else {
    PROFILES_DIR = path.join(cfg.arma3Dir, 'serverprofile');
    for (const d of ['serverprofile', 'profiles']) {
      const full = path.join(cfg.arma3Dir, d);
      if (await fs.pathExists(full)) { PROFILES_DIR = full; break; }
    }
  }

  // Missions dir — 'mpmissions' (standard) or 'missions'
  if (process.env.MISSIONS_DIR) {
    MISSIONS_DIR = process.env.MISSIONS_DIR;
  } else {
    MISSIONS_DIR = path.join(cfg.arma3Dir, 'mpmissions');
    for (const d of ['mpmissions', 'missions']) {
      const full = path.join(cfg.arma3Dir, d);
      if (await fs.pathExists(full)) { MISSIONS_DIR = full; break; }
    }
  }

  // Mods state tracking file (JSON at arma3 root)
  MODS_STATE = path.join(cfg.arma3Dir, 'mods.json');
  MODLISTS_STATE = path.join(cfg.arma3Dir, 'modlists.json');
  STARTUP_STATE = path.join(cfg.arma3Dir, 'startup.json');
  STEAMCMD_AUTH_STATE = path.join(cfg.arma3Dir, 'steamcmd-auth.json');
  await loadSteamCmdAuthState();

  console.log('[arma3-manager] Detected paths:');
  console.log(`  binary   : ${ARMA3_BIN}`);
  console.log(`  steamcmd : ${STEAMCMD}`);
  console.log(`  config   : ${CONFIG_DIR}`);
  console.log(`  profiles : ${PROFILES_DIR}`);
  console.log(`  missions : ${MISSIONS_DIR}`);
  console.log(`  @mods    : ${cfg.arma3Dir} (@ folders at root)`);
  console.log(`  workshop : ${WORKSHOP_DIR}`);
}

// ─── Runtime State ───────────────────────────────────────────────────────────
let arma3Proc   = null;        // child_process for the Arma 3 server
let serverLogs  = [];          // in-memory circular log buffer
const MAX_LOGS  = 1000;

// Mod install queue (sequential to avoid SteamCMD conflicts)
const installQueue = [];
let   installBusy  = false;

const MAX_STEAMCMD_LOGS = 300;
const steamCmdLogin = {
  proc         : null,
  running      : false,
  awaitingInput: false,
  username     : null,
  startedAt    : null,
  exitCode     : null,
  lastError    : null,
  logs         : [],
};

// ─── Express + Socket.IO setup ───────────────────────────────────────────────
const app    = express();
const server = http.createServer(app);
const io     = new Server(server, { cors: { origin: false } });

// ─── Security middleware ──────────────────────────────────────────────────────
app.use(helmet({
  contentSecurityPolicy: {
    directives: {
      defaultSrc : ["'self'"],
      scriptSrc  : ["'self'", "'unsafe-inline'", 'cdn.jsdelivr.net', 'cdnjs.cloudflare.com'],
      styleSrc   : ["'self'", "'unsafe-inline'", 'cdn.jsdelivr.net', 'cdnjs.cloudflare.com', 'fonts.googleapis.com'],
      fontSrc    : ["'self'", 'fonts.gstatic.com', 'cdnjs.cloudflare.com'],
      imgSrc     : ["'self'", 'data:'],
      connectSrc : ["'self'", 'ws:', 'wss:'],
    },
  },
}));

app.use(express.json({ limit: '10mb' }));
app.use(express.urlencoded({ extended: true, limit: '10mb' }));

// ─── Sessions ─────────────────────────────────────────────────────────────────
const sessionMiddleware = session({
  secret           : cfg.sessionSecret,
  resave           : false,
  saveUninitialized: false,
  cookie           : { httpOnly: true, sameSite: 'strict', maxAge: 24 * 60 * 60 * 1000 },
});
app.use(sessionMiddleware);

// ─── Static assets ───────────────────────────────────────────────────────────
app.use('/static', express.static(path.join(__dirname, 'public')));

// ─── Auth helpers ─────────────────────────────────────────────────────────────
function requireAuth(req, res, next) {
  if (req.session?.authenticated) return next();
  res.status(401).json({ error: 'Unauthorized' });
}

const loginLimiter = rateLimit({ windowMs: 15 * 60 * 1000, max: 15 });

// ════════════════════════════════════════════════════════════════════════════════
// AUTH
// ════════════════════════════════════════════════════════════════════════════════
app.post('/api/auth/login', loginLimiter, (req, res) => {
  const { username, password } = req.body || {};
  if (typeof username !== 'string' || typeof password !== 'string') {
    return res.status(400).json({ error: 'Invalid request' });
  }
  if (username === cfg.username && password === cfg.password) {
    req.session.authenticated = true;
    return res.json({ ok: true });
  }
  res.status(401).json({ error: 'Invalid credentials' });
});

app.post('/api/auth/logout', (req, res) => {
  req.session.destroy(() => res.json({ ok: true }));
});

app.get('/api/auth/check', (req, res) => {
  res.json({
    authenticated: !!req.session?.authenticated,
    steamId      : req.session?.steamId || null,
    method       : req.session?.authMethod || null,
  });
});

// ════════════════════════════════════════════════════════════════════════════════
// STEAM OPENID — "Login with Steam" (no extra packages needed, uses built-in fetch)
// Flow: browser → /auth/steam → steamcommunity.com → /auth/steam/return → session
// ════════════════════════════════════════════════════════════════════════════════
const STEAM_OPENID = 'https://steamcommunity.com/openid/login';

function getSteamReturnBase(req) {
  // Use BASE_URL env var if set; otherwise derive from request
  if (cfg.baseUrl) return cfg.baseUrl;
  const proto = req.headers['x-forwarded-proto'] || req.protocol || 'http';
  const host  = req.headers['x-forwarded-host']  || req.headers.host || `localhost:${cfg.port}`;
  return `${proto}://${host}`;
}

app.get('/auth/steam', (req, res) => {
  const base   = getSteamReturnBase(req);
  const params = new URLSearchParams({
    'openid.ns'        : 'http://specs.openid.net/auth/2.0',
    'openid.mode'      : 'checkid_setup',
    'openid.return_to' : `${base}/auth/steam/return`,
    'openid.realm'     : base,
    'openid.identity'  : 'http://specs.openid.net/auth/2.0/identifier_select',
    'openid.claimed_id': 'http://specs.openid.net/auth/2.0/identifier_select',
  });
  res.redirect(`${STEAM_OPENID}?${params}`);
});

app.get('/auth/steam/return', async (req, res) => {
  try {
    const q = req.query;
    if (q['openid.mode'] !== 'id_res') {
      return res.redirect('/?steam_error=cancelled');
    }

    // Verify the assertion with Steam (server-to-server, prevents forgery)
    const verifyParams = new URLSearchParams({ ...q, 'openid.mode': 'check_authentication' });
    const verifyRes    = await fetch(STEAM_OPENID, {
      method : 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body   : verifyParams.toString(),
    });
    const verifyText = await verifyRes.text();
    if (!verifyText.includes('is_valid:true')) {
      return res.redirect('/?steam_error=invalid');
    }

    // Extract Steam64 ID from claimed_id URL
    const m = (q['openid.claimed_id'] || '').match(/\/id\/(\d+)$/);
    if (!m) return res.redirect('/?steam_error=no_id');
    const steamId = m[1];

    // If STEAM_OWNER_IDS is set, restrict to those IDs only
    if (cfg.steamOwnerIds.length > 0 && !cfg.steamOwnerIds.includes(steamId)) {
      console.warn(`[steam-auth] Blocked Steam ID: ${steamId}`);
      return res.redirect('/?steam_error=unauthorized');
    }

    req.session.authenticated = true;
    req.session.authMethod    = 'steam';
    req.session.steamId       = steamId;
    // Explicitly save session before redirect — required for express-session + redirects
    req.session.save(err => {
      if (err) console.error('[steam-auth] Session save error:', err);
      console.log(`[steam-auth] Logged in Steam ID: ${steamId}`);
      res.redirect('/');
    });
  } catch (err) {
    console.error('[steam-auth] Error:', err.message);
    res.redirect('/?steam_error=server_error');
  }
});

// ════════════════════════════════════════════════════════════════════════════════
// HEALTH
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/health', (_req, res) => res.json({ status: 'ok' }));

// ════════════════════════════════════════════════════════════════════════════════
// STEAM STATUS  (no credentials are ever returned — only a boolean flag)
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/steam/status', requireAuth, (_req, res) => {
  const hasUser = cfg.steamUser !== 'anonymous' && cfg.steamUser !== '';
  res.json({
    hasCredentials: hasUser,
    hasPassword   : cfg.steamPass !== '',
    user          : hasUser ? cfg.steamUser : 'anonymous',
    steamcmd   : STEAMCMD,
    login      : publicSteamCmdLoginState(),
  });
});

app.get('/api/steamcmd/login', requireAuth, (_req, res) => {
  res.json(publicSteamCmdLoginState(true));
});

app.post('/api/steamcmd/login/start', requireAuth, (req, res) => {
  if (steamCmdLogin.running) {
    return res.status(409).json({ error: 'SteamCMD login is already running' });
  }

  const username = String(req.body?.username || cfg.steamUser || '').trim();
  const password = typeof req.body?.password === 'string' ? req.body.password : cfg.steamPass;
  if (!username || username === 'anonymous') {
    return res.status(400).json({ error: 'Steam username is required' });
  }

  cfg.steamUser = username;
  cfg.steamPass = password || '';
  startSteamCmdLogin(username, cfg.steamPass);
  res.json(publicSteamCmdLoginState(true));
});

app.post('/api/steamcmd/login/input', requireAuth, (req, res) => {
  const input = req.body?.input;
  if (!steamCmdLogin.running || !steamCmdLogin.proc?.stdin?.writable) {
    return res.status(400).json({ error: 'No SteamCMD login process is waiting for input' });
  }
  if (typeof input !== 'string' || input.length > 256) {
    return res.status(400).json({ error: 'Input must be a short string' });
  }
  steamCmdLogin.awaitingInput = false;
  steamCmdLogin.proc.stdin.write(`${input}\n`);
  io.emit('steamcmd:state', publicSteamCmdLoginState());
  res.json({ ok: true });
});

app.post('/api/steamcmd/login/cancel', requireAuth, (_req, res) => {
  if (!steamCmdLogin.running || !steamCmdLogin.proc) {
    return res.status(400).json({ error: 'No SteamCMD login process is running' });
  }
  steamCmdLogin.proc.kill('SIGTERM');
  res.json({ ok: true });
});

// ════════════════════════════════════════════════════════════════════════════════
// METRICS
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/metrics', requireAuth, async (_req, res) => {
  try {
    const [cpu, mem, disk, temp, network] = await Promise.all([
      si.currentLoad(),
      si.mem(),
      si.fsSize(),
      si.cpuTemperature().catch(() => null),
      si.networkStats().catch(() => []),
    ]);
    res.json({
      cpu: {
        load   : +cpu.currentLoad.toFixed(1),
        cores  : cpu.cpus.map(c => +c.load.toFixed(1)),
      },
      memory: {
        total  : mem.total,
        used   : mem.used,
        free   : mem.free,
        percent: +((mem.used / mem.total) * 100).toFixed(1),
      },
      disk: disk.map(d => ({
        fs       : d.fs,
        mount    : d.mount,
        size     : d.size,
        used     : d.used,
        available: d.available,
        percent  : +d.use,
      })),
      temperature: temp?.main ?? null,
      network: network.map(n => ({
        iface  : n.iface,
        rx_sec : n.rx_sec,
        tx_sec : n.tx_sec,
      })),
    });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ════════════════════════════════════════════════════════════════════════════════
// SERVER CONTROL
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/server/status', requireAuth, async (_req, res) => {
  const running = isRunning();
  const startup = await readStartupSettings().catch(() => ({ port: cfg.serverPort }));
  res.json({ running, pid: running ? arma3Proc.pid : null, port: startup.port || cfg.serverPort });
});

app.get('/api/startup', requireAuth, async (_req, res) => {
  try {
    const settings = await readStartupSettings();
    res.json({ settings, command: await buildStartupCommand(settings) });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.put('/api/startup', requireAuth, async (req, res) => {
  if (isRunning()) return res.status(400).json({ error: 'Stop the game server before changing startup settings' });
  try {
    const settings = normaliseStartupSettings(req.body || {});
    await fs.writeJson(STARTUP_STATE, settings, { spaces: 2 });
    cfg.serverPort = settings.port;
    if (settings.profilesDir) PROFILES_DIR = settings.profilesDir;
    if (settings.serverPassword !== undefined || settings.maxPlayers !== undefined) {
      await applyStartupConfigToServerCfg(settings);
    }
    res.json({ settings, command: await buildStartupCommand(settings) });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.post('/api/server/install', requireAuth, (_req, res) => {
  if (!cfg.steamUser || cfg.steamUser === 'anonymous') {
    return res.status(400).json({ error: 'Steam credentials required. AppID 233780 needs an account that owns Arma 3. Set STEAM_USER and STEAM_PASS.' });
  }
  res.json({ ok: true, message: 'Server installation started' });
  streamProcess(STEAMCMD,
    ['+force_install_dir', cfg.arma3Dir,
     ...steamLoginArgs(),
     '+app_update', '233780', 'validate', '+quit'],
    'install'
  );
});

app.post('/api/server/update', requireAuth, (_req, res) => {
  if (isRunning()) return res.status(400).json({ error: 'Stop the server before updating' });
  if (!cfg.steamUser || cfg.steamUser === 'anonymous') {
    return res.status(400).json({ error: 'Steam credentials required. Start a SteamCMD session first or set STEAM_USER and STEAM_PASS.' });
  }
  res.json({ ok: true, message: 'Update started' });
  streamProcess(STEAMCMD,
    ['+force_install_dir', cfg.arma3Dir,
     ...steamLoginArgs(),
     '+app_update', '233780', 'validate', '+quit'],
    'update'
  );
});

app.post('/api/server/start', requireAuth, async (req, res) => {
  if (isRunning()) return res.status(400).json({ error: 'Server is already running' });
  const isMock = process.env.MOCK_SERVER === 'true';
  if (!isMock && !await fs.pathExists(ARMA3_BIN)) {
    return res.status(400).json({ error: 'Server binary not found. Please install the server first.' });
  }
  try {
    await startServer();
    res.json({ ok: true, pid: arma3Proc.pid });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.post('/api/server/stop', requireAuth, (req, res) => {
  if (!isRunning()) return res.status(400).json({ error: 'Server is not running' });
  arma3Proc.kill('SIGTERM');
  res.json({ ok: true });
});

app.post('/api/server/restart', requireAuth, async (req, res) => {
  if (isRunning()) {
    arma3Proc.kill('SIGTERM');
    await new Promise(resolve => {
      const t = setTimeout(resolve, 8000);
      arma3Proc.once('exit', () => { clearTimeout(t); resolve(); });
    });
  }
  try {
    await startServer();
    res.json({ ok: true, pid: arma3Proc.pid });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ── helpers ──────────────────────────────────────────────────────────────────
function isRunning() { return arma3Proc !== null && !arma3Proc.killed; }

function steamLoginArgs() {
  return ['+login', cfg.steamUser, ...(cfg.steamPass ? [cfg.steamPass] : [])];
}

async function buildArgs() {
  const startup = await readStartupSettings();
  const serverCfg = startup.serverCfg || path.join(CONFIG_DIR, 'server.cfg');
  const basicCfg  = startup.basicCfg || path.join(CONFIG_DIR, 'basic.cfg');
  const activeMods = await getActiveMods();

  const args = [
    `-ip=${startup.ip}`,
    `-port=${startup.port}`,
    `-config=${serverCfg}`,
    `-cfg=${basicCfg}`,
    `-profiles=${startup.profilesDir || PROFILES_DIR}`,
    '-noSplash', '-noPause', '-world=empty',
  ];
  if (activeMods.length) {
    // Use paths relative to arma3Dir — standard Arma 3 convention (@modname)
    const relMods = activeMods.map(p => path.relative(cfg.arma3Dir, p));
    args.push(`-mod=${relMods.join(';')}`);
  }
  const serverMods = splitModFolders(startup.serverMods);
  if (serverMods.length) args.push(`-serverMod=${serverMods.join(';')}`);
  args.push(...splitArgs(startup.extraParams));
  return args;
}

async function getActiveMods() {
  if (!await fs.pathExists(MODS_STATE)) return [];
  const list = await fs.readJson(MODS_STATE);
  return list.filter(m => m.active).map(m => m.path);
}

function defaultStartupSettings() {
  return {
    serverBinary       : path.basename(ARMA3_BIN || 'arma3server_x64'),
    ip                 : '0.0.0.0',
    port               : cfg.serverPort,
    profilesDir        : PROFILES_DIR,
    serverCfg          : path.join(CONFIG_DIR, 'server.cfg'),
    basicCfg           : path.join(CONFIG_DIR, 'basic.cfg'),
    extraParams        : '-autoInit -preload -limitFPS=120 -bandwidthAlg=2 -maxFileCacheSize -noSound',
    maxPlayers         : null,
    serverPassword     : '',
    automaticUpdates   : false,
    downloadCreatorDlcs: false,
    lowerCaseMods      : false,
    validateServerFiles: false,
    serverMods         : '',
    optionalClientMods : '',
    steamCmdFlags      : '',
    headlessClients    : 0,
    hcHideConsole      : false,
    clearHcProfiles    : false,
    extraPorts         : [cfg.serverPort + 1, cfg.serverPort + 2, cfg.serverPort + 3, cfg.serverPort + 4],
  };
}

async function readStartupSettings() {
  const defaults = defaultStartupSettings();
  if (!await fs.pathExists(STARTUP_STATE)) return defaults;
  const saved = await fs.readJson(STARTUP_STATE).catch(() => ({}));
  return normaliseStartupSettings({ ...defaults, ...saved });
}

function normaliseStartupSettings(raw) {
  const defaults = defaultStartupSettings();
  const port = clampInt(raw.port, defaults.port, 1, 65535);
  return {
    serverBinary       : safeBinaryName(raw.serverBinary || defaults.serverBinary),
    ip                 : String(raw.ip || defaults.ip).trim() || defaults.ip,
    port,
    profilesDir        : String(raw.profilesDir || defaults.profilesDir),
    serverCfg          : String(raw.serverCfg || defaults.serverCfg),
    basicCfg           : String(raw.basicCfg || defaults.basicCfg),
    extraParams        : String(raw.extraParams || ''),
    maxPlayers         : raw.maxPlayers === null || raw.maxPlayers === '' || raw.maxPlayers === undefined ? null : clampInt(raw.maxPlayers, 32, 1, 256),
    serverPassword     : typeof raw.serverPassword === 'string' ? raw.serverPassword : '',
    automaticUpdates   : !!raw.automaticUpdates,
    downloadCreatorDlcs: !!raw.downloadCreatorDlcs,
    lowerCaseMods      : !!raw.lowerCaseMods,
    validateServerFiles: !!raw.validateServerFiles,
    serverMods         : String(raw.serverMods || ''),
    optionalClientMods : String(raw.optionalClientMods || ''),
    steamCmdFlags      : String(raw.steamCmdFlags || ''),
    headlessClients    : clampInt(raw.headlessClients, 0, 0, 5),
    hcHideConsole      : !!raw.hcHideConsole,
    clearHcProfiles    : !!raw.clearHcProfiles,
    extraPorts         : normalisePorts(raw.extraPorts, port),
  };
}

function safeBinaryName(name) {
  const base = path.basename(String(name || 'arma3server_x64'));
  return ['arma3server_x64', 'arma3server'].includes(base) ? base : 'arma3server_x64';
}

function clampInt(value, fallback, min, max) {
  const n = parseInt(value, 10);
  if (!Number.isFinite(n)) return fallback;
  return Math.max(min, Math.min(max, n));
}

function normalisePorts(value, gamePort) {
  const input = Array.isArray(value) ? value : String(value || '').split(/[,\s]+/);
  const ports = input.map(p => parseInt(p, 10)).filter(p => Number.isFinite(p) && p >= 1 && p <= 65535 && p !== gamePort);
  return [...new Set(ports)].slice(0, 12);
}

function splitArgs(str) {
  const args = [];
  const re = /"([^"]*)"|'([^']*)'|(\S+)/g;
  let m;
  while ((m = re.exec(String(str || '')))) args.push(m[1] ?? m[2] ?? m[3]);
  return args;
}

function splitModFolders(str) {
  return String(str || '').split(';').map(s => s.trim()).filter(Boolean);
}

async function buildStartupCommand(settings = null) {
  const s = settings || await readStartupSettings();
  const bin = path.join(cfg.arma3Dir, s.serverBinary);
  const args = await buildArgs();
  return [bin, ...args].map(shellQuote).join(' ');
}

function shellQuote(value) {
  const s = String(value);
  return /^[A-Za-z0-9_./:=@;+,-]+$/.test(s) ? s : `"${s.replace(/(["\\$`])/g, '\\$1')}"`;
}

async function applyStartupConfigToServerCfg(settings) {
  const file = settings.serverCfg || path.join(CONFIG_DIR, 'server.cfg');
  if (!await fs.pathExists(file)) return;
  let content = await fs.readFile(file, 'utf8');
  if (settings.serverPassword !== undefined) {
    content = content.replace(/^password\s*=\s*".*";?/m, `password = "${settings.serverPassword}";`);
  }
  if (settings.maxPlayers !== null && settings.maxPlayers !== undefined) {
    content = content.replace(/^maxPlayers\s*=\s*\d+;?/m, `maxPlayers = ${settings.maxPlayers};`);
  }
  await fs.writeFile(file, content, 'utf8');
}

async function startServer() {
  const isMock = process.env.MOCK_SERVER === 'true';

  if (isMock) {
    // ── Mock mode: fake Arma 3 process for local Windows testing ─────────────
    const mockScript = `
const lines = [
  'Mission file: Russia-Ukraine.ruha',
  'BattlEye Server: Initialized (v1.234)',
  'Server is running on port ${cfg.serverPort}',
  'Player connected: TestPlayer',
  'Mission Round started. AIs: 12',
  'Server FPS: 47',
  'Server FPS: 49',
  'Server FPS: 50',
];
let i = 0;
process.stdout.write('[MOCK] Arma 3 server started (mock mode)\\n');
const iv = setInterval(() => {
  process.stdout.write('[' + new Date().toLocaleTimeString() + '] ' + lines[i % lines.length] + '\\n');
  i++;
}, 2500);
process.on('SIGTERM', () => { clearInterval(iv); process.stdout.write('[MOCK] Server shutting down...\\n'); process.exit(0); });
process.on('SIGINT',  () => { clearInterval(iv); process.exit(0); });
    `;
    arma3Proc = require('child_process').spawn(process.execPath, ['-e', mockScript]);
  } else {
    const startup = await readStartupSettings();
    const binary = path.join(cfg.arma3Dir, startup.serverBinary);
    const args = await buildArgs();
    arma3Proc = spawn(binary, args, { cwd: cfg.arma3Dir });
  }

  arma3Proc.stdout.on('data', d => pushLog('stdout', d.toString()));
  arma3Proc.stderr.on('data', d => pushLog('stderr', d.toString()));
  arma3Proc.on('exit', code => {
    pushLog('system', `Arma 3 server exited with code ${code}`);
    arma3Proc = null;
    io.emit('server:stopped', { code });
  });
  arma3Proc.on('error', err => {
    pushLog('error', `Failed to start: ${err.message}`);
    arma3Proc = null;
    io.emit('server:error', { message: err.message });
  });

  io.emit('server:started', { pid: arma3Proc.pid });
}

function streamProcess(cmd, args, eventPrefix) {
  const proc = spawn(cmd, args);
  io.emit(`${eventPrefix}:started`);
  proc.stdout.on('data', d => io.emit(`${eventPrefix}:log`, { data: d.toString() }));
  proc.stderr.on('data', d => io.emit(`${eventPrefix}:log`, { data: d.toString() }));
  proc.on('exit', code => io.emit(`${eventPrefix}:done`, { code }));
}

function publicSteamCmdLoginState(includeLogs = false) {
  return {
    running      : steamCmdLogin.running,
    awaitingInput: steamCmdLogin.awaitingInput,
    username     : steamCmdLogin.username,
    startedAt    : steamCmdLogin.startedAt,
    exitCode     : steamCmdLogin.exitCode,
    lastError    : steamCmdLogin.lastError,
    logs         : includeLogs ? steamCmdLogin.logs : undefined,
  };
}

function startSteamCmdLogin(username, password) {
  resetSteamCmdLogin();
  steamCmdLogin.running   = true;
  steamCmdLogin.username  = username;
  steamCmdLogin.startedAt = new Date().toISOString();

  let cmd = STEAMCMD;
  let args = ['+login', username];
  if (password) args.push(password);
  args.push('+quit');

  if (process.env.MOCK_STEAMCMD === 'true') {
    const safeUser = username.replace(/\\/g, '\\\\').replace(/'/g, "\\'");
    cmd = process.execPath;
    args = ['-e', `
process.stdout.write('Connecting anonymously to Steam Public...OK\\n');
process.stdout.write('Logging in user ${safeUser}...\\n');
process.stdout.write('Steam Guard code: ');
process.stdin.once('data', data => {
  process.stdout.write('\\nSteam Guard accepted: ' + data.toString().trim() + '\\n');
  process.stdout.write('OK\\n');
  setTimeout(() => process.exit(0), 150);
});
setTimeout(() => process.stdout.write('\\nWaiting for Steam Guard input...\\n'), 3000);
    `];
  }

  const proc = spawn(cmd, args);
  steamCmdLogin.proc = proc;
  io.emit('steamcmd:state', publicSteamCmdLoginState());

  proc.stdout.on('data', d => pushSteamCmdLoginLog('stdout', d.toString()));
  proc.stderr.on('data', d => pushSteamCmdLoginLog('stderr', d.toString()));
  proc.on('error', err => {
    steamCmdLogin.running = false;
    steamCmdLogin.awaitingInput = false;
    steamCmdLogin.proc = null;
    steamCmdLogin.lastError = err.message;
    pushSteamCmdLoginLog('error', err.message);
  });
  proc.on('exit', async code => {
    steamCmdLogin.running = false;
    steamCmdLogin.awaitingInput = false;
    steamCmdLogin.exitCode = code;
    steamCmdLogin.proc = null;
    if (code === 0 && steamCmdLogin.username) {
      await saveSteamCmdAuthUsername(steamCmdLogin.username).catch(err => {
        steamCmdLogin.lastError = `Could not save Steam username: ${err.message}`;
      });
    }
    pushSteamCmdLoginLog('system', `SteamCMD login exited with code ${code}`);
    io.emit('steamcmd:done', publicSteamCmdLoginState());
  });
}

async function loadSteamCmdAuthState() {
  if (cfg.steamUser && cfg.steamUser !== 'anonymous') return;
  if (!STEAMCMD_AUTH_STATE || !await fs.pathExists(STEAMCMD_AUTH_STATE)) return;
  const saved = await fs.readJson(STEAMCMD_AUTH_STATE).catch(() => null);
  const username = String(saved?.username || '').trim();
  if (username) cfg.steamUser = username;
}

async function saveSteamCmdAuthUsername(username) {
  const clean = String(username || '').trim();
  if (!clean || clean === 'anonymous') return;
  cfg.steamUser = clean;
  await fs.writeJson(STEAMCMD_AUTH_STATE, {
    username: clean,
    updatedAt: new Date().toISOString(),
  }, { spaces: 2 });
}

function resetSteamCmdLogin() {
  steamCmdLogin.proc = null;
  steamCmdLogin.running = false;
  steamCmdLogin.awaitingInput = false;
  steamCmdLogin.username = null;
  steamCmdLogin.startedAt = null;
  steamCmdLogin.exitCode = null;
  steamCmdLogin.lastError = null;
  steamCmdLogin.logs = [];
}

function pushSteamCmdLoginLog(type, data) {
  const text = data.toString();
  if (steamCmdNeedsInput(text)) steamCmdLogin.awaitingInput = true;
  const entry = { type, data: text, ts: new Date().toISOString() };
  steamCmdLogin.logs.push(entry);
  if (steamCmdLogin.logs.length > MAX_STEAMCMD_LOGS) steamCmdLogin.logs.shift();
  io.emit('steamcmd:log', entry);
  io.emit('steamcmd:state', publicSteamCmdLoginState());
}

function steamCmdNeedsInput(text) {
  const s = text.toLowerCase();
  return s.includes('steam guard') ||
    s.includes('two-factor') ||
    s.includes('authenticator') ||
    s.includes('enter the current') ||
    s.includes('password:');
}

// ════════════════════════════════════════════════════════════════════════════════
// MODS
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/mods', requireAuth, async (_req, res) => {
  try {
    res.json(await readModsState());
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.post('/api/mods/install', requireAuth, (req, res) => {
  const { workshopId, name } = req.body || {};
  if (!workshopId || !/^\d+$/.test(String(workshopId))) {
    return res.status(400).json({ error: 'Invalid Workshop ID' });
  }
  enqueueInstall(String(workshopId), name || '');
  res.json({ ok: true, message: `Mod ${workshopId} queued for installation` });
});

// Batch install from preset list
app.post('/api/mods/install-batch', requireAuth, (req, res) => {
  const { mods } = req.body || {};
  if (!Array.isArray(mods)) return res.status(400).json({ error: 'mods array required' });
  let queued = 0;
  for (const m of mods) {
    if (m.workshopId && /^\d+$/.test(String(m.workshopId))) {
      enqueueInstall(String(m.workshopId), m.name || '');
      queued++;
    }
  }
  res.json({ ok: true, queued });
});

// Parse HTML preset, return mod list without installing
const upload = multer({
  storage: multer.memoryStorage(),
  limits : { fileSize: 5 * 1024 * 1024 },
  fileFilter: (_req, file, cb) => {
    if (file.originalname.endsWith('.html') || file.mimetype === 'text/html') cb(null, true);
    else cb(new Error('Only .html preset files are accepted'));
  },
});

app.post('/api/mods/preset', requireAuth, upload.single('preset'), (req, res) => {
  if (!req.file) return res.status(400).json({ error: 'No file uploaded' });
  try {
    const mods = parsePresetMods(req.file.buffer);
    if (!mods.length) return res.status(400).json({ error: 'No mods found in preset file' });
    res.json({ ok: true, mods });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.get('/api/modlists', requireAuth, async (_req, res) => {
  try {
    res.json(await readModlistsState());
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.post('/api/modlists', requireAuth, async (req, res) => {
  const { name, mods } = req.body || {};
  if (!Array.isArray(mods) || !mods.length) return res.status(400).json({ error: 'mods array required' });
  const cleanMods = normalisePresetMods(mods);
  if (!cleanMods.length) return res.status(400).json({ error: 'No valid Workshop mods in list' });
  try {
    const state = await readModlistsState();
    const list = {
      id       : uuid(),
      name     : String(name || `Modlist ${new Date().toLocaleDateString()}`).trim(),
      mods     : cleanMods,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    };
    state.lists.push(list);
    await fs.writeJson(MODLISTS_STATE, state, { spaces: 2 });
    res.json(list);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.put('/api/modlists/:id/activate', requireAuth, async (req, res) => {
  if (isRunning()) return res.status(400).json({ error: 'Stop the game server before switching modlists' });
  try {
    const state = await readModlistsState();
    const list = state.lists.find(l => l.id === req.params.id);
    if (!list) return res.status(404).json({ error: 'Modlist not found' });

    const ids = new Set(list.mods.map(m => String(m.workshopId)));
    const modsState = await readModsState();
    const installedIds = new Set();
    for (const mod of modsState) {
      if (mod.workshopId) {
        mod.active = ids.has(String(mod.workshopId));
        if (ids.has(String(mod.workshopId))) installedIds.add(String(mod.workshopId));
      } else {
        mod.active = false;
      }
    }
    await fs.writeJson(MODS_STATE, modsState, { spaces: 2 });
    state.activeModlistId = list.id;
    await fs.writeJson(MODLISTS_STATE, state, { spaces: 2 });
    const missing = list.mods.filter(m => !installedIds.has(String(m.workshopId)));
    res.json({ ok: true, activeModlistId: list.id, missing, activeCount: installedIds.size });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.post('/api/modlists/:id/install-missing', requireAuth, async (req, res) => {
  try {
    const state = await readModlistsState();
    const list = state.lists.find(l => l.id === req.params.id);
    if (!list) return res.status(404).json({ error: 'Modlist not found' });
    const modsState = await readModsState();
    const installedIds = new Set(modsState.map(m => String(m.workshopId)).filter(Boolean));
    let queued = 0;
    for (const mod of list.mods) {
      if (!installedIds.has(String(mod.workshopId))) {
        enqueueInstall(String(mod.workshopId), mod.name || '');
        queued++;
      }
    }
    res.json({ ok: true, queued });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.delete('/api/modlists/:id', requireAuth, async (req, res) => {
  if (isRunning() && req.query.deleteMods === 'true') {
    return res.status(400).json({ error: 'Stop the game server before deleting installed mods from a modlist' });
  }
  try {
    const state = await readModlistsState();
    const idx = state.lists.findIndex(l => l.id === req.params.id);
    if (idx === -1) return res.status(404).json({ error: 'Modlist not found' });
    const [list] = state.lists.splice(idx, 1);
    let deletedMods = 0;

    if (req.query.deleteMods === 'true') {
      const ids = new Set(list.mods.map(m => String(m.workshopId)));
      const modsState = await readModsState();
      const keep = [];
      for (const mod of modsState) {
        if (mod.workshopId && ids.has(String(mod.workshopId))) {
          if (mod.path && await fs.pathExists(mod.path)) await fs.remove(mod.path);
          deletedMods++;
        } else {
          keep.push(mod);
        }
      }
      await fs.writeJson(MODS_STATE, keep, { spaces: 2 });
    }

    if (state.activeModlistId === list.id) state.activeModlistId = null;
    await fs.writeJson(MODLISTS_STATE, state, { spaces: 2 });
    res.json({ ok: true, deletedMods });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.put('/api/mods/:id', requireAuth, async (req, res) => {
  const { active } = req.body || {};
  try {
    const list = await readModsState();
    const mod  = list.find(m => m.id === req.params.id);
    if (!mod) return res.status(404).json({ error: 'Mod not found' });
    mod.active = !!active;
    await fs.writeJson(MODS_STATE, list, { spaces: 2 });
    res.json(mod);
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.delete('/api/mods/:id', requireAuth, async (req, res) => {
  try {
    const list = await readModsState();
    const idx  = list.findIndex(m => m.id === req.params.id);
    if (idx === -1) return res.status(404).json({ error: 'Mod not found' });
    const [mod] = list.splice(idx, 1);
    if (mod.path && await fs.pathExists(mod.path)) await fs.remove(mod.path);
    await fs.writeJson(MODS_STATE, list, { spaces: 2 });
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ── mod helpers ───────────────────────────────────────────────────────────────
async function readModsState() {
  let saved = [];
  if (await fs.pathExists(MODS_STATE)) {
    saved = await fs.readJson(MODS_STATE);
  }
  if (!Array.isArray(saved)) saved = [];
  const existingSaved = [];
  for (const mod of saved) {
    if (!mod.path || await fs.pathExists(mod.path)) existingSaved.push(mod);
  }
  saved = existingSaved;

  // Auto-discover @* mod folders at arma3 root (standard Arma 3 layout)
  const dirs = await fs.readdir(cfg.arma3Dir).catch(() => []);
  for (const d of dirs) {
    if (!d.startsWith('@')) continue;
    const full = path.join(cfg.arma3Dir, d);
    const stat = await fs.stat(full).catch(() => null);
    if (!stat?.isDirectory()) continue;
    if (!saved.find(m => m.path === full)) {
      saved.push({ id: uuid(), name: d, path: full, active: true, workshopId: null });
    }
  }
  await fs.writeJson(MODS_STATE, saved, { spaces: 2 });
  return saved;
}

async function removeModsForDeletedPath(target) {
  if (!await fs.pathExists(MODS_STATE)) return 0;
  const targetResolved = path.resolve(target);
  const list = await fs.readJson(MODS_STATE).catch(() => []);
  const keep = [];
  let removed = 0;

  for (const mod of Array.isArray(list) ? list : []) {
    if (!mod.path) {
      keep.push(mod);
      continue;
    }
    const modPath = path.resolve(mod.path);
    const targetToMod = path.relative(targetResolved, modPath);
    const modToTarget = path.relative(modPath, targetResolved);
    const modIsInsideTarget = targetToMod === '' || (!targetToMod.startsWith('..') && !path.isAbsolute(targetToMod));
    const targetIsInsideMod = modToTarget === '' || (!modToTarget.startsWith('..') && !path.isAbsolute(modToTarget));

    if (modIsInsideTarget || targetIsInsideMod) removed++;
    else keep.push(mod);
  }

  if (removed) await fs.writeJson(MODS_STATE, keep, { spaces: 2 });
  return removed;
}

async function readModlistsState() {
  const empty = { activeModlistId: null, lists: [] };
  if (!await fs.pathExists(MODLISTS_STATE)) return empty;
  const state = await fs.readJson(MODLISTS_STATE).catch(() => empty);
  return {
    activeModlistId: state.activeModlistId || null,
    lists: Array.isArray(state.lists) ? state.lists : [],
  };
}

function parsePresetMods(buffer) {
  const html = buffer.toString('utf8');
  const $    = cheerio.load(html);
  const mods = [];

  // ── Strategy 1: Standard Arma 3 Launcher format (most common) ────────────
  // <tr data-type="ModContainer">
  //   <td data-type="DisplayName">Name</td>
  //   <td><ul><li data-type="HtmlLink"><a href="...?id=XXXXXX">
  $('[data-type="ModContainer"]').each((_i, el) => {
    const name = $(el).find('[data-type="DisplayName"]').text().trim();
    const href = $(el).find('[data-type="HtmlLink"] a').attr('href') || '';
    const m    = href.match(/[?&]id=(\d+)/);
    if (m) mods.push({ name: name || `@${m[1]}`, workshopId: m[1] });
  });

  // ── Strategy 2: steam:// protocol links (older launcher exports) ──────────
  // <a href="steam://openurl/https://steamcommunity.com/...?id=XXXXXX">
  if (!mods.length) {
    $('a[href*="steam://"]').each((_i, el) => {
      const href = $(el).attr('href') || '';
      const m    = href.match(/[?&]id=(\d+)/);
      if (!m) return;
      const name = $(el).closest('tr').find('[data-type="DisplayName"]').text().trim()
                || $(el).text().trim()
                || `@${m[1]}`;
      mods.push({ name, workshopId: m[1] });
    });
  }

  // ── Strategy 3: Any anchor tag pointing to a Workshop page ───────────────
  // Covers any format where links contain filedetails/?id=
  if (!mods.length) {
    $('a').each((_i, el) => {
      const href = $(el).attr('href') || '';
      if (!href.includes('filedetails') && !href.includes('sharedfiles')) return;
      const m = href.match(/[?&]id=(\d+)/);
      if (!m) return;
      const name = $(el).closest('tr').find('[data-type="DisplayName"]').text().trim()
                || $(el).closest('td').prev('td').text().trim()
                || $(el).text().trim()
                || `@${m[1]}`;
      mods.push({ name, workshopId: m[1] });
    });
  }

  // ── Strategy 4: Scan entire file for any Workshop IDs ────────────────────
  // Last resort — extracts every ?id=XXXXXX found anywhere in the HTML
  if (!mods.length) {
    const allIds = [...html.matchAll(/[?&]id=(\d{6,12})/g)];
    for (const match of allIds) {
      mods.push({ name: `@${match[1]}`, workshopId: match[1] });
    }
  }

  return normalisePresetMods(mods);
}

function normalisePresetMods(mods) {
  const seen = new Set();
  const out = [];
  for (const mod of mods) {
    const workshopId = String(mod.workshopId || '').trim();
    if (!/^\d+$/.test(workshopId) || seen.has(workshopId)) continue;
    seen.add(workshopId);
    out.push({ workshopId, name: String(mod.name || `@${workshopId}`).trim() });
  }
  return out;
}

function enqueueInstall(workshopId, name) {
  installQueue.push({ workshopId, name });
  processInstallQueue();
}

function processInstallQueue() {
  if (installBusy || !installQueue.length) return;
  installBusy = true;
  const task = installQueue.shift();
  io.emit('mod:installing', { workshopId: task.workshopId, name: task.name });

  const args = ['+force_install_dir', cfg.arma3Dir,
                 ...steamLoginArgs(),
                 '+workshop_download_item', '107410', task.workshopId, 'validate',
                 '+quit'];

  const proc = spawn(STEAMCMD, args);
  proc.stdout.on('data', d => io.emit('mod:log',   { workshopId: task.workshopId, data: d.toString() }));
  proc.stderr.on('data', d => io.emit('mod:log',   { workshopId: task.workshopId, data: d.toString() }));
  proc.on('exit', async code => {
    if (code === 0) {
      try { await finaliseModInstall(task.workshopId, task.name); }
      catch (err) { io.emit('mod:error', { workshopId: task.workshopId, error: err.message }); }
    } else {
      io.emit('mod:error', { workshopId: task.workshopId, error: `SteamCMD exited with code ${code}` });
    }
    installBusy = false;
    processInstallQueue();
  });
}

async function finaliseModInstall(workshopId, displayName) {
  const src = path.join(WORKSHOP_DIR, workshopId);
  if (!await fs.pathExists(src)) throw new Error('Downloaded mod directory not found');

  // Derive safe folder name
  let folderName = `@${workshopId}`;
  if (displayName) {
    folderName = '@' + displayName.toLowerCase().replace(/[^a-z0-9_-]/g, '_');
  } else {
    const metaFile = path.join(src, 'meta.cpp');
    if (await fs.pathExists(metaFile)) {
      const meta  = await fs.readFile(metaFile, 'utf8');
      const match = meta.match(/name\s*=\s*"([^"]+)"/i);
      if (match) folderName = '@' + match[1].toLowerCase().replace(/[^a-z0-9_-]/g, '_');
    }
  }

  // Install mod as @folder at arma3 root (standard Arma 3 layout)
  const dest = path.join(cfg.arma3Dir, folderName);
  await fs.copy(src, dest, { overwrite: true });

  // Copy keys
  await fs.ensureDir(KEYS_DIR);
  for (const kd of ['keys', 'key']) {
    const kdir = path.join(dest, kd);
    if (await fs.pathExists(kdir)) {
      const keys = await fs.readdir(kdir);
      for (const k of keys.filter(f => f.endsWith('.bikey'))) {
        await fs.copy(path.join(kdir, k), path.join(KEYS_DIR, k), { overwrite: true });
      }
    }
  }

  // Track in state — update existing entry if same path, otherwise add new
  const list = await readModsState();
  const existing = list.find(m => m.path === dest || m.workshopId === workshopId);
  if (existing) {
    existing.workshopId = workshopId;
    existing.name       = folderName;
    existing.path       = dest;
  } else {
    list.push({ id: uuid(), name: folderName, path: dest, active: true, workshopId });
  }
  await fs.writeJson(MODS_STATE, list, { spaces: 2 });

  io.emit('mod:installed', { workshopId, name: folderName });
}

// ════════════════════════════════════════════════════════════════════════════════
// FILE MANAGER
// ════════════════════════════════════════════════════════════════════════════════
function guardPath(reqPath) {
  const root = path.resolve(cfg.arma3Dir);
  const raw = String(reqPath || '').trim();
  const candidate = path.isAbsolute(raw) ? raw : path.join(root, raw);
  const resolved = path.resolve(candidate);
  const rel = path.relative(root, resolved);
  if (rel.startsWith('..') || path.isAbsolute(rel)) {
    throw Object.assign(new Error('Access denied'), { status: 403 });
  }
  return resolved;
}

function toSafeRelative(absPath) {
  const rel = path.relative(path.resolve(cfg.arma3Dir), path.resolve(absPath)).replace(/\\/g, '/');
  return rel === '' ? '' : rel;
}

app.get('/api/files', requireAuth, async (req, res) => {
  try {
    const dir   = guardPath(req.query.path);
    const items = await fs.readdir(dir, { withFileTypes: true });
    const out   = await Promise.all(items.map(async item => {
      const full = path.join(dir, item.name);
      const stat = await fs.stat(full).catch(() => null);
      return {
        name    : item.name,
        path    : toSafeRelative(full),
        isDir   : item.isDirectory(),
        size    : stat?.size    ?? 0,
        modified: stat?.mtime   ?? null,
      };
    }));
    res.json({
      path: toSafeRelative(dir),
      rootName: 'Arma 3 Server',
      items: out.sort((a, b) => (b.isDir - a.isDir) || a.name.localeCompare(b.name)),
    });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message });
  }
});

app.get('/api/files/content', requireAuth, async (req, res) => {
  try {
    const file = guardPath(req.query.path);
    const stat = await fs.stat(file);
    if (stat.isDirectory()) return res.status(400).json({ error: 'Path is a directory' });
    if (stat.size > 2 * 1024 * 1024) return res.status(400).json({ error: 'File too large to edit (>2 MB)' });
    const content = await fs.readFile(file, 'utf8');
    res.json({ path: toSafeRelative(file), content });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message });
  }
});

app.put('/api/files/content', requireAuth, async (req, res) => {
  const { content } = req.body || {};
  if (typeof content !== 'string') return res.status(400).json({ error: 'content string required' });
  try {
    const file = guardPath(req.body.path);
    await fs.writeFile(file, content, 'utf8');
    res.json({ ok: true });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message });
  }
});

// File upload into the arma3 dir
const fileUpload = multer({ storage: multer.diskStorage({
  destination: async (req, _file, cb) => {
    try {
      const dir = guardPath(req.query.dir);
      cb(null, dir);
    } catch (e) { cb(e); }
  },
  filename: (_req, file, cb) => cb(null, path.basename(file.originalname)),
}), limits: { fileSize: 500 * 1024 * 1024 } });

app.post('/api/files/upload', requireAuth, fileUpload.array('files'), (req, res) => {
  res.json({ ok: true, uploaded: (req.files || []).map(f => f.filename) });
});

app.delete('/api/files', requireAuth, async (req, res) => {
  try {
    const target = guardPath(req.query.path);
    // Refuse to delete root arma3 dir itself
    if (path.resolve(target) === path.resolve(cfg.arma3Dir)) {
      return res.status(400).json({ error: 'Cannot delete server root' });
    }
    await fs.remove(target);
    const removedMods = await removeModsForDeletedPath(target);
    res.json({ ok: true, removedMods });
  } catch (err) {
    res.status(err.status || 500).json({ error: err.message });
  }
});

// ════════════════════════════════════════════════════════════════════════════════
// CONFIG EDITOR
// ════════════════════════════════════════════════════════════════════════════════
const ALLOWED_CONFIGS = ['server.cfg', 'basic.cfg'];

app.get('/api/config', requireAuth, async (req, res) => {
  const file = req.query.file || 'server.cfg';
  if (!ALLOWED_CONFIGS.includes(file)) return res.status(400).json({ error: 'Unknown config file' });
  try {
    const content = await fs.readFile(path.join(CONFIG_DIR, file), 'utf8');
    res.json({ file, content });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

app.put('/api/config', requireAuth, async (req, res) => {
  const { file, content } = req.body || {};
  if (!ALLOWED_CONFIGS.includes(file)) return res.status(400).json({ error: 'Unknown config file' });
  if (typeof content !== 'string') return res.status(400).json({ error: 'content required' });
  try {
    await fs.writeFile(path.join(CONFIG_DIR, file), content, 'utf8');
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// ════════════════════════════════════════════════════════════════════════════════
// LOGS
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/logs', requireAuth, (req, res) => {
  const limit = Math.min(parseInt(req.query.limit || '300', 10), MAX_LOGS);
  res.json(serverLogs.slice(-limit));
});

// ════════════════════════════════════════════════════════════════════════════════
// PATHS INFO  (useful for debugging and the UI settings panel)
// ════════════════════════════════════════════════════════════════════════════════
app.get('/api/paths', requireAuth, (_req, res) => {
  res.json({
    arma3Dir : cfg.arma3Dir,
    binary   : ARMA3_BIN,
    steamcmd : STEAMCMD,
    config   : CONFIG_DIR,
    profiles : PROFILES_DIR,
    missions : MISSIONS_DIR,
    keys     : KEYS_DIR,
    workshop : WORKSHOP_DIR,
    modsRoot : cfg.arma3Dir,
  });
});

// ════════════════════════════════════════════════════════════════════════════════
// SPA fallback
// ════════════════════════════════════════════════════════════════════════════════
app.get('*', (_req, res) => res.sendFile(path.join(__dirname, 'public', 'index.html')));

// ─── Log helper ──────────────────────────────────────────────────────────────
function pushLog(type, data) {
  const entry = { type, data: data.trimEnd(), ts: new Date().toISOString() };
  serverLogs.push(entry);
  if (serverLogs.length > MAX_LOGS) serverLogs.shift();
  io.emit('server:log', entry);
}

// ─── Socket.IO auth ──────────────────────────────────────────────────────────
io.use((socket, next) => {
  sessionMiddleware(socket.request, {}, err => {
    if (err) return next(err);
    if (!socket.request.session?.authenticated) return next(new Error('Unauthorized'));
    next();
  });
});

io.on('connection', socket => {
  socket.emit('server:status', { running: isRunning() });
  socket.emit('steamcmd:state', publicSteamCmdLoginState());
});

// ─── Real-time metrics broadcast ─────────────────────────────────────────────
setInterval(async () => {
  try {
    const [cpu, mem] = await Promise.all([si.currentLoad(), si.mem()]);
    io.emit('metrics:tick', {
      cpu    : +cpu.currentLoad.toFixed(1),
      mem    : +((mem.used / mem.total) * 100).toFixed(1),
      memUsed: mem.used,
      memFree: mem.free,
      memTotal: mem.total,
    });
  } catch { /* ignore */ }
}, 2000);

// ─── Start: detect paths then listen ────────────────────────────────────────
initPaths().then(() => {
  server.listen(cfg.port, '0.0.0.0', () => {
    console.log(`[arma3-manager] Web panel listening on :${cfg.port}`);
    if (process.env.AUTO_START_SERVER === 'true') {
      setTimeout(async () => {
        if (isRunning()) return;
        if (process.env.MOCK_SERVER !== 'true' && !await fs.pathExists(path.join(cfg.arma3Dir, (await readStartupSettings()).serverBinary))) {
          console.warn('[arma3-manager] AUTO_START_SERVER skipped: server binary not found');
          return;
        }
        try {
          await startServer();
          console.log('[arma3-manager] AUTO_START_SERVER started the game server');
        } catch (err) {
          console.error('[arma3-manager] AUTO_START_SERVER failed:', err.message);
        }
      }, 2000);
    }
  });
}).catch(err => {
  console.error('[arma3-manager] Failed to init paths:', err);
  process.exit(1);
});
