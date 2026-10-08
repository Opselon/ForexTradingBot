/* ============================================================
   views/console.js — log file console.

   Reads real log files from /api/log. There is no websocket stream in
   the backend, so this view polls the selected file on an interval and
   appends only new lines. It does not fabricate output.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
const T = window.FtbPanel ? window.FtbPanel.toast : () => {};

const POLL_MS = 4000;
const MAX_RENDER = 1200; // keep the DOM bounded on very large files

let files = [];
let current = null;
let level = "ALL";
let query = "";
let paused = false;
let autoScroll = true;
let rendered = 0;
let pollTimer = null;
let lastSize = 0;
let buffer = [];

/* ------------------------------ parsing ------------------------------ */
/* Serilog lines look like: [22:30:33 INF] (Source) Message text */
const LINE_RE = /^\[?(\d{2}:\d{2}:\d{2}(?:\.\d+)?)?\]?\s*(VRB|DBG|INF|WRN|ERR|FTL|INF\(.+?\)|WRN\(.+?\)|ERR\(.+?\)|FTL\(.+?\))?\]?\s*(.*)$/;

function parseLine(raw) {
  const text = String(raw || "");
  if (!text.trim()) return null;
  const m = LINE_RE.exec(text);
  if (!m) return { time: "", level: "INF", message: text, raw: text };

  let lvl = m[2] || "INF";
  // "INF(Source)" → INF, with the source kept for the message line.
  const sourceMatch = /^([A-Z]+)\((.*)\)$/.exec(lvl);
  let source = "";
  if (sourceMatch) { lvl = sourceMatch[1]; source = sourceMatch[2]; }

  let message = m[3] || "";
  if (source && !message.startsWith("(")) message = `(${source}) ${message}`;

  return { time: m[1] || "", level: lvl, message, raw: text };
}

function levelClass(lvl) {
  switch (String(lvl).toUpperCase()) {
    case "ERR":
    case "FTL": return "err";
    case "WRN": return "warn";
    case "DBG":
    case "VRB": return "dbg";
    default: return "";
  }
}

function levelPasses(lvl) {
  if (level === "ALL") return true;
  if (level === "ERR") return ["ERR", "FTL"].includes(String(lvl).toUpperCase());
  if (level === "WARN") return ["ERR", "FTL", "WRN"].includes(String(lvl).toUpperCase());
  if (level === "INFO") return ["INF", "WRN", "ERR", "FTL"].includes(String(lvl).toUpperCase());
  return true;
}

function highlight(text, q) {
  if (!q) return U.esc(text);
  const idx = text.toLowerCase().indexOf(q.toLowerCase());
  if (idx === -1) return U.esc(text);
  const before = text.slice(0, idx);
  const hit = text.slice(idx, idx + q.length);
  const after = text.slice(idx + q.length);
  return `${U.esc(before)}<span class="hl">${U.esc(hit)}</span>${U.esc(after)}`;
}

/* ------------------------------ render ------------------------------ */
function renderBuffer() {
  const box = document.getElementById("consoleBody");
  if (!box) return;

  const q = query.trim().toLowerCase();
  const lines = buffer
    .map(parseLine)
    .filter((l) => l && levelPasses(l.level))
    .filter((l) => !q || l.raw.toLowerCase().includes(q))
    .slice(-MAX_RENDER);

  box.innerHTML = lines.map((l) =>
    `<div class="log-line ${levelClass(l.level)}">
      <span class="lt">${U.esc(l.time)}</span>
      <span class="lv">${U.esc(l.level)}</span>
      <span class="lm">${highlight(l.message, q)}</span>
    </div>`).join("") || `<div class="state"><div class="state-art">${U.icon("logs")}</div>
      <h3>No matching lines</h3><p>Nothing in this file matches the current level and search filters.</p></div>`;

  rendered = lines.length;
  const countEl = document.getElementById("consoleCount");
  if (countEl) countEl.textContent = `${lines.length} shown · ${buffer.length} loaded`;

  if (autoScroll && !paused) {
    box.scrollTop = box.scrollHeight;
  }
}

