/* ============================================================
   views/system.js — core control center.

   Restart and update act on the real process. The view never claims an
   operation finished: it reports the state the backend reported, then
   polls /system/info until the process actually cycles.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
const T = window.FtbPanel ? window.FtbPanel.toast : () => {};

let info = null;
let updateCheck = null;
let phase = U.PHASES.CHECKING;
let pollTimer = null;
let restartDeadline = null;
let busy = false;

async function refreshInfo(silent = false) {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.system.info(), undefined);
  if (r.ok) {
    info = r.data;
    if (restartDeadline && new Date(info.StartTimeUtc).getTime() > restartDeadline) {
      // The process start time moved forward: the restart really happened.
      restartDeadline = null;
      phase = U.PHASES.RUNNING;
      T("ok", "Core restarted", "New configuration is now live.");
      if (!silent) render();
    }
  }
  if (!silent) render();
  return r;
}

function controlCard() {
  const restarting = phase === U.PHASES.RESTARTING || phase === U.PHASES.UPDATING;
  return `<div class="card">
    <div class="card-head"><h3 style="flex:1">Core controls</h3>${U.pill(phase)}</div>
    <div class="card-body stack">
      ${U.note(
        restarting ? "warn" : "info",
        restarting
          ? "An operation is in progress. The panel reconnects automatically when the process is back."
          : "Database and provider changes take effect on restart because the data context and job storage are built once at boot.")}
      <div class="row">
        <button class="btn btn-primary" data-act="sys-restart" ${restarting || busy ? "disabled" : ""}>
          ${U.icon("restart")} Restart core
        </button>
        <button class="btn" data-act="sys-update" ${restarting || busy ? "disabled" : ""}>
          ${U.icon("update")} Update
        </button>
        <button class="btn" data-act="sys-refresh" ${busy ? "disabled" : ""}>
          ${U.icon("refresh")} Refresh status
        </button>
      </div>
    </div>
  </div>`;
}

function updateCard() {
  // No fake "up to date": until the check resolves, this says "not checked yet".
  const c = updateCheck;
  let statusHtml;
  if (!c) {
    statusHtml = `<div class="state"><div class="state-art">◌</div>
      <h3>Update status not checked</h3>
      <p>Click "Check for updates" to compare this install with the latest GitHub release.</p></div>`;
  } else if (c.error) {
    statusHtml = `<div class="state warn"><div class="state-art">⚠</div>
      <h3>Could not check for updates</h3><p>${esc(c.error)}</p></div>`;
  } else if (c.updateAvailable) {
    statusHtml = `<div class="state ok"><div class="state-art">↑</div>
      <h3>Update available — v${esc(c.latestVersion)}</h3>
      <p>You are running <strong>v${esc(c.currentVersion)}</strong>.</p>
      <div class="row" style="margin-top:12px">
        <button class="btn btn-primary" data-act="sys-apply-update" ${busy ? "disabled" : ""}>
          Download &amp; install v${esc(c.latestVersion)}
        </button>
        <a class="btn" href="${esc(c.releaseUrl)}" target="_blank" rel="noopener">Release notes</a>
      </div></div>`;
  } else {
    statusHtml = `<div class="state"><div class="state-art">✓</div>
      <h3>Up to date — v${esc(c.currentVersion)}</h3>
      <p>This is the latest published release${c.latestVersion ? ` (v${esc(c.latestVersion)})` : ""}.</p></div>`;
  }

  return `<div class="card">
    <div class="card-head"><h3 style="flex:1">Software update</h3>
      ${c && !c.error && c.updateAvailable ? U.pill("UPDATE_AVAILABLE") : ""}</div>
    <div class="card-body stack">
      ${statusHtml}
      <div class="row">
        <button class="btn" data-act="sys-check-update" ${busy ? "disabled" : ""}>
          ${U.icon("refresh")} Check for updates
        </button>
      </div>
      ${c && c.releaseNotes ? `<details style="margin-top:8px"><summary>Release notes</summary>
        <div style="white-space:pre-wrap;font-size:0.9em;max-height:220px;overflow:auto">${esc(c.releaseNotes.slice(0, 3000))}</div></details>` : ""}
    </div>
  </div>`;
}

function infoCard() {
  if (!info) return `<div class="card card-body">${U.loading(4)}</div>`;
  const up = formatUptime(info.Uptime);
  return `<div class="card">
    <div class="card-head"><h3 style="flex:1">Runtime</h3><span class="card-sub">live from /api/system/info</span></div>
    <div class="card-body">
      <dl class="kvs">
        <dt>Version</dt><dd>${U.esc(String(info.Version || "—"))}</dd>
        <dt>Status</dt><dd>${U.pill(phase)}</dd>
        <dt>Uptime</dt><dd>${U.esc(up)}</dd>
        <dt>Started at</dt><dd>${U.fmtDate(info.StartTimeUtc)}</dd>
        <dt>Process</dt><dd>#${info.ProcessId} · ${info.WorkingSetMb} MB · ${info.Threads} threads</dd>
        <dt>Database</dt><dd>${U.esc(String(info.DatabaseProvider || "—"))}</dd>
        <dt>Environment</dt><dd>${U.esc(String(info.AspNetCoreEnvironment || "—"))}</dd>
        <dt>Machine</dt><dd>${U.esc(String(info.MachineName || "—"))}</dd>
        <dt>Runtime</dt><dd>${U.truncate(info.FrameworkDescription, 44)}</dd>
        <dt>Operating system</dt><dd>${U.truncate(info.OsDescription, 44)}</dd>
      </dl>
    </div>
  </div>`;
}

function healthCard() {
  return `<div class="card">
    <div class="card-head"><h3 style="flex:1">Dependencies</h3></div>
    <div class="card-body" id="sysHealth">
      <div class="svc"><div><div class="svc-name">HTTP endpoint</div><div class="svc-sub">/healthz</div></div><span class="spacer"></span>${U.pill(U.PHASES.RUNNING)}</div>
      <div class="note info">${U.icon("info")}<div>Dependency probes are on the Dashboard. This card reports only the process you are connected to.</div></div>
    </div>
  </div>`;
}

function render() {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>Core</h2><p>Runtime lifecycle and process controls.</p></div>
    </div>
    <div class="grid grid-2">
      ${controlCard()}
      ${updateCard()}
    </div>
    <div style="margin-top:16px">${infoCard()}</div>
    <div style="margin-top:16px">${healthCard()}</div>
  </div>`;
}

function formatUptime(up) {
  const s = String(up || "");
  const m = /^(\d+\.)?(\d+):(\d+):(\d+)/.exec(s);
  if (!m) return s || "—";
  const days = m[1] ? parseInt(m[1], 10) : 0;
  const parts = [];
  if (days) parts.push(`${days}d`);
  parts.push(`${parseInt(m[2], 10)}h`);
  parts.push(`${parseInt(m[3], 10)}m`);
  return parts.join(" ");
}

async function restart() {
  if (busy) return;
  if (!confirm("Restart the core now? Configuration changes are applied when the process comes back.")) return;
  busy = true;
  phase = U.PHASES.RESTARTING;
  render();

  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.system.restart(2));
  busy = false;
  if (!r.ok) {
    phase = U.PHASES.ERROR;
    render();
    T("err", "Restart failed", (r.error || `HTTP ${r.status}`).slice(0, 160));
    return;
  }
  // Record the boundary so a later poll can prove the process really restarted.
  restartDeadline = Date.now() - 2000;
  phase = U.PHASES.RESTARTING;
  render();
  T("warn", "Restarting", r.data && r.data.message ? r.data.message : "The core is cycling. Reconnecting automatically.");
}

async function update() {
  // "Update" now means: check first, then let the panel show what it found.
  await checkUpdate();
  if (updateCheck && updateCheck.updateAvailable) {
    if (!confirm(`Download and install v${updateCheck.latestVersion}? The app restarts when it finishes.`)) return;
    await applyUpdate();
  }
  return;
}

async function checkUpdate() {
  if (busy) return;
  busy = true;
  render();
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.system.updateCheck());
  busy = false;
  if (r.ok && r.data) {
    updateCheck = r.data;
    if (updateCheck.updateAvailable) {
      T("ok", "Update available", `v${updateCheck.currentVersion} → v${updateCheck.latestVersion}`);
    } else if (!updateCheck.error) {
      T("ok", "Up to date", `Running v${updateCheck.currentVersion}`);
    } else {
      T("warn", "Check failed", String(updateCheck.error).slice(0, 160));
    }
  } else {
    T("err", "Check failed", (r.error || `HTTP ${r.status}`).slice(0, 160));
  }
  render();
}

async function applyUpdate() {
  if (busy) return;
  busy = true;
  phase = U.PHASES.UPDATING;
  render();

  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.system.update());
  busy = false;

  if (!r.ok) {
    phase = U.PHASES.ERROR;
    render();
    const url = r.data && r.data.releasesUrl ? r.data.releasesUrl : null;
    T("err", "Update unavailable", (r.error || `HTTP ${r.status}`).slice(0, 200));
    if (url) window.open(url, "_blank");
    return;
  }

  restartDeadline = Date.now() - 2000;
  phase = U.PHASES.UPDATING;
  render();
  T("ok", "Update started", (r.data && r.data.message) || "The updater is running. The panel reconnects on completion.");
}

function mount() {
  phase = U.PHASES.CHECKING;
  render();
  refreshInfo();
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(() => refreshInfo(true), 5000);
}

function unmount() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
}

window.FtbViews = window.FtbViews || {};
window.FtbViews.system = {
  mount, unmount,
  "sys-restart": restart,
  "sys-update": update,
  "sys-check-update": checkUpdate,
  "sys-apply-update": applyUpdate,
  "sys-refresh": () => { phase = U.PHASES.CHECKING; render(); refreshInfo(); },
};
})();
