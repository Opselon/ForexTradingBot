/* ============================================================
   views/rss.js — RSS source management.
   Every row is a persisted RssSource. No mock feeds.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
let editing = null; // Guid being edited, or null when creating

function healthPill(src) {
  if (!src) return U.pill(U.PHASES.UNKNOWN);
  if (!src.IsActive) return U.pill(U.PHASES.UNCONFIGURED, "Paused — not fetched");
  if (src.FetchErrorCount >= 3) return U.pill(U.PHASES.ERROR, `${src.FetchErrorCount} fetch errors`);
  if (src.FetchErrorCount > 0) return U.pill(U.PHASES.DEGRADED, `${src.FetchErrorCount} fetch errors`);
  return U.pill(U.PHASES.RUNNING, "Healthy");
}

function sourceTable(rows) {
  if (!rows.length) {
    return U.empty("rss", "No RSS sources configured yet",
      "Add a feed to start collecting news items from it. Nothing is shown here until you do.",
      `<div style="margin-top:14px"><button class="btn btn-primary" data-act="rss-new">${U.icon("plus")} Add RSS source</button></div>`);
  }

  const cols = [
    { key: "SourceName", label: "Source", render: (v, r) =>
      `<div class="cell-title">${U.esc(v)}</div><div class="cell-sub">${U.truncate(r.Url, 56)}</div>` },
    { key: "IsActive", label: "State", render: (v) => U.badge(v ? "Active" : "Paused", v ? "ok" : "") },
    { key: "FetchIntervalMinutes", label: "Interval", numeric: true, render: (v) => v ? `${v} min` : "default" },
    { key: "NewsItemCount", label: "Items", numeric: true, render: (v) => String(v ?? 0) },
    { key: "LastSuccessfulFetchAt", label: "Last success", render: (v) =>
      `<span title="${U.fmtDate(v)}">${U.fmtRelative(v)}</span>` },
    { key: "Health", label: "Health", render: (_v, r) => healthPill(r) },
    { key: "__actions", label: "", render: (_v, r) =>
      `<div class="row-actions">
        <button class="btn btn-sm" data-act="rss-fetch" data-id="${r.Id}" title="Fetch now">${U.icon("sync")}</button>
        <button class="btn btn-sm" data-act="rss-edit" data-id="${r.Id}" title="Edit">${U.icon("edit")}</button>
        <button class="btn btn-sm btn-danger" data-act="rss-del" data-id="${r.Id}" title="Delete">${U.icon("trash")}</button>
      </div>` },
  ];

  return U.table({ columns: cols, rows, rowKey: (r) => r.Id, dense: true });
}

function formHtml(existing) {
  const isEdit = !!existing;
  const src = existing || {};

  return `<div class="card" id="rssFormCard">
    <div class="card-head">
      <h3 style="flex:1">${isEdit ? "Edit source" : "Add an RSS source"}</h3>
      <button class="btn btn-sm btn-ghost" data-act="rss-cancel">${U.icon("x")}</button>
    </div>
    <div class="card-body">
      ${U.field({ id: "rs-name", label: "Source name", value: src.SourceName ?? "",
        placeholder: "e.g. Forex Live", required: true, hint: "A label only you see." })}
      ${U.field({ id: "rs-url", label: "Feed URL", type: "url", value: src.Url ?? "",
        placeholder: "https://example.com/rss.xml", required: true,
        hint: "Must be a reachable RSS or Atom feed. The URL is validated when you save." })}
      <div class="grid grid-2">
        ${U.field({ id: "rs-interval", label: "Fetch interval (minutes)", type: "number",
          value: src.FetchIntervalMinutes ?? "", placeholder: "system default",
          hint: "Leave blank to use the global default." })}
        ${U.field({ id: "rs-desc", label: "Description", value: src.Description ?? "",
          placeholder: "Optional note" })}
      </div>
      <label class="switch" for="rs-active">
        <input type="checkbox" id="rs-active" ${src.IsActive !== false ? "checked" : ""}>
        <span class="switch-track"></span>
        <span class="switch-label">Active (this feed is fetched on schedule)</span>
      </label>
      <div class="form-actions">
        <button class="btn" data-act="rss-cancel">Cancel</button>
        <button class="btn btn-primary" data-act="rss-save">${U.icon("save")} ${isEdit ? "Save changes" : "Add source"}</button>
      </div>
    </div>
  </div>`;
}

async function load() {
  const el = document.getElementById("viewRoot");
  if (!el) return;

  el.innerHTML = `<div class="view">
    <div class="view-head"><div><h2>RSS Sources</h2><p>Loading feeds…</p></div></div>
    <div class="card skel-card"><div class="skel skel-line" style="width:60%"></div><div class="skel skel-line" style="width:80%"></div><div class="skel skel-line" style="width:45%"></div></div>
  </div>`;

  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.rss.list());
  editing = null;
  render(r);
}

function render(result) {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  const rows = result.ok ? (Array.isArray(result.data) ? result.data : []) : [];

  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>RSS Sources</h2>
      <p>${rows.length} feed${rows.length === 1 ? "" : "s"} configured. Health reflects real fetch results.</p></div>
      <span class="spacer"></span>
      <button class="btn" data-act="rss-fetch-all">${U.icon("sync")} Fetch all</button>
      <button class="btn btn-primary" data-act="rss-new">${U.icon("plus")} Add source</button>
    </div>

    <div id="rssFormSlot" class="hidden">${formHtml(null)}</div>

    ${result.ok ? sourceTable(rows)
                : U.errorState(result, { title: "Could not load feeds", retry: true })}
  </div>`;
}

async function openForm(id) {
  const api = window.FtbApi;
  const slot = document.getElementById("rssFormSlot");
  if (!slot) return;

  if (id) {
    const r = await api.call(api.ENDPOINTS.rss.list());
    const rows = r.ok && Array.isArray(r.data) ? r.data : [];
    const found = rows.find((x) => String(x.Id) === String(id));
    if (!found) { U && toastMissing(); return; }
    editing = id;
    slot.innerHTML = formHtml(found);
  } else {
    editing = null;
    slot.innerHTML = formHtml(null);
  }
  slot.classList.remove("hidden");
  slot.scrollIntoView({ behavior: "smooth", block: "start" });
}

function toastMissing() {
  window.FtbPanel && window.FtbPanel.toast("err", "Source not found", "It may have been deleted.");
}

async function save() {
  const api = window.FtbApi;
  const name = document.getElementById("rs-name").value.trim();
  const url = document.getElementById("rs-url").value.trim();
  const intervalRaw = document.getElementById("rs-interval").value.trim();
  const interval = Number(intervalRaw);

  if (!name || !url) {
    window.FtbPanel.toast("err", "Missing fields", "Source name and feed URL are required.");
    return;
  }
  try { new URL(url); } catch {
    window.FtbPanel.toast("err", "Invalid URL", "Enter a full URL including https://");
    return;
  }
  if (intervalRaw && (!Number.isFinite(interval) || interval < 1)) {
    window.FtbPanel.toast("err", "Invalid interval", "Interval must be at least 1 minute.");
    return;
  }

  const payload = {
    SourceName: name,
    Url: url,
    IsActive: document.getElementById("rs-active").checked,
    Description: document.getElementById("rs-desc").value.trim() || null,
    FetchIntervalMinutes: intervalRaw ? interval : null,
  };

  const r = editing
    ? await api.call(api.ENDPOINTS.rss.update(editing)).then((res) =>
        window.FtbApi.request("PUT", `/api/rss/sources/${editing}`, { body: payload }))
    : await window.FtbApi.request("POST", "/api/rss/sources", { body: payload });

  if (!r.ok) {
    window.FtbPanel.toast("err", "Save failed", (r.error || `HTTP ${r.status}`).slice(0, 160));
    return;
  }
  api.invalidate("/api/rss");
  window.FtbPanel.toast("ok", editing ? "Source updated" : "Source added", name);
  editing = null;
  load();
}

async function remove(id) {
  if (!confirm("Delete this RSS source? Its collected news items stay in the database.")) return;
  const api = window.FtbApi;
  const r = await api.request("DELETE", `/api/rss/sources/${id}`);
  if (!r.ok) { window.FtbPanel.toast("err", "Delete failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/rss");
  window.FtbPanel.toast("ok", "Source deleted", "");
  load();
}

async function toggle(id, active) {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.rss.toggle(id, active));
  if (!r.ok) { window.FtbPanel.toast("err", "Toggle failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/rss");
  window.FtbPanel.toast("ok", active ? "Feed resumed" : "Feed paused", "");
  load();
}

async function fetchNow(id, all = false) {
  const api = window.FtbApi;
  window.FtbPanel.toast("warn", "Fetching", all ? "Reading every active feed…" : "Reading the feed…");
  const r = await api.call(api.ENDPOINTS.rss.fetch(id, true));
  if (!r.ok) { window.FtbPanel.toast("err", "Fetch failed", (r.error || `HTTP ${r.status}`)); return; }
  const count = Array.isArray(r.data) ? r.data.length : null;
  window.FtbPanel.toast("ok", "Fetch complete", count === null ? "" : `${count} item${count === 1 ? "" : "s"} collected.`);
  load();
}

function mount() { load(); }
function unmount() { editing = null; }

window.FtbViews = window.FtbViews || {};
window.FtbViews.rss = {
  mount, unmount, load,
  // Actions dispatched by the panel router.
  "rss-new": () => openForm(null),
  "rss-cancel": () => { const s = document.getElementById("rssFormSlot"); if (s) s.classList.add("hidden"); editing = null; },
  "rss-save": save,
  "rss-del": (el) => remove(el.dataset.id),
  "rss-fetch": (el) => fetchNow(el.dataset.id),
  "rss-fetch-all": () => fetchNow(null, true),
  "rss-toggle": (el) => toggle(el.dataset.id, el.dataset.active !== "true"),
};
})();
