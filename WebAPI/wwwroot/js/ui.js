/* ============================================================
   ui.js — view helpers for the control panel.

   These render markup that uses the existing panel.css classes. Views
   compose these instead of writing raw HTML so error/empty/loading
   states are identical everywhere and no view invents its own markup.
   ============================================================ */

(() => {
"use strict";

const esc = (s) => String(s ?? "")
  .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
  .replace(/"/g, "&quot;").replace(/'/g, "&#39;");

/* ------------------------------ runtime phases ------------------------------ */
/* Services are modelled as explicit phases. "Disabled" and "not
   configured" need different guidance, so a boolean cannot express it. */
const PHASES = Object.freeze({
  UNKNOWN:       { id: "unknown",       label: "Unknown",            pill: "muted" },
  CHECKING:      { id: "checking",      label: "Checking…",          pill: "muted" },
  RUNNING:       { id: "running",       label: "Running",            pill: "ok" },
  STOPPED:       { id: "stopped",       label: "Stopped",            pill: "err" },
  STARTING:      { id: "starting",      label: "Starting…",          pill: "warn" },
  RESTARTING:    { id: "restarting",    label: "Restarting…",        pill: "warn" },
  UPDATING:      { id: "updating",      label: "Updating…",          pill: "warn" },
  DEGRADED:      { id: "degraded",      label: "Degraded",           pill: "warn" },
  UNCONFIGURED:  { id: "unconfigured",  label: "Not configured",     pill: "muted" },
  ERROR:         { id: "error",         label: "Error",              pill: "err" },
  UNAUTHORIZED:  { id: "unauthorized",  label: "Sign in required",   pill: "warn" },
  EMPTY:         { id: "empty",         label: "Empty",              pill: "muted" },
});

function phase(id) {
  if (id && typeof id === "object") return id;
  return PHASES[String(id).toUpperCase()] || PHASES.UNKNOWN;
}

/**
 * Derives a phase from an ApiResult so every view describes failure the
 * same way. A 401 is never painted as a hard backend error.
 */
function phaseFromResult(result, opts = {}) {
  if (!result) return PHASES.CHECKING;
  if (result.ok) {
    if (opts.emptyWhenOk && isEmpty(result.data)) return PHASES.EMPTY;
    if (opts.configuredWhenOk && result.data && typeof result.data === "object" && result.data.isEnabled === false) {
      return PHASES.UNCONFIGURED;
    }
    return PHASES.RUNNING;
  }
  if (result.status === 401 || result.status === 403) return PHASES.UNAUTHORIZED;
  if (result.status === 404) return opts.missingIsUnconfigured ? PHASES.UNCONFIGURED : PHASES.ERROR;
  if (result.status === 0 || result.aborted) return PHASES.STOPPED;
  return PHASES.ERROR;
}

function isEmpty(data) {
  if (data === null || data === undefined) return true;
  if (Array.isArray(data)) return data.length === 0;
  if (typeof data === "object") return Object.keys(data).length === 0;
  return false;
}

/* ------------------------------ atoms ------------------------------ */
function pill(ph, note) {
  const p = phase(ph);
  return `<span class="pill ${p.pill}" title="${esc(note || p.label)}"><span class="dot"></span>${esc(p.label)}</span>`;
}

function badge(label, tone = "") {
  return `<span class="badge ${tone}">${esc(label)}</span>`;
}

function icon(name) {
  const glyphs = {
    activity: "↯", alert: "⚠", arrowDown: "↓", arrowUp: "↑", bot: "✆",
    box: "▣", brush: "✎", calendar: "▦", channel: "◈", check: "✓",
    clock: "◷", cloud: "☁", code: "⌗", config: "⚙", copy: "⧉",
    database: "⛁", delete: "✕", download: "⇓", edit: "✎", err: "✕",
    feed: "❖", file: "▤", filter: "∇", forward: "⇄", gauge: "◍",
    key: "⚿", lock: "⛨", logs: "≣", pause: "▮▮", play: "▶",
    plus: "+", refresh: "⟳", restart: "⟳", rss: "❖", save: "⇪",
    search: "⌕", server: "◫", settings: "⚙", shield: "⛨", spark: "✦",
    stop: "■", sync: "⟳", terminal: "⌘", update: "⬆", upload: "⇪",
    user: "☻", warn: "⚠", wrench: "⚒", zap: "↯", x: "✕",
    ok: "✓", info: "ℹ", lockOpen: "⚿", send: "➤", trash: "🗑",
  };
  return `<span class="ico" aria-hidden="true">${esc(glyphs[name] || "•")}</span>`;
}

function btn({ label, act, tone = "", size = "", icon: ic, disabled = false, kind = "btn" }) {
  const cls = [kind, tone, size].filter(Boolean).join(" ");
  return `<button type="button" class="${esc(cls)}" data-act="${esc(act)}" ${disabled ? "disabled" : ""}>${ic ? icon(ic) + " " : ""}${esc(label)}</button>`;
}

/* ------------------------------ states ------------------------------ */
/**
 * The empty-state renderer. Mock data is forbidden by design, so a
 * meaningful empty state is a first-class UI rather than a fallback.
 */
function empty(iconName, title, message, actionHtml = "") {
  return `<div class="state"><div class="state-art">${icon(iconName)}</div>
    <h3>${esc(title)}</h3><p>${esc(message || "")}</p>${actionHtml}</div>`;
}

/**
 * Renders a failure honestly with the server's own reason, plus a retry
 * when the caller supports one. Never substitutes placeholder data.
 */
function errorState(result, { title = "Could not load this section", retry = false } = {}) {
  const auth = result && (result.status === 401 || result.status === 403);
  const reason = result ? (result.error || `HTTP ${result.status}`) : "No response";
  return `<div class="state ${auth ? "auth" : "err"}">
    <div class="state-art">${icon(auth ? "key" : "alert")}</div>
    <h3>${esc(auth ? "Sign in required" : title)}</h3>
    <p>${esc(String(reason)).slice(0, 300)}</p>
    ${retry ? btn({ label: "Try again", act: "__retry", icon: "refresh" }) : ""}
  </div>`;
}

function loading(lines = 3, card = false) {
  const inner = Array.from({ length: lines }, (_, i) =>
    `<div class="skel skel-line" style="width:${[100, 84, 66, 92][i % 4]}%"></div>`).join("");
  return card ? `<div class="card skel-card">${inner}</div>` : inner;
}

/* ------------------------------ tables ------------------------------ */
/**
 * @param {Array} columns  [{key,label,render,numeric,cellClass}]
 * @param {Array} rows     plain objects
 * @param {function} rowKey (row, i) => string
 */
function table({ columns, rows, rowKey = (r, i) => r.id ?? i, dense = false, emptyHtml = null }) {
  if (!rows || rows.length === 0) {
    return emptyHtml || empty("box", "Nothing here yet", "No rows to display.");
  }
  const heads = columns.map((c) =>
    `<th class="${c.numeric ? "num" : ""}">${esc(c.label)}</th>`).join("");
  const body = rows.map((row, i) => {
    const cells = columns.map((c) => {
      const val = c.render ? c.render(row[c.key], row, i) : esc(row[c.key] ?? "");
      return `<td class="${c.numeric ? "num" : ""} ${c.cellClass || ""}">${val}</td>`;
    }).join("");
    return `<tr data-id="${esc(String(rowKey(row, i)))}">${cells}</tr>`;
  }).join("");
  return `<div class="table-wrap"><table class="${dense ? "dense" : ""}">
    <thead><tr>${heads}</tr></thead><tbody>${body}</tbody></table></div>`;
}

/* ------------------------------ forms ------------------------------ */
function field({ id, label, type = "text", value = "", placeholder = "", hint = "", required = false, rows, mono = false, isSecret = false }) {
  const tag = rows ? "textarea" : "input";
  const classes = [rows ? "" : "", isSecret ? "secret" : "", mono ? "" : ""].filter(Boolean).join(" ");
  const attrs = [
    `id="${esc(id)}"`, `name="${esc(id)}"`, `type="${esc(rows ? "" : type)}"`,
    `placeholder="${esc(placeholder)}"`, "autocomplete=\"off\"",
    required ? "required" : "",
    classes ? `class="${esc(classes)}"` : "",
  ].filter(Boolean);
  if (!rows) attrs.push(`value="${esc(value ?? "")}"`);
  if (rows) attrs.push(`rows="${rows}"`);
  const body = rows ? esc(value ?? "") : "";
  return `<div class="field" data-field="${esc(id)}">
    <label for="${esc(id)}">${esc(label)}${required ? ' <span style="color:var(--err)">*</span>' : ""}</label>
    <${tag} ${attrs.join(" ")}>${body}</${tag}>
    ${hint ? `<p class="hint">${esc(hint)}</p>` : ""}
  </div>`;
}

function switchToggle(id, checked = false, label = "") {
  return `<label class="switch" for="${esc(id)}">
    <input type="checkbox" id="${esc(id)}" ${checked ? "checked" : ""}>
    <span class="switch-track" aria-hidden="true"></span>
    ${label ? `<span class="switch-label">${esc(label)}</span>` : ""}
  </label>`;
}

function note(kind, html, ic = "warn") {
  return `<div class="note ${esc(kind)}">${icon(ic)}<div>${html}</div></div>`;
}

/* ------------------------------ service row ------------------------------ */
function serviceRow({ name, sub, phase: ph, note }) {
  return `<div class="svc"><div><div class="svc-name">${esc(name)}</div>${sub ? `<div class="svc-sub">${esc(sub)}</div>` : ""}</div>
    <span class="spacer"></span>${pill(ph, note)}</div>`;
}

/* ------------------------------ formatting ------------------------------ */
function fmtDate(value) {
  if (!value) return "—";
  const d = new Date(value);
  if (isNaN(d.getTime())) return "—";
  return d.toLocaleString(undefined, { year: "numeric", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit" });
}

function fmtRelative(value) {
  if (!value) return "never";
  const d = new Date(value);
  if (isNaN(d.getTime())) return "—";
  const secs = Math.round((Date.now() - d.getTime()) / 1000);
  if (secs < 5) return "just now";
  if (secs < 60) return `${secs}s ago`;
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins}m ago`;
  const hrs = Math.round(mins / 60);
  if (hrs < 24) return `${hrs}h ago`;
  const days = Math.round(hrs / 24);
  return days === 1 ? "yesterday" : `${days}d ago`;
}

function truncate(str, n = 90) {
  const s = String(str ?? "");
  return s.length > n ? s.slice(0, n - 1) + "…" : s;
}

function debounce(fn, ms = 250) {
  let t = null;
  return function (...args) { clearTimeout(t); t = setTimeout(() => fn.apply(this, args), ms); };
}

function maskSecret(secret, visible = 4) {
  const s = String(secret ?? "");
  if (!s.length) return "";
  if (s.length <= visible) return "•".repeat(s.length);
  return `${s.slice(0, visible)}${"•".repeat(Math.max(6, Math.min(24, s.length - visible)))}`;
}

/* ------------------------------ exports ------------------------------ */
window.FtbUi = {
  esc,
  PHASES, phase, phaseFromResult, isEmpty,
  pill, badge, icon, btn,
  empty, errorState, loading,
  table, field, switchToggle, note, serviceRow,
  fmtDate, fmtRelative, truncate, debounce, maskSecret,
};
})();