function renderShell() {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>Console</h2><p>Live tail of the application log file. Real output only.</p></div>
      <span class="spacer"></span>
    </div>
    <div class="card">
      <div class="console-bar">
        <div class="field" style="margin:0;min-width:170px">
          <select id="consoleFile">${files.map((f) =>
            `<option value="${U.esc(f)}" ${f === current ? "selected" : ""}>${U.esc(f)}</option>`).join("")}</select>
        </div>
        <div class="seg" id="consoleLevels">
          ${["ALL", "INFO", "WARN", "ERR"].map((lv) =>
            `<button type="button" data-lv="${lv}" class="${level === lv ? "on" : ""}">${lv === "ERR" ? "ERRORS" : lv === "WARN" ? "WARN+" : lv === "INFO" ? "INFO+" : "ALL"}</button>`).join("")}
        </div>
        <input type="search" id="consoleSearch" placeholder="Search lines…" value="${U.esc(query)}" autocomplete="off">
        <span class="spacer"></span>
        <span class="pill muted" id="consoleCount">0 lines</span>
        <button class="btn btn-sm" data-act="con-pause">${U.icon(paused ? "play" : "pause")} ${paused ? "Resume" : "Pause"}</button>
        <button class="btn btn-sm" data-act="con-download">${U.icon("download")} Zip</button>
      </div>
      <div class="console" id="consoleBody"></div>
    </div>
  </div>`;
}

/* ------------------------------ data ------------------------------ */
async function loadFiles() {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.logs.list(), undefined);
  if (!r.ok) {
    files = [];
    renderShell();
    const box = document.getElementById("consoleBody");
    if (box) box.innerHTML = U.errorState(r, { title: "Could not list log files", retry: true });
    return;
  }
  const list = Array.isArray(r.data) ? r.data : [];
  // Newest first: the daily files sort lexically by date.
  files = list.slice().sort().reverse();
  if (!files.includes(current)) current = files[0] || null;
  renderShell();
  await loadCurrent();
}

async function loadCurrent() {
  if (!current) {
    const box = document.getElementById("consoleBody");
    if (box) box.innerHTML = U.empty("logs", "No log files yet",
      "Log files appear here once the application has written its first line.");
    return;
  }

  const api = window.FtbApi;
  // Fetch the tail: 1000 recent lines is enough context without hauling a huge file.
  const r = await api.call(api.ENDPOINTS.logs.view(current, 1000));
  if (!r.ok) {
    renderShell();
    const box = document.getElementById("consoleBody");
    if (box) box.innerHTML = U.errorState(r, { title: "Could not read this log file", retry: true });
    return;
  }

  const text = typeof r.data === "string" ? r.data
    : (r.data && (r.data.content || r.data.lines)) ? String(r.data.content || (Array.isArray(r.data.lines) ? r.data.lines.join("\n") : ""))
    : "";
  buffer = String(text || "").split("\n").filter((l) => l.length);
  lastSize = buffer.length;
  renderBuffer();
}

async function pollOnce() {
  if (paused || !current) return;
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.logs.view(current, 400));
  if (!r.ok) return;
  const text = typeof r.data === "string" ? r.data
    : (r.data && (r.data.content || r.data.lines)) ? String(r.data.content || (Array.isArray(r.data.lines) ? r.data.lines.join("\n") : ""))
    : "";
  const lines = String(text || "").split("\n").filter((l) => l.length);
  if (lines.length === lastSize) return; // nothing new

  // Append only the delta so the scroll position and DOM stay stable.
  const delta = lines.slice(lastSize);
  buffer = buffer.concat(delta).slice(-MAX_RENDER * 2);
  lastSize = lines.length;
  renderBuffer();
}

function startPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(pollOnce, POLL_MS);
}

/* ------------------------------ actions ------------------------------ */
const actions = {
  "con-pause": () => { paused = !paused; renderShell(); renderBuffer(); },
  "con-download": () => { window.open("/api/log/zip", "_blank"); },
};

function bind() {
  const sel = document.getElementById("consoleFile");
  if (sel) sel.addEventListener("change", () => {
    current = sel.value;
    buffer = [];
    lastSize = 0;
    renderShell();
    loadCurrent();
  });

  const seg = document.getElementById("consoleLevels");
  if (seg) seg.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-lv]");
    if (!b) return;
    level = b.dataset.lv;
    renderShell();
    renderBuffer();
    bind();
  });

  const search = document.getElementById("consoleSearch");
  if (search) search.addEventListener("input", U.debounce(() => {
    query = search.value;
    renderBuffer();
  }, 200));

  const box = document.getElementById("consoleBody");
  if (box) box.addEventListener("scroll", () => {
    // If the admin scrolls up, stop forcing the view to the bottom.
    const atEnd = box.scrollHeight - box.scrollTop - box.clientHeight < 40;
    autoScroll = atEnd;
  });
}

async function mount() {
  level = "ALL"; query = ""; paused = false; autoScroll = true;
  buffer = []; lastSize = 0;
  await loadFiles();
  bind();
  startPolling();
}

function unmount() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
  buffer = [];
}

window.FtbViews = window.FtbViews || {};
window.FtbViews.console = { mount, unmount, ...actions };
})();
