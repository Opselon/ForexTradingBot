/* ============================================================
   ForexTradingBot Control Panel — SPA shell.
   Tiny router + fetch layer + views. No build step, no npm:
   the whole panel ships as static files inside the WebAPI wwwroot.
   ============================================================ */

(() => {
"use strict";

/* ----------------------------- utilities ----------------------------- */
const $  = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => Array.from(r.querySelectorAll(s));
const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) =>
  ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const state = {
  route: "dashboard",
  status: null,
  health: "checking",
  loading: true,
};

/* ----------------------------- toast ----------------------------- */
function toast(title, msg, kind = "") {
  const el = document.createElement("div");
  el.className = "toast " + kind;
  el.innerHTML = `<strong>${esc(title)}</strong><span>${esc(msg ?? "")}</span>`;
  $("#toasts").appendChild(el);
  setTimeout(() => { el.style.opacity = "0"; el.style.transform = "translateX(20px)"; }, 3800);
  setTimeout(() => el.remove(), 4300);
}

/* ----------------------------- api ----------------------------- */
let base = "";
function api(path, opts = {}) {
  const init = {
    method: opts.method || "GET",
    headers: { "Content-Type": "application/json", ...(opts.headers || {}) },
    credentials: "same-origin",
  };
  if (opts.body) init.body = JSON.stringify(opts.body);

  return fetch(base + path, init).then(async (r) => {
    const text = await r.text();
    let data = null;
    try { data = text ? JSON.parse(text) : null; } catch { data = text; }

    if (!r.ok) {
      const msg = (data && (data.message || data.title || data.errors)) || `HTTP ${r.status}`;
      const detail = typeof msg === "string" ? msg : JSON.stringify(msg);
      throw new Error(detail);
    }
    return data;
  });
}

async function apiSafe(path, opts) {
  try { return await api(path, opts); }
  catch (e) { toast("Request failed", e.message, "err"); return null; }
}

/* ----------------------------- theme ----------------------------- */
function applyTheme(t) {
  document.documentElement.setAttribute("data-theme", t);
  try { localStorage.setItem("fb-theme", t); } catch {}
}
$("#themeBtn").addEventListener("click", () => {
  const cur = document.documentElement.getAttribute("data-theme");
  applyTheme(cur === "dark" ? "light" : "dark");
});
try { applyTheme(localStorage.getItem("fb-theme") || "dark"); } catch { applyTheme("dark"); }

/* ----------------------------- router ----------------------------- */
const TITLES = {
  dashboard:  "Dashboard",
  setup:      "Easy Setup",
  forwarding: "Forwarding Rules",
  rss:        "RSS Sites",
  ai:         "AI Analysis Settings",
  forcejoin:  "Force Join",
  tglogin:    "Telegram Login",
  channels:   "Channels & Private Chats",
  settings:   "Settings",
  secrets:    "Secrets Vault",
  logs:       "Logs",
  deploy:     "Deploy & Ports",
};

function go(route) {
  if (!TITLES[route]) route = "dashboard";
  state.route = route;
  $$(".nav-item").forEach((a) => a.classList.toggle("active", a.dataset.route === route));
  $("#pageTitle").textContent = TITLES[route];
  document.body.classList.remove("nav-open");
  render();
  try { history.replaceState(null, "", "#/" + route); } catch {}
}

$$(".nav-item").forEach((a) => a.addEventListener("click", (e) => {
  e.preventDefault(); go(a.dataset.route);
}));

$("#hamburger").addEventListener("click", () => document.body.classList.toggle("nav-open"));

/* ----------------------------- health pill ----------------------------- */
async function refreshHealth() {
  try {
    const r = await fetch("/healthz", { credentials: "same-origin" });
    state.health = r.ok ? "ok" : "warn";
  } catch { state.health = "err"; }

  const pill = $("#healthPill");
  pill.className = "pill " + state.health;
  const label = state.health === "ok" ? "healthy" : state.health === "warn" ? "degraded" : "down";
  pill.innerHTML = `<span class="dot"></span> ${label}`;
}

/* ----------------------------- views ----------------------------- */
function skeleton(msg = "Loading…") {
  return `<div class="grid grid-3">
    ${[0,1,2].map(() => `<div class="card"><div class="skel" style="height:14px;width:46%"></div>
      <div class="skel" style="height:26px;width:64%;margin-top:12px"></div>
      <div class="skel" style="height:11px;width:80%;margin-top:10px"></div></div>`).join("")}
  </div>
  <p class="hint" style="text-align:center;margin-top:14px">${esc(msg)}</p>`;
}

function stat(label, value, hint, accent) {
  return `<div class="stat" style="--accent:${accent || "var(--brand)"}">
    <div class="stat-label">${esc(label)}</div>
    <div class="stat-value">${esc(value)}</div>
    ${hint ? `<div class="stat-hint">${esc(hint)}</div>` : ""}</div>`;
}

/* ---------- Dashboard ---------- */
async function viewDashboard() {
  const s = await apiSafe("/api/setup/status");
  if (!s) return `<div class="card"><p class="card-sub">Could not read setup status. Is the API running?</p></div>`;

  state.status = s;
  const dep = s.dependencies || [];
  const healthy = dep.filter((d) => d.isHealthy).length;

  return `
  <div class="grid grid-4">
    ${stat("Database", esc(s.databaseProvider), s.databaseMigrated ? "migrated ✓" : "migration needed", s.databaseMigrated ? "var(--ok)" : "var(--warn)")}
    ${stat("Redis", s.redisReachable ? "reachable" : "memory only", s.redisReachable ? "queue ready" : "fallback mode", s.redisReachable ? "var(--ok)" : "var(--warn)")}
    ${stat("Forwarding rules", String(s.forwardingRulesConfigured ? "configured" : "none"), s.firstRunCompleted ? "admin set" : "first run pending")}
    ${stat("Setup progress", s.setupProgress + "%", s.isFullyConfigured ? "all steps done" : "open the wizard")}
  </div>

  <div class="card" style="margin-top:16px">
    <h3 class="card-title">Infrastructure dependencies</h3>
    <p class="card-sub">Live probes of everything the bot needs to run.</p>
    <div class="table-wrap"><table>
      <thead><tr><th>Dependency</th><th>Status</th><th>Current value</th><th>Recommendation</th></tr></thead>
      <tbody>
      ${dep.map((d) => `<tr>
        <td><strong>${esc(d.name)}</strong></td>
        <td>${d.isHealthy ? `<span class="badge ok">healthy</span>` : `<span class="badge ${d.isConfigured ? "warn" : "err"}">${d.isConfigured ? "unreachable" : "missing"}</span>`}</td>
        <td style="font-family:var(--mono);font-size:11.5px;color:var(--text-2)">${esc(d.currentValue || "—")}</td>
        <td style="color:var(--text-3);font-size:12px">${esc(d.recommendation)}</td>
      </tr>`).join("")}
      </tbody></table></div>
  </div>

  <div class="grid grid-2" style="margin-top:16px">
    <div class="card">
      <h3 class="card-title">Listening on</h3>
      <dl style="margin:6px 0 0">
        <div class="kv"><dt>ASPNETCORE_URLS</dt><dd>${esc(s.aspNetCoreUrls || "—")}</dd></div>
        <div class="kv"><dt>Domain</dt><dd>${esc(s.configuredDomain || "not set")}</dd></div>
        <div class="kv"><dt>HTTPS</dt><dd>${s.httpsConfigured ? "enabled" : "not configured"}</dd></div>
      </dl>
    </div>
    <div class="card">
      <h3 class="card-title">Next step</h3>
      <p class="card-sub">${esc(s.isFullyConfigured
        ? "Everything is configured. Create a forwarding rule to start copying messages."
        : "Open the Easy Setup wizard — it applies migrations, seeds data and verifies Redis and Telegram for you.")}</p>
      <button class="btn btn-primary" data-act="goto" data-route="setup">Open Easy Setup →</button>
    </div>
  </div>`;
}

/* ---------- Easy Setup wizard ---------- */
async function viewSetup() {
  const s = await apiSafe("/api/setup/status");
  if (!s) return `<div class="card"><p class="card-sub">Could not read setup status.</p></div>`;
  state.status = s;

  const steps = [
    { ok: s.databaseMigrated, ico: "1", title: "Database — apply migrations",
      desc: `Provider: ${s.databaseProvider}. EF Core migrations create every table the bot needs.`,
      action: `<button class="btn btn-primary btn-sm" data-act="migrate">Apply migrations &amp; seed</button>
               <button class="btn btn-ghost btn-sm" data-act="switch-db">Switch provider…</button>` },
    { ok: s.seedDataApplied, ico: "2", title: "Seed data",
      desc: s.seedDataApplied ? "Reference data is in place." : "Seeds a default forwarding rule so the panel is not empty.",
      action: `` },
    { ok: s.redisReachable, ico: "3", title: "Redis — background jobs",
      desc: s.redisReachable ? "Redis answered the ping." : "Optional. Without Redis, jobs queue in memory and are lost on restart.",
      action: `<button class="btn btn-sm" data-act="test-redis">Test Redis</button>` },
    { ok: s.hangfireRunning, ico: "4", title: "Hangfire scheduler",
      desc: s.hangfireRunning ? "Scheduler storage is connected." : "Hangfire storage is not initialized.",
      action: `` },
    { ok: s.telegramBotConfigured, ico: "5", title: "Telegram bot token",
      desc: s.telegramBotConfigured ? "A bot token is configured." : "Create a bot with @BotFather, then paste the token into Settings.",
      action: `<button class="btn btn-sm" data-act="test-telegram">Test Telegram</button>
               <button class="btn btn-ghost btn-sm" data-act="goto" data-route="settings">Settings →</button>` },
    { ok: s.forwardingRulesConfigured, ico: "6", title: "First forwarding rule",
      desc: s.forwardingRulesConfigured ? "At least one rule exists." : "Create a rule that copies from a channel to your private chat.",
      action: `<button class="btn btn-primary btn-sm" data-act="goto" data-route="forwarding">Create rule →</button>` },
  ];

  return `
  <div class="card">
    <h3 class="card-title">Easy Setup wizard</h3>
    <p class="card-sub">Six steps from an empty server to a working bot. Each step verifies itself.</p>
    <div class="progress" style="margin:12px 0 18px"><i style="width:${s.setupProgress}%"></i></div>
    <div class="steps">
      ${steps.map((st) => `<div class="step ${st.ok ? "done" : "pending"}">
        <div class="step-ico">${st.ok ? "✓" : st.ico}</div>
        <div class="step-body">
          <div class="step-title">${esc(st.title)}</div>
          <div class="step-desc">${esc(st.desc)}</div>
          ${!st.ok ? `<div class="step-action">${st.action}</div>` : ""}
        </div>
      </div>`).join("")}
    </div>
  </div>

  <div class="card">
    <h3 class="card-title">Install Redis &amp; PostgreSQL in one command</h3>
    <p class="card-sub">If your server does not have them yet, this is the fastest path.</p>
    <pre class="code"><span class="c-com"># Redis + PostgreSQL via Docker (recommended)</span>
docker run -d --name fb-redis -p <span class="c-str">6379</span>:6379 --restart unless-stopped redis:<span class="c-str">7</span>-alpine
docker run -d --name fb-postgres -p <span class="c-str">5432</span>:5432 --restart unless-stopped \
  -e POSTGRES_USER=<span class="c-str">forexbot</span> \
  -e POSTGRES_PASSWORD=<span class="c-str">change-me</span> \
  -e POSTGRES_DB=<span class="c-str">forexbotdb</span> \
  postgres:<span class="c-str">15</span>-alpine

<span class="c-com"># …or install Redis natively on Debian/Ubuntu</span>
sudo apt install -y redis-server && sudo systemctl enable --now redis-server</pre>
  </div>`;
}

/* ---------- Forwarding rules ---------- */
let rules = [];
async function viewForwarding() {
  const list = await apiSafe("/api/v2/Forwarding/rules");
  if (list) rules = Array.isArray(list) ? list : [];

  return `
  <div class="card">
    <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">
      <div><h3 class="card-title">Forwarding rules</h3>
      <p class="card-sub" style="margin:0">Copy messages between channels and private chats.</p></div>
      <button class="btn btn-primary" style="margin-left:auto" data-act="new-rule">+ New rule</button>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <div id="ruleForm" hidden>
      <h3 class="card-title">New forwarding rule</h3>
      <p class="card-sub">Source channel ID, target IDs (comma separated), then filters.</p>
      <div class="grid grid-2">
        <div class="field"><label class="lab">Rule name</label>
          <input type="text" id="f-name" placeholder="e.g. signals-to-pv" /></div>
        <div class="field"><label class="lab">Source channel ID</label>
          <input type="text" id="f-src" placeholder="-1001234567890" /></div>
        <div class="field"><label class="lab">Target channel / PV IDs</label>
          <input type="text" id="f-dst" placeholder="-1009876543210, -1005555555555" />
          <div class="hint">Separate multiple targets with commas. Use the same ID format the source uses.</div></div>
        <div class="field"><label class="lab">Enabled</label>
          <label class="switch"><input type="checkbox" id="f-on" checked /><i></i></label></div>
      </div>
      <button class="btn btn-primary" data-act="save-rule">Save rule</button>
      <button class="btn btn-ghost" data-act="cancel-rule">Cancel</button>
    </div>

    ${rules.length === 0
      ? `<div class="empty"><span class="ico">⇄</span>
          No forwarding rules yet.<br />Create your first rule to start copying messages.</div>`
      : rules.map((r) => `<div class="rule">
          <div class="rule-head">
            <label class="switch"><input type="checkbox" ${r.isEnabled ? "checked" : ""} data-act="toggle-rule" data-name="${esc(r.ruleName)}" /><i></i></label>
            <strong>${esc(r.ruleName)}</strong>
            <span class="badge ${r.isEnabled ? "ok" : ""}">${r.isEnabled ? "on" : "off"}</span>
            <button class="btn btn-danger btn-sm" style="margin-left:auto" data-act="del-rule" data-name="${esc(r.ruleName)}">Delete</button>
          </div>
          <div class="rule-meta">${esc(r.sourceChannelId)} → ${(r.targetChannelIds || []).join(", ") || "—"}</div>
        </div>`).join("")}
  </div>`;
}

/* ---------- Channels & PVs ---------- */
async function viewChannels() {
  const s = await apiSafe("/api/setup/status");
  const t = await apiSafe("/api/telegram/bot-settings");

  return `
  <div class="grid grid-2">
    <div class="card">
      <h3 class="card-title">Bot session</h3>
      <p class="card-sub">The account the bot connects with.</p>
      <dl style="margin:6px 0 0">
        <div class="kv"><dt>Configured</dt><dd>${s && s.telegramBotConfigured ? "yes" : "no"}</dd></div>
        <div class="kv"><dt>Bot username</dt><dd>${t && t.botUserName ? "@" + esc(t.botUserName) : "—"}</dd></div>
        <div class="kv"><dt>API ID</dt><dd>${t && t.apiId ? esc(String(t.apiId)) : "—"}</dd></div>
      </dl>
      <button class="btn btn-sm" style="margin-top:14px" data-act="test-telegram">Test connection</button>
    </div>
    <div class="card">
      <h3 class="card-title">How to get channel / chat IDs</h3>
      <p class="card-sub">Forwarding rules need numeric IDs, not @usernames.</p>
      <pre class="code"><span class="c-com"># 1. Add the bot as admin to the source channel</span>
<span class="c-com"># 2. Send any message in the channel</span>
<span class="c-com"># 3. Read the update feed:</span>
curl https://api.telegram.org/bot<span class="c-str">&lt;TOKEN&gt;</span>/getUpdates | jq

<span class="c-com"># Channels start with -100, private chats are positive</span>
<span class="c-com"># e.g.  channel: -1001234567890</span>
<span class="c-com">#       private chat:  612345678</pre>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <h3 class="card-title">Client session (user account, optional)</h3>
    <p class="card-sub">A user session can read channels the bot cannot. Use a phone-number session only when needed.</p>
    <button class="btn btn-sm" data-act="goto" data-route="secrets">Manage session secret →</button>
  </div>`;
}

/* ---------- Settings ---------- */
let settingsCache = [];
async function viewSettings() {
  const list = await apiSafe("/api/settings/all");
  if (list) settingsCache = Array.isArray(list) ? list : [];

  const groups = {};
  (settingsCache || []).forEach((s) => {
    const g = String(s.key || "").split(":")[0] || "general";
    (groups[g] = groups[g] || []).push(s);
  });
  const keys = Object.keys(groups).sort();

  return `
  <div class="card">
    <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">
      <div><h3 class="card-title">Dynamic settings</h3>
      <p class="card-sub" style="margin:0">Every value here is written back to the database and hot-reloaded. No restart, no file edits.</p></div>
      <button class="btn btn-primary" style="margin-left:auto" data-act="save-settings">Save changes</button>
    </div>
  </div>

  ${keys.length === 0
    ? `<div class="card" style="margin-top:16px"><div class="empty"><span class="ico">✦</span>No settings registered yet.</div></div>`
    : keys.map((g) => `<div class="card" style="margin-top:16px">
        <h3 class="card-title">${esc(g)}</h3>
        <div class="grid grid-2">
          ${(groups[g] || []).map((s) => `<div class="field">
            <label class="lab">${esc(s.key)}
              ${s.isSensitive ? `<span class="badge warn" style="margin-left:6px">sensitive</span>` : ""}
              ${s.isOverriddenByEnvironment ? `<span class="badge" style="margin-left:4px">env override</span>` : ""}
            </label>
            <input type="${s.isSensitive ? "password" : "text"}"
                   data-key="${esc(s.key)}" value="${esc(s.displayValue ?? s.value ?? "")}" />
            ${s.description ? `<div class="hint">${esc(s.description)}</div>` : ""}
          </div>`).join("")}
        </div>
      </div>`).join("")}`;
}

/* ---------- Telegram login (phone → code → 2FA) ---------- */
let tgSession = null;
async function viewTgLogin() {
  return `
  <div class="grid grid-2">
    <div class="card">
      <h3 class="card-title">Connect your Telegram account</h3>
      <p class="card-sub">Forwarding needs a user session. Log in once — the panel handles the code and 2FA.</p>
      <div class="field"><label class="lab">API ID</label>
        <input type="text" id="tg-apiid" placeholder="12345678" />
        <div class="hint">From <a href="https://my.telegram.org/apps" target="_blank" rel="noopener">my.telegram.org/apps</a>.</div></div>
      <div class="field"><label class="lab">API hash</label>
        <input type="password" id="tg-apihash" placeholder="0123456789abcdef…" /></div>
      <div class="field"><label class="lab">Phone number</label>
        <input type="text" id="tg-phone" placeholder="+989123456789" />
        <div class="hint">Include the country code. Telegram sends the code to this number.</div></div>
      <button class="btn btn-primary btn-block" data-act="tg-start">Send login code</button>
    </div>

    <div class="card" id="tg-step2" hidden>
      <h3 class="card-title">Enter the code</h3>
      <p class="card-sub" id="tg-code-hint">Telegram sent a code. Type it below.</p>
      <div class="field"><label class="lab">Login code</label>
        <input type="text" id="tg-code" placeholder="12345" autocomplete="one-time-code" /></div>
      <div class="field" id="tg-2fa-wrap" hidden><label class="lab">2FA password (if enabled)</label>
        <input type="password" id="tg-2fa" placeholder="Your cloud password" /></div>
      <button class="btn btn-primary btn-block" data-act="tg-verify">Verify &amp; log in</button>
      <button class="btn btn-ghost btn-block" data-act="tg-resend">Start over</button>
    </div>

    <div class="card" id="tg-done" hidden>
      <h3 class="card-title">Session active</h3>
      <p class="card-sub">The account is connected and the session is saved. The bot reconnects automatically on restart.</p>
      <dl style="margin:6px 0 0">
        <div class="kv"><dt>User</dt><dd id="tg-done-user">—</dd></div>
        <div class="kv"><dt>Session file</dt><dd id="tg-done-path">—</dd></div>
      </dl>
      <button class="btn btn-primary" style="margin-top:14px" data-act="goto" data-route="forwarding">Create a forwarding rule →</button>
    </div>
  </div>`;
}
/* ---------- RSS sites ---------- */
async function viewRss() {
  const list = await apiSafe("/api/rss/sources");
  const rows = Array.isArray(list) ? list : [];

  const badge = (h) => h === "ok" ? `<span class="badge ok">healthy</span>`
    : h === "warn" ? `<span class="badge warn">failing</span>`
    : h === "error" ? `<span class="badge err">failing</span>`
    : `<span class="badge">paused</span>`;

  return `
  <div class="card">
    <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">
      <div><h3 class="card-title">RSS feeds</h3>
      <p class="card-sub" style="margin:0">News sources the bot scrapes. Failing feeds show their error count.</p></div>
      <button class="btn btn-sm" style="margin-left:auto" data-act="rss-fetch-all">Fetch all now</button>
      <button class="btn btn-primary btn-sm" data-act="rss-new">+ Add feed</button>
    </div>
  </div>

  <div class="card" style="margin-top:16px" id="rssForm" hidden>
    <h3 class="card-title">Add an RSS feed</h3>
    <div class="grid grid-2">
      <div class="field"><label class="lab">Name</label><input type="text" id="rs-name" placeholder="ForexLive" /></div>
      <div class="field"><label class="lab">Feed URL</label><input type="url" id="rs-url" placeholder="https://www.forexlive.com/feed/" /></div>
      <div class="field"><label class="lab">Fetch interval (minutes)</label>
        <input type="number" id="rs-interval" min="1" placeholder="system default" /></div>
      <div class="field"><label class="lab">Active</label><label class="switch"><input type="checkbox" id="rs-active" checked /><i></i></label></div>
      <div class="field" style="grid-column:1/-1"><label class="lab">Description (optional)</label>
        <input type="text" id="rs-desc" placeholder="What this feed covers" /></div>
    </div>
    <button class="btn btn-primary" data-act="rss-save">Save feed</button>
    <button class="btn btn-ghost" data-act="rss-cancel">Cancel</button>
  </div>

  ${rows.length === 0
    ? `<div class="card" style="margin-top:16px"><div class="empty"><span class="ico">❖</span>
        No RSS feeds configured. Add one to start collecting news.</div></div>`
    : `<div class="card" style="margin-top:16px"><div class="table-wrap"><table>
      <thead><tr><th>Feed</th><th>Status</th><th>Interval</th><th>Last fetch</th><th>Errors</th><th>Items</th><th></th></tr></thead>
      <tbody>
      ${rows.map((r) => `<tr>
        <td><strong>${esc(r.sourceName)}</strong><div style="font-family:var(--mono);font-size:11px;color:var(--text-3);max-width:280px;overflow:hidden;text-overflow:ellipsis">${esc(r.url)}</div></td>
        <td>${badge(r.health)}</td>
        <td>${r.fetchIntervalMinutes ? r.fetchIntervalMinutes + "m" : "default"}</td>
        <td style="font-family:var(--mono);font-size:11px;color:var(--text-3)">${r.lastSuccessfulFetchAt ? new Date(r.lastSuccessfulFetchAt).toLocaleString() : "never"}</td>
        <td>${r.fetchErrorCount ? `<span class="badge ${r.fetchErrorCount > 2 ? "err" : "warn"}">${r.fetchErrorCount}</span>` : "0"}</td>
        <td>${r.newsItemCount ?? 0}</td>
        <td style="white-space:nowrap">
          <button class="btn btn-sm" data-act="rss-fetch" data-id="${r.id}">Fetch</button>
          <button class="btn btn-sm" data-act="rss-toggle" data-id="${r.id}" data-active="${r.isActive}">${r.isActive ? "Pause" : "Resume"}</button>
          <button class="btn btn-danger btn-sm" data-act="rss-del" data-id="${r.id}">Delete</button>
        </td>
      </tr>`).join("")}
      </tbody></table></div></div>`}`;
}

/* ---------- AI analysis settings ---------- */
async function viewAi() {
  const list = await apiSafe("/api/ai/config");
  const rows = Array.isArray(list) ? list : [];

  return `
  <div class="card">
    <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">
      <div><h3 class="card-title">AI providers</h3>
      <p class="card-sub" style="margin:0">Each provider stores its key, model and the analysis prompt template it runs.</p></div>
      <button class="btn btn-primary btn-sm" style="margin-left:auto" data-act="ai-new">+ Add provider</button>
    </div>
  </div>

  <div class="card" style="margin-top:16px" id="aiForm" hidden>
    <h3 class="card-title">Configure an AI provider</h3>
    <div class="grid grid-2">
      <div class="field"><label class="lab">Provider name</label>
        <input type="text" id="ai-provider" placeholder="Gemini" /></div>
      <div class="field"><label class="lab">Model</label>
        <input type="text" id="ai-model" placeholder="gemini-2.0-flash" /></div>
      <div class="field"><label class="lab">API key</label>
        <input type="password" id="ai-key" placeholder="AIza…" /></div>
      <div class="field"><label class="lab">Enabled</label>
        <label class="switch"><input type="checkbox" id="ai-enabled" checked /><i></i></label></div>
      <div class="field" style="grid-column:1/-1"><label class="lab">Analysis prompt template</label>
        <textarea id="ai-prompt" rows="5" placeholder="You are a forex analyst. Summarise the following news into a signal…"></textarea>
        <div class="hint">This template is sent with every news item this provider analyses.</div></div>
      <div class="field" style="grid-column:1/-1"><label class="lab">Description (optional)</label>
        <input type="text" id="ai-desc" placeholder="Primary analysis model" /></div>
    </div>
    <button class="btn btn-primary" data-act="ai-save">Save provider</button>
    <button class="btn btn-ghost" data-act="ai-cancel">Cancel</button>
  </div>

  ${rows.length === 0
    ? `<div class="card" style="margin-top:16px"><div class="empty"><span class="ico">✦</span>
        No AI providers configured. Add one to enable news analysis.</div></div>`
    : rows.map((p) => `<div class="rule" style="margin-top:12px">
      <div class="rule-head">
        <label class="switch"><input type="checkbox" ${p.isEnabled ? "checked" : ""} data-act="ai-toggle" data-id="${p.id}" /><i></i></label>
        <strong>${esc(p.providerName)}</strong>
        <span class="badge ${p.isEnabled ? "ok" : ""}">${p.isEnabled ? "enabled" : "disabled"}</span>
        <span class="badge" style="font-family:var(--mono)">${esc(p.modelName)}</span>
        <span class="badge" style="font-family:var(--mono)">${esc(p.apiKeyMasked || "no key")}</span>
        <button class="btn btn-sm" style="margin-left:auto" data-act="ai-edit" data-id="${p.id}">Edit</button>
        <button class="btn btn-danger btn-sm" data-act="ai-del" data-id="${p.id}">Delete</button>
      </div>
      ${p.promptTemplate ? `<div class="rule-meta" style="white-space:pre-wrap;font-family:var(--sans);font-size:12px;color:var(--text-2)">${esc(p.promptTemplate.slice(0, 240))}${p.promptTemplate.length > 240 ? "…" : ""}</div>` : `<div class="rule-meta">No prompt template set.</div>`}
    </div>`).join("")}`;
}

/* ---------- Force Join ---------- */
let fjData = null;
async function viewForceJoin() {
  const s = await apiSafe("/api/telegram/force-join");
  if (s) fjData = s;

  return `
  <div class="grid grid-2">
    <div class="card">
      <div style="display:flex;align-items:center;gap:12px">
        <div><h3 class="card-title">Force Join</h3>
        <p class="card-sub" style="margin:0">Users must join your channel before the bot responds.</p></div>
        <label class="switch" style="margin-left:auto"><input type="checkbox" id="fj-on" ${fjData?.isEnabled ? "checked" : ""} data-act="fj-toggle" /><i></i></label>
      </div>
      <div class="field" style="margin-top:16px"><label class="lab">Required channel ID</label>
        <input type="text" id="fj-channel" value="${esc(fjData?.channelId ? String(fjData.channelId) : "")}" placeholder="-1001234567890" />
        <div class="hint">The numeric ID of the channel users must join. Public channels start with -100.</div></div>
      <div class="field"><label class="lab">Channel link</label>
        <input type="url" id="fj-link" value="${esc(fjData?.channelLink || "")}" placeholder="https://t.me/yourchannel" /></div>
      <div class="field"><label class="lab">Message shown to non-members</label>
        <textarea id="fj-msg" rows="4" placeholder="To use this bot you must first join our channel: {0}">${esc(fjData?.message || "")}</textarea>
        <div class="hint"><code>{0}</code> is replaced with the channel link when the message is sent.</div></div>
      <button class="btn btn-primary" data-act="fj-save">Save settings</button>
    </div>

    <div class="card">
      <h3 class="card-title">How this works</h3>
      <p class="card-sub">The bot checks membership of the configured channel on every message.</p>
      <dl style="margin:6px 0 0">
        <div class="kv"><dt>Status</dt><dd>${fjData?.isEnabled ? "enforcing" : "disabled"}</dd></div>
        <div class="kv"><dt>Channel</dt><dd>${fjData?.channelId ? esc(String(fjData.channelId)) : "not set"}</dd></div>
        <div class="kv"><dt>Link</dt><dd>${esc(fjData?.channelLink || "—")}</dd></div>
      </dl>
      <div class="field" style="margin-top:16px"><label class="lab">Finding your channel ID</label>
        <pre class="code"><span class="c-com"># Add the bot as admin, post a message, then:</span>
curl https://api.telegram.org/bot<span class="c-str">&lt;TOKEN&gt;</span>/getUpdates | jq</pre>
      </div>
      <p class="hint" style="margin-top:14px">Changes apply immediately — the bot reads the updated settings on the next message. No restart needed.</p>
    </div>
  </div>`;
}

async function viewSecrets() {
  return `
  <div class="card">
    <h3 class="card-title">Secrets vault</h3>
    <p class="card-sub">Encrypted at rest with data protection keys in <code>keys/</code>. Never stored in plain text.</p>
    <div class="grid grid-2">
      <div class="field"><label class="lab">Secret key</label>
        <input type="text" id="sk-key" placeholder="TelegramPanel:BotToken" /></div>
      <div class="field"><label class="lab">Secret value</label>
        <input type="password" id="sk-val" placeholder="••••••••••••" /></div>
    </div>
    <button class="btn btn-primary" data-act="save-secret">Store secret</button>
  </div>

  <div class="card" style="margin-top:16px">
    <h3 class="card-title">Reveal a stored secret</h3>
    <div class="grid grid-2">
      <div class="field"><label class="lab">Key</label>
        <input type="text" id="rk-key" placeholder="TelegramPanel:BotToken" /></div>
    </div>
    <button class="btn btn-sm" data-act="reveal-secret">Reveal</button>
    <pre class="code" id="revealOut" style="margin-top:12px" hidden></pre>
  </div>`;
}

/* ---------- Logs ---------- */
async function viewLogs() {
  let lines = [];
  try {
    const j = await api("/api/logs/list?limit=120");
    lines = Array.isArray(j) ? j : (j?.entries || j?.logs || []);
  } catch { lines = []; }

  return `
  <div class="card">
    <h3 class="card-title">Recent log entries</h3>
    <p class="card-sub">Last ${lines.length} entries from the running process.</p>
    ${lines.length === 0
      ? `<div class="empty"><span class="ico">≣</span>No log entries available.</div>`
      : `<pre class="code">${lines.map((l) => esc(typeof l === "string" ? l : JSON.stringify(l))).join("\n")}</pre>`}
  </div>`;
}

/* ---------- Deploy & ports ---------- */
async function viewDeploy() {
  const s = await apiSafe("/api/setup/status");
  return `
  <div class="grid grid-2">
    <div class="card">
      <h3 class="card-title">Port &amp; binding</h3>
      <p class="card-sub">Where the API and this panel listen.</p>
      <dl style="margin:6px 0 0">
        <div class="kv"><dt>Current URLs</dt><dd>${esc(s?.aspNetCoreUrls || "—")}</dd></div>
        <div class="kv"><dt>HTTPS</dt><dd>${s?.httpsConfigured ? "enabled" : "not configured"}</dd></div>
        <div class="kv"><dt>Health check</dt><dd>/healthz</dd></div>
      </dl>
      <div class="field" style="margin-top:16px"><label class="lab">Publish port (docker)</label>
        <input type="text" id="dp-port" value="5000" /></div>
      <div class="field"><label class="lab">Domain / reverse proxy host</label>
        <input type="text" id="dp-domain" placeholder="bot.example.com" /></div>
      <button class="btn btn-primary" data-act="gen-compose">Generate docker-compose</button>
    </div>
    <div class="card">
      <h3 class="card-title">Deployment commands</h3>
      <pre class="code" id="composeOut" style="min-height:300px"><span class="c-com"># fill the form and press “Generate docker-compose”</span></pre>
    </div>
  </div>

  <div class="card" style="margin-top:16px">
    <h3 class="card-title">Caddy reverse proxy (automatic HTTPS)</h3>
    <pre class="code"><span class="c-com"># /etc/caddy/Caddyfile — reload with: sudo systemctl reload caddy</span>
bot.example.com {
    reverse_proxy localhost:<span class="c-str">5000</span>
}</pre>
  </div>`;
}

/* ----------------------------- render ----------------------------- */
const VIEWS = {
  dashboard: viewDashboard, setup: viewSetup, forwarding: viewForwarding,
  rss: viewRss, ai: viewAi, forcejoin: viewForceJoin,
  tglogin: viewTgLogin, channels: viewChannels, settings: viewSettings,
  secrets: viewSecrets, logs: viewLogs, deploy: viewDeploy,
};

function render() {
  const host = $("#view");
  host.innerHTML = `<div class="card" style="text-align:center;color:var(--text-3)"><div class="skel" style="height:18px;width:32%;margin:22px auto"></div></div>`;

  const view = VIEWS[state.route] || viewDashboard;
  Promise.resolve(view()).then((html) => {
    host.innerHTML = html;
    $$("#view [data-act]").forEach(bindActions);
  }).catch((e) => {
    host.innerHTML = `<div class="card"><div class="empty"><span class="ico">⚠</span>${esc(e.message)}</div></div>`;
  });
}

/* ----------------------------- actions ----------------------------- */
function bindActions(el) {
  if (el.dataset.bound) return;
  el.dataset.bound = "1";
  el.addEventListener(el.type === "checkbox" ? "change" : "click", (e) => handleAction(el, e));
}

async function handleAction(el, e) {
  const act = el.dataset.act;
  if (act === "goto") return go(el.dataset.route);

  if (act === "migrate") {
    const prov = state.status?.databaseProvider || "postgres";
    toast("Applying migrations", `Provider: ${prov}`);
    const r = await apiSafe("/api/setup/database", {
      method: "POST",
      body: { databaseProvider: prov, connectionString: "", applySeedData: true, overwriteExistingSeed: false },
    });
    if (r) toast("Database ready", r.message || "migrations applied", r.success === false ? "err" : "ok");
    return render();
  }

  if (act === "switch-db") {
    const prov = prompt("Database provider — postgres, sqlserver or sqlite:", state.status?.databaseProvider || "postgres");
    if (!prov) return;
    const conn = prompt("Connection string (leave empty to keep current):", "");
    const r = await apiSafe("/api/setup/database", {
      method: "POST",
      body: { databaseProvider: prov, connectionString: conn || "", applySeedData: true, overwriteExistingSeed: false },
    });
    if (r) toast("Database ready", r.message || "applied", r.success === false ? "err" : "ok");
    return render();
  }

  if (act === "test-redis") {
    const r = await apiSafe("/api/setup/redis/test", { method: "POST" });
    if (!r) return;
    if (r.success) toast("Redis OK", `${r.endpoint} — ${r.latencyMs}ms, v${r.serverVersion}`, "ok");
    else toast("Redis unreachable", (r.errors || []).join(" ") || r.message, "warn");
    return;
  }

  if (act === "test-telegram") {
    const r = await apiSafe("/api/setup/telegram/test", { method: "POST" });
    if (!r) return;
    if (r.success) toast("Telegram OK", `Connected as @${r.botUserName} (id ${r.botId})`, "ok");
    else toast("Telegram failed", (r.errors || []).join(" ") || r.message, "err");
    return;
  }

  /* ---- System toolbar: restart & self-update ---- */
  if (act === "sys-restart") {
    if (!confirm("Restart the application now? New configuration takes effect on restart.")) return;
    const r = await apiSafe("/api/system/restart?delaySeconds=2", { method: "POST" });
    if (!r) return;
    toast("Restarting", r.message || "The app is cycling. The panel reconnects automatically.", "warn");
    // Poll health until the process is back.
    setTimeout(() => refreshHealth(), 2500);
    return;
  }

  if (act === "sys-update") {
    if (!confirm("Pull the newest release and update this installation?")) return;
    toast("Updating", "Running the update script…");
    const r = await apiSafe("/api/system/update", { method: "POST" });
    if (!r) return;
    if (r.success) toast("Update complete", r.message, "ok");
    else toast("Update unavailable", r.message, "warn");
    return;
  }

  /* ---- RSS ---- */
  if (act === "rss-new") { $("#rssForm").hidden = false; return; }
  if (act === "rss-cancel") { $("#rssForm").hidden = true; return; }

  if (act === "rss-save") {
    const name = $("#rs-name").value.trim();
    const url = $("#rs-url").value.trim();
    if (!name || !url) return toast("Missing fields", "Name and feed URL are required.", "err");
    const interval = parseInt($("#rs-interval").value, 10);
    const r = await apiSafe("/api/rss/sources", {
      method: "POST",
      body: {
        sourceName: name, url, isActive: $("#rs-active").checked,
        description: $("#rs-desc").value.trim() || null,
        fetchIntervalMinutes: isNaN(interval) || interval < 1 ? null : interval,
      },
    });
    if (r !== null) { toast("Feed added", name, "ok"); render(); }
    return;
  }

  if (act === "rss-fetch") {
    toast("Fetching", "Reading the feed now…");
    const r = await apiSafe(`/api/admin/rss/fetch?rssSourceId=${encodeURIComponent(el.dataset.id)}&forceFetch=true`, { method: "POST" });
    if (r !== null) { toast("Fetch complete", "Items collected from the feed.", "ok"); render(); }
    return;
  }

  if (act === "rss-fetch-all") {
    toast("Fetching all", "Reading every active feed…");
    const r = await apiSafe("/api/admin/rss/fetch?forceFetch=true", { method: "POST" });
    if (r !== null) { toast("Fetch complete", "All active feeds read.", "ok"); render(); }
    return;
  }

  if (act === "rss-toggle") {
    const next = el.dataset.active !== "true";
    const r = await apiSafe(`/api/rss/sources/${encodeURIComponent(el.dataset.id)}/toggle?active=${next}`, { method: "POST" });
    if (r !== null) { toast("Feed updated", next ? "Resumed" : "Paused", "ok"); render(); }
    return;
  }

  if (act === "rss-del") {
    if (!confirm("Delete this feed? Its collected items stay in the database.")) return;
    const r = await apiSafe(`/api/rss/sources/${encodeURIComponent(el.dataset.id)}`, { method: "DELETE" });
    if (r !== null) { toast("Feed deleted", "", "ok"); render(); }
    return;
  }

  /* ---- AI providers ---- */
  if (act === "ai-new") { $("#aiForm").hidden = false; return; }
  if (act === "ai-cancel") { $("#aiForm").hidden = true; return; }

  if (act === "ai-save") {
    const provider = $("#ai-provider").value.trim();
    const model = $("#ai-model").value.trim();
    const key = $("#ai-key").value;
    if (!provider || !model || !key) return toast("Missing fields", "Provider, model and API key are required.", "err");
    const r = await apiSafe("/api/ai/config", {
      method: "POST",
      body: {
        providerName: provider, modelName: model, apiKey: key,
        isEnabled: $("#ai-enabled").checked,
        promptTemplate: $("#ai-prompt").value,
        description: $("#ai-desc").value.trim() || null,
      },
    });
    if (r !== null) { toast("Provider saved", `${provider} / ${model}`, "ok"); render(); }
    return;
  }

  if (act === "ai-toggle") {
    const r = await apiSafe(`/api/ai/config/${encodeURIComponent(el.dataset.id)}/toggle?enabled=${el.checked}`, { method: "POST" });
    if (r !== null) toast("Provider updated", el.checked ? "Enabled" : "Disabled", "ok");
    else render();
    return;
  }

  if (act === "ai-edit") {
    // Load the provider into the form so the admin can change the prompt without
    // retyping the key — the API key field is required on save.
    const rows = await apiSafe("/api/ai/config");
    const p = (Array.isArray(rows) ? rows : []).find((x) => String(x.id) === String(el.dataset.id));
    if (!p) return;
    $("#aiForm").hidden = false;
    $("#ai-provider").value = p.providerName || "";
    $("#ai-model").value = p.modelName || "";
    $("#ai-prompt").value = p.promptTemplate || "";
    $("#ai-desc").value = p.description || "";
    $("#ai-enabled").checked = !!p.isEnabled;
    $("#ai-key").value = "";
    $("#ai-key").placeholder = "Type the current key to confirm the edit";
    toast("Editing", `${p.providerName} — re-enter the key to save changes.`, "warn");
    return;
  }

  if (act === "ai-del") {
    if (!confirm("Delete this AI provider configuration?")) return;
    const r = await apiSafe(`/api/ai/config/${encodeURIComponent(el.dataset.id)}`, { method: "DELETE" });
    if (r !== null) { toast("Provider deleted", "", "ok"); render(); }
    return;
  }

  /* ---- Force Join ---- */
  if (act === "fj-toggle") {
    const r = await apiSafe(`/api/telegram/force-join/toggle?enabled=${el.checked}`, { method: "POST" });
    if (!r) return render();
    toast("Force Join", el.checked ? "Enabled — users must join the channel." : "Disabled.", el.checked ? "ok" : "warn");
    return render();
  }

  if (act === "fj-save") {
    const channelId = parseInt($("#fj-channel").value.trim(), 10);
    const link = $("#fj-link").value.trim();
    const msg = $("#fj-msg").value;
    const enabled = $("#fj-on").checked;
    if (enabled && (!channelId || !link || !msg)) {
      return toast("Missing fields", "Enabled Force Join needs a channel ID, link and message.", "err");
    }
    const r = await apiSafe("/api/telegram/force-join", {
      method: "POST",
      body: { isEnabled: enabled, channelId: isNaN(channelId) ? 0 : channelId, channelLink: link, message: msg },
    });
    if (r !== null) { toast("Settings saved", enabled ? "Force Join is active." : "Force Join is off.", "ok"); render(); }
    return;
  }

  if (act === "new-rule") { $("#ruleForm").hidden = false; return; }
  if (act === "cancel-rule") { $("#ruleForm").hidden = true; return; }

  /* ---- Telegram login flow: phone → code → 2FA ---- */
  if (act === "tg-start") {
    const apiId = $("#tg-apiid").value.trim();
    const apiHash = $("#tg-apihash").value.trim();
    const phone = $("#tg-phone").value.trim();
    if (!apiId || !apiHash || !phone) return toast("Missing fields", "API ID, API hash and phone number are all required.", "err");

    const btn = el; btn.disabled = true; btn.textContent = "Sending code…";
    const r = await apiSafe("/api/telegram/login/start", {
      method: "POST",
      body: { apiId, apiHash, phoneNumber: phone },
    });
    btn.disabled = false; btn.textContent = "Send login code";

    if (!r) return;
    if (!r.success) return toast("Telegram rejected the request", (r.errors || []).join(" ") || r.message, "err");

    tgSession = r.sessionId;
    $("#tg-step2").hidden = false;
    $("#tg-code-hint").textContent = r.message || "Telegram sent a code. Type it below.";
    toast("Code sent", r.message || "Check your Telegram app.", "ok");
    return;
  }

  if (act === "tg-resend") {
    tgSession = null;
    $("#tg-step2").hidden = true;
    $("#tg-2fa-wrap").hidden = true;
    $("#tg-code").value = "";
    $("#tg-2fa").value = "";
    return;
  }

  if (act === "tg-verify") {
    const code = $("#tg-code").value.trim();
    const pass = $("#tg-2fa").value;
    if (!tgSession) { toast("Session expired", "Start the login again.", "err"); return go("tglogin"); }
    if (!code && !pass) return toast("Nothing to verify", "Enter the code Telegram sent you.", "err");

    const btn = el; btn.disabled = true; btn.textContent = "Verifying…";
    const r = await apiSafe("/api/telegram/login/verify", {
      method: "POST",
      body: { sessionId: tgSession, verificationCode: code || null, twoFactorPassword: pass || null },
    });
    btn.disabled = false; btn.textContent = "Verify & log in";

    if (!r) return;

    if (r.success) {
      $("#tg-step2").hidden = true;
      $("#tg-done").hidden = false;
      $("#tg-done-user").textContent = r.botUserName ? "@" + r.botUserName : "session saved";
      $("#tg-done-path").textContent = r.sessionFilePath || "";
      toast("Logged in", "Telegram session is active and saved.", "ok");
      return;
    }

    // Telegram may want the 2FA password next — reveal that field and keep the flow open.
    if (r.needsTwoFactorPassword) {
      $("#tg-2fa-wrap").hidden = false;
      $("#tg-code-hint").textContent = r.message || "This account has 2FA. Enter your cloud password.";
      toast("2FA required", r.message || "Enter your 2FA password.", "warn");
      return;
    }

    toast("Verification failed", r.message || (r.errors || []).join(" "), "err");
    return;
  }

  if (act === "save-rule") {
    const name = $("#f-name").value.trim();
    const src = $("#f-src").value.trim();
    const dst = $("#f-dst").value.split(",").map((x) => x.trim()).filter(Boolean);
    if (!name || !src || dst.length === 0) return toast("Missing fields", "Name, source and at least one target are required.", "err");

    const body = {
      ruleName: name,
      isEnabled: $("#f-on").checked,
      sourceChannelId: src,
      targetChannelIds: dst,
      editOptions: {
        prependText: null, appendText: null, textReplacements: [],
        removeSourceForwardHeader: true, removeLinks: false, stripFormatting: false,
        customFooter: null, dropAuthor: false, dropMediaCaptions: false, noForwards: false,
      },
      filterOptions: {
        allowedMessageTypes: [], allowedMimeTypes: [],
        allowedSenderUserIds: [], blockedSenderUserIds: [],
        containsText: null, containsTextIsRegex: false, containsTextRegexOptions: 0,
        ignoreEditedMessages: false, ignoreServiceMessages: true,
        maxMessageLength: null, minMessageLength: null,
      },
    };
    const r = await apiSafe("/api/v2/Forwarding/rules", { method: "POST", body });
    if (r !== null) { toast("Rule created", name, "ok"); render(); }
    return;
  }

  if (act === "del-rule") {
    if (!confirm(`Delete rule "${el.dataset.name}"?`)) return;
    const r = await apiSafe(`/api/v2/Forwarding/rules/${encodeURIComponent(el.dataset.name)}`, { method: "DELETE" });
    if (r !== null) { toast("Rule deleted", el.dataset.name, "ok"); render(); }
    return;
  }

  if (act === "toggle-rule") {
    const r = await apiSafe(`/api/v2/Forwarding/rules/${encodeURIComponent(el.dataset.name)}`, { method: "PUT", body: { isEnabled: el.checked } });
    if (r === null) return render();
    toast("Rule updated", `${el.dataset.name} → ${el.checked ? "enabled" : "disabled"}`, "ok");
    return render();
  }

  if (act === "save-settings") {
    const payload = $$("#view input[data-key]").map((i) => ({ key: i.dataset.key, value: i.value }));
    const map = {};
    payload.forEach((p) => { map[p.key] = p.value; });
    const r = await apiSafe("/api/settings/update", { method: "POST", body: { settingsToUpdate: map } });
    if (r !== null) toast("Settings saved", `${payload.length} value(s) persisted`, "ok");
    return;
  }

  if (act === "save-secret") {
    const key = $("#sk-key").value.trim();
    const val = $("#sk-val").value;
    if (!key || !val) return toast("Missing fields", "Both key and value are required.", "err");
    const r = await apiSafe(`/api/secrets/${encodeURIComponent(key)}`, {
      method: "PUT", body: { value: val },
    });
    if (r !== null) { toast("Secret stored", key, "ok"); $("#sk-key").value = ""; $("#sk-val").value = ""; }
    return;
  }

  if (act === "reveal-secret") {
    const key = $("#rk-key").value.trim();
    if (!key) return;
    const r = await apiSafe(`/api/secrets/${encodeURIComponent(key)}/reveal`);
    const out = $("#revealOut");
    if (r === null) { out.hidden = true; return; }
    out.hidden = false;
    out.textContent = typeof r === "string" ? r : (r.value ?? JSON.stringify(r, null, 2));
    return;
  }

  if (act === "gen-compose") {
    const port = $("#dp-port").value.trim() || "5000";
    const domain = $("#dp-domain").value.trim();
    const compose = `version: "3.9"
services:
  bot:
    image: ghcr.io/opselon/forextradingbot:latest
    restart: unless-stopped
    ports:
      - "${port}:5000"
    environment:
      ASPNETCORE_URLS: "http://+:5000"
      DatabaseSettings__DatabaseProvider: "postgres"
      ConnectionStrings__DefaultConnection: "Host=postgres;Database=forexbotdb;Username=forexbot;Password=change-me"
      ConnectionStrings__Redis: "redis:6379"
    volumes:
      - bot-keys:/app/keys
      - bot-logs:/app/logs
    depends_on: [postgres, redis]

  postgres:
    image: postgres:15-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: forexbot
      POSTGRES_PASSWORD: change-me
      POSTGRES_DB: forexbotdb
    volumes:
      - pg-data:/var/lib/postgresql/data

  redis:
    image: redis:7-alpine
    restart: unless-stopped

${domain ? `  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports: ["80:80", "443:443"]
    volumes:
      - caddy-data:/data
      - caddy-config:/config
    command: caddy reverse-proxy --from ${domain} --to bot:5000
` : ""}volumes:
  bot-keys:
  bot-logs:
  pg-data:
${domain ? "  caddy-data:\n  caddy-config:\n" : ""}`;
    $("#composeOut").innerHTML = `<span class="c-com"># docker-compose.yml — save and run: docker compose up -d</span>\n` +
      esc(compose).replace(/"([^"]+)"/g, '<span class="c-str">"$1"</span>');
    toast("Generated", "docker-compose.yml is ready to copy", "ok");
    return;
  }
}

/* ----------------------------- boot ----------------------------- */
refreshHealth();
setInterval(refreshHealth, 20000);

const hash = (location.hash || "").replace("#/", "");
go(TITLES[hash] ? hash : "dashboard");
})();
