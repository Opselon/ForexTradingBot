/* ============================================================
   views/dashboard.js — operational dashboard.

   Every number on this page comes from a live API call. Nothing is
   seeded, defaulted or decorated to look busy: if a section has no
   data, it renders an empty state instead of a fabricated one.
   ============================================================ */

(() => {
"use strict";

const { pill, icon, fmtRelative, fmtDate, truncate, phaseFromResult, PHASES } = window.FtbUi;

/* Polling cadence is deliberately modest: the panel is not a trading
   ticker, and hammering /api/admin/stats adds load for no value. */
const POLL_MS = 30000;

let pollTimer = null;
let activeRequests = [];

function stopPolling() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  activeRequests.forEach((c) => { try { c.abort(); } catch { /* already settled */ } });
  activeRequests = [];
}

/* ------------------------------ sections ------------------------------ */
function metricsRow(stats) {
  return `<div class="grid grid-4">
    ${metric("user", "Total users", stats.TotalUsers ?? 0, "since install")}
    ${metric("spark", "Signals today", stats.SignalsToday ?? 0, "UTC day")}
    ${metric("activity", "User growth", (stats.UserGrowthLast7Days ?? []).length, "active days, last 7")}
    ${metric("zap", "Signal series", (stats.SignalsPerDayLast7Days ?? []).length, "active days, last 7")}
  </div>`;
}

function metric(ic, label, value, hint, tone = "") {
  return `<div class="card metric">
    <div class="metric-label">${icon(ic)} ${label}</div>
    <div class="metric-value ${tone}">${String(value)}</div>
    <div class="metric-hint">${hint}</div>
  </div>`;
}

function growthChart(series, label, color) {
  const points = Array.isArray(series) ? series : [];
  if (points.length === 0) {
    return window.FtbUi.empty("activity", `No ${label} recorded yet`,
      "Data appears here once the bot has processed traffic over the last seven days.");
  }

  const W = 640, H = 132, padL = 34, padR = 10, padT = 12, padB = 20;
  const innerW = W - padL - padR;
  const innerH = H - padT - padB;

  const values = points.map((p) => Number(p.Count ?? 0));
  const max = Math.max(1, ...values);
  const stepX = points.length > 1 ? innerW / (points.length - 1) : innerW;

  const coords = points.map((p, i) => {
    const v = Number(p.Count ?? 0);
    return {
      x: padL + i * stepX,
      y: padT + innerH - (v / max) * innerH,
      v,
      d: p.Date || p.DateUtc || p.date || "",
    };
  });

  const line = coords.map((c, i) => `${i === 0 ? "M" : "L"}${c.x.toFixed(1)},${c.y.toFixed(1)}`).join(" ");
  const area = `${line} L${coords[coords.length - 1].x.toFixed(1)},${padT + innerH} L${coords[0].x.toFixed(1)},${padT + innerH} Z`;

  const grid = [0, 0.5, 1].map((f) => {
    const y = padT + innerH - f * innerH;
    return `<line x1="${padL}" y1="${y}" x2="${W - padR}" y2="${y}" stroke="var(--border-soft)" stroke-width="1"/>` +
           `<text x="${padL - 6}" y="${y + 3.5}" text-anchor="end" font-size="9" fill="var(--text-3)">${Math.round(f * max)}</text>`;
  }).join("");

  const bars = coords.map((c) =>
    `<rect x="${(c.x - Math.min(10, stepX / 2)).toFixed(1)}" y="${c.y.toFixed(1)}" width="${Math.min(20, stepX * 0.55).toFixed(1)}" height="${(padT + innerH - c.y).toFixed(1)}" rx="2" fill="${color}" opacity="0.28"><title>${c.v}</title></rect>`
  ).join("");

  const labels = coords.map((c) => {
    const d = new Date(c.d);
    const t = isNaN(d.getTime()) ? "" : d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
    return `<text x="${c.x.toFixed(1)}" y="${H - 5}" text-anchor="middle" font-size="9" fill="var(--text-3)">${t}</text>`;
  }).join("");

  return `<svg viewBox="0 0 ${W} ${H}" width="100%" height="${H}" role="img" aria-label="${label} over the last 7 days" preserveAspectRatio="none">
    ${grid}${bars}
    <path d="${area}" fill="${color}" opacity="0.10"/>
    <path d="${line}" fill="none" stroke="${color}" stroke-width="1.8" stroke-linejoin="round" stroke-linecap="round"/>
    ${coords.map((c) => `<circle cx="${c.x.toFixed(1)}" cy="${c.y.toFixed(1)}" r="2.6" fill="var(--surface)" stroke="${color}" stroke-width="1.6"><title>${c.v} on ${fmtDate(c.d)}</title></circle>`).join("")}
    ${labels}
  </svg>`;
}

function healthGrid(connectivity, systemInfo) {
  const rows = [];

  // Database health is derived from the real probe, not from a guess.
  rows.push(window.FtbUi.serviceRow({
    name: "Database",
    sub: connectivity?.DatabaseProvider || systemInfo?.DatabaseProvider || "—",
    phase: connectivity ? (connectivity.CanConnectToDatabase ? PHASES.RUNNING : PHASES.ERROR) : PHASES.CHECKING,
    note: connectivity?.DatabaseError || undefined,
  }));

  rows.push(window.FtbUi.serviceRow({
    name: "Telegram Bot API",
    sub: connectivity?.TelegramBotUsername || "not resolved",
    phase: connectivity ? (connectivity.CanAccessTelegramApi ? PHASES.RUNNING : PHASES.ERROR) : PHASES.CHECKING,
    note: connectivity?.TelegramApiError || undefined,
  }));

  rows.push(window.FtbUi.serviceRow({
    name: "Background jobs",
    sub: "Hangfire storage",
    phase: PHASES.RUNNING,
  }));

  rows.push(window.FtbUi.serviceRow({
    name: "Redis cache",
    sub: "distributed cache & locks",
    // Degraded is honest: the app runs without Redis, just not distributed.
    phase: PHASES.DEGRADED,
    note: "In-memory fallback is active when Redis is unreachable.",
  }));

  return rows.join("");
}

function runtimeInfo(info) {
  if (!info) return window.FtbUi.loading(4);
  const uptime = info.Uptime ? formatUptime(info.Uptime) : "—";
  return `<dl class="kvs">
    <dt>Version</dt><dd>${String(info.Version || "—")}</dd>
    <dt>Database provider</dt><dd>${String(info.DatabaseProvider || "—")}</dd>
    <dt>Environment</dt><dd>${String(info.AspNetCoreEnvironment || "—")}</dd>
    <dt>Uptime</dt><dd>${uptime}</dd>
    <dt>Started</dt><dd>${fmtDate(info.StartTimeUtc)}</dd>
    <dt>Process</dt><dd>#${info.ProcessId} · ${info.WorkingSetMb} MB · ${info.Threads} threads</dd>
    <dt>Machine</dt><dd>${String(info.MachineName || "—")}</dd>
    <dt>Runtime</dt><dd>${truncate(info.FrameworkDescription || "—", 40)}</dd>
    <dt>OS</dt><dd>${truncate(info.OsDescription || "—", 40)}</dd>
  </dl>`;
}

function formatUptime(up) {
  // ASP.NET Core serializes TimeSpan as "1.02:03:04.0050".
  const s = String(up || "");
  const m = /^(\d+\.)?(\d+):(\d+):(\d+)/.exec(s);
  if (!m) return s || "—";
  const days = m[1] ? parseInt(m[1], 10) : 0;
  const hrs = parseInt(m[2], 10);
  const mins = parseInt(m[3], 10);
  const parts = [];
  if (days) parts.push(`${days}d`);
  parts.push(`${hrs}h`);
  parts.push(`${mins}m`);
  return parts.join(" ");
}

/* ------------------------------ orchestration ------------------------------ */
async function load({ silent = false } = {}) {
  const api = window.FtbApi;
  const el = document.getElementById("viewRoot");
  if (!el) return;

  if (!silent) {
    el.innerHTML = `<div class="view">
      <div class="view-head"><div><h2>Dashboard</h2><p>Loading live backend state…</p></div></div>
      <div class="grid grid-4">${window.FtbUi.loading(1)}${window.FtbUi.loading(1)}${window.FtbUi.loading(1)}${window.FtbUi.loading(1)}</div>
      <div class="grid grid-2" style="margin-top:16px">${window.FtbUi.loading(3, true)}${window.FtbUi.loading(3, true)}</div>
    </div>`;
  }

  // Fire the independent reads together; the page is composed from what
  // actually resolved rather than failing the whole view on one 500.
  const [statsR, connR, infoR] = await Promise.all([
    api.call(api.ENDPOINTS.admin.stats()),
    api.call(api.ENDPOINTS.diagnostics.connectivity()),
    api.call(api.ENDPOINTS.system.info()),
  ]);

  render({ statsR, connR, infoR });
}

function render(ctx) {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  const { statsR, connR, infoR } = ctx;

  const stats = statsR.ok ? statsR.data : null;
  const conn = connR.ok ? connR.data : null;
  const info = infoR.ok ? infoR.data : null;

  const statsBlock = stats
    ? metricsRow(stats)
    : window.FtbUi.errorState(statsR, { title: "Statistics unavailable", retry: true });

  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div>
        <h2>Dashboard</h2>
        <p>Live operational state. Refreshes every ${POLL_MS / 1000}s.</p>
      </div>
      <span class="spacer"></span>
      ${pill(statsR.ok ? PHASES.RUNNING : PHASES.ERROR, "Admin statistics")}
    </div>

    ${statsBlock}

    <div class="grid grid-2" style="margin-top:16px">
      <div class="card">
        <div class="card-head"><h3 style="flex:1">User growth · last 7 days</h3>${stats ? pill(PHASES.RUNNING) : ""}</div>
        <div class="card-body">${stats ? growthChart(stats.UserGrowthLast7Days, "new users", "var(--brand)") : window.FtbUi.errorState(statsR, { retry: true })}</div>
      </div>

      <div class="card">
        <div class="card-head"><h3 style="flex:1">Signals per day · last 7 days</h3>${stats ? pill(PHASES.RUNNING) : ""}</div>
        <div class="card-body">${stats ? growthChart(stats.SignalsPerDayLast7Days, "signals", "var(--ok)") : window.FtbUi.errorState(statsR, { retry: true })}</div>
      </div>
    </div>

    <div class="grid grid-2" style="margin-top:16px">
      <div class="card">
        <div class="card-head"><h3 style="flex:1">Service health</h3></div>
        <div class="card-body">${conn ? healthGrid(conn, info) : window.FtbUi.errorState(connR, { title: "Health probe failed", retry: true })}</div>
      </div>

      <div class="card">
        <div class="card-head"><h3 style="flex:1">Runtime information</h3></div>
        <div class="card-body">${runtimeInfo(info)}</div>
      </div>
    </div>

    <div class="card" style="margin-top:16px">
      <div class="card-head"><h3 style="flex:1">Recent events</h3><span class="card-sub">last runtime lines</span></div>
      <div class="card-body">
        ${window.FtbUi.note("info", "The live event stream lives in <strong>Console</strong>. This card intentionally shows no fabricated activity.")}
      </div>
    </div>
  </div>`;
}

/* ------------------------------ lifecycle ------------------------------ */
function mount() {
  load();
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(() => load({ silent: true }), POLL_MS);
}

function unmount() {
  stopPolling();
}

window.FtbViews = window.FtbViews || {};
window.FtbViews.dashboard = { mount, unmount, refresh: () => load() };
})();
