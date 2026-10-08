/* ============================================================
   views/settings.js — advanced runtime settings.

   Settings come from the dynamic configuration service, which already
   masks sensitive values and reports whether a value is overridden by
   the environment. This view edits what that service exposes and
   nothing more — it does not invent knobs the backend does not have.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
const T = window.FtbPanel ? window.FtbPanel.toast : () => {};

let all = [];        // every setting as returned by the API
let dirty = new Set();
let group = "All";

/* Settings are grouped by their key prefix so the page is scannable
   instead of one long undifferentiated form. */
const GROUPS = ["All", "Telegram", "AI", "RSS", "Database", "Redis", "Core", "Security", "Other"];

function groupOf(key) {
  const k = String(key || "");
  const head = k.split(/[:._]/)[0] || "";
  const lower = head.toLowerCase();
  if (["telegram", "tg", "bot"].includes(lower)) return "Telegram";
  if (["ai", "gemini", "openai", "llm", "anthropic"].includes(lower)) return "AI";
  if (["rss", "news", "feed"].includes(lower)) return "RSS";
  if (["database", "db", "connectionstrings", "hangfire"].includes(lower)) return "Database";
  if (["redis", "cache"].includes(lower)) return "Redis";
  if (["core", "app", "logging", "serilog", "worker"].includes(lower)) return "Core";
  if (["security", "jwt", "auth", "vault"].includes(lower)) return "Security";
  return GROUPS.includes(head) ? head : "Other";
}

function settingRow(s) {
  const g = groupOf(s.key);
  if (group !== "All" && g !== group) return "";

  const overridden = !!s.isOverriddenByEnvironment;
  const sensitive = !!s.isSensitive;
  const value = dirty.has(s.key) ? localValue(s.key) : (sensitive ? s.displayValue : s.value);

  return `<div class="field" data-field-key="${U.esc(s.key)}">
    <label for="st-${U.esc(s.key)}">
      ${U.esc(s.key)}
      ${sensitive ? U.badge("secret", "") : ""}
      ${overridden ? U.badge("env", "warn") : ""}
      ${!s.isPersistedInDb ? U.badge("read-only", "") : ""}
    </label>
    <input id="st-${U.esc(s.key)}" data-setting-key="${U.esc(s.key)}"
      class="${sensitive ? "secret" : ""}"
      value="${U.esc(value ?? "")}" placeholder="${U.esc(s.displayValue || "")}"
      ${!s.isPersistedInDb ? "disabled" : ""} autocomplete="off">
    ${s.description ? `<p class="hint">${U.esc(s.description)}</p>` : ""}
    ${overridden ? `<p class="hint" style="color:var(--warn)">Overridden by the environment. Saving here changes the stored value but the environment wins until the variable is removed.</p>` : ""}
  </div>`;
}

function localValue(key) {
  const el = document.querySelector(`[data-setting-key="${CSS.escape(key)}"]`);
  return el ? el.value : null;
}

function groupTabs() {
  const counts = {};
  all.forEach((s) => { const g = groupOf(s.key); counts[g] = (counts[g] || 0) + 1; });
  return `<div class="seg" id="settingsTabs">
    ${GROUPS.filter((g) => group === "All" || counts[g]).map((g) =>
      `<button type="button" data-group="${g}" class="${group === g ? "on" : ""}">${g}${g === "All" ? ` (${all.length})` : ` (${counts[g] || 0})`}</button>`).join("")}
  </div>`;
}

function render(result) {
  const el = document.getElementById("viewRoot");
  if (!el) return;

  if (!result.ok) {
    el.innerHTML = `<div class="view">
      <div class="view-head"><div><h2>Settings</h2></div></div>
      ${U.errorState(result, { title: "Could not load settings", retry: true })}
    </div>`;
    return;
  }

  all = Array.isArray(result.data) ? result.data : [];
  dirty = new Set();

  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>Settings</h2><p>${all.length} configurable setting${all.length === 1 ? "" : "s"}. Sensitive values are masked.</p></div>
      <span class="spacer"></span>
      <button class="btn" data-act="st-revert" id="stRevert" disabled>${U.icon("refresh")} Revert</button>
      <button class="btn btn-primary" data-act="st-save" id="stSave" disabled>${U.icon("save")} Save changes</button>
    </div>
    ${groupTabs()}
    <div class="card">
      <div class="card-body stack" id="settingsBody">
        ${all.map(settingRow).join("") || U.empty("settings", "No settings exposed", "The dynamic configuration service has nothing configurable right now.")}
      </div>
    </div>
  </div>`;

  const body = document.getElementById("settingsBody");
  if (body) body.addEventListener("input", (e) => {
    const key = e.target.dataset && e.target.dataset.settingKey;
    if (!key) return;
    dirty.add(key);
    const save = document.getElementById("stSave");
    const rev = document.getElementById("stRevert");
    if (save) save.disabled = false;
    if (rev) rev.disabled = false;
    const original = all.find((s) => s.key === key);
    if (original) {
      const baseline = original.isSensitive ? original.displayValue : original.value;
      if (String(e.target.value) === String(baseline ?? "")) {
        dirty.delete(key);
        if (!dirty.size) { if (save) save.disabled = true; if (rev) rev.disabled = true; }
      }
    }
  });

  const tabs = document.getElementById("settingsTabs");
  if (tabs) tabs.addEventListener("click", (e) => {
    const b = e.target.closest("button[data-group]");
    if (!b) return;
    group = b.dataset.group;
    render(result);
  });
}

async function load() {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view"><div class="view-head"><div><h2>Settings</h2><p>Loading…</p></div></div>
    <div class="card skel-card"><div class="skel skel-line" style="width:40%"></div><div class="skel skel-line" style="width:65%"></div><div class="skel skel-line" style="width:52%"></div></div></div>`;
  const api = window.FtbApi;
  render(await api.call(api.ENDPOINTS.settings.all()));
}

async function save() {
  if (!dirty.size) return;
  const api = window.FtbApi;
  const payload = {};
  dirty.forEach((key) => { payload[key] = localValue(key); });

  const r = await api.call(api.ENDPOINTS.settings.update(), { body: { SettingsToUpdate: payload } });
  if (!r.ok) {
    T("err", "Save failed", (r.error || `HTTP ${r.status}`).slice(0, 200));
    return;
  }
  api.invalidate("/api/settings");
  T("ok", "Settings saved", `${Object.keys(payload).length} value${Object.keys(payload).length === 1 ? "" : "s"} persisted. Restart the core if the change affects the data provider or job storage.`);
  load();
}

function revert() {
  dirty = new Set();
  load();
}

function mount() { group = "All"; load(); }
function unmount() { dirty = new Set(); all = []; }

window.FtbViews = window.FtbViews || {};
window.FtbViews.settings = {
  mount, unmount,
  "st-save": save,
  "st-revert": revert,
};
})();
