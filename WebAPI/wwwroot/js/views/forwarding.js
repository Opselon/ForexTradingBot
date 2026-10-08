/* ============================================================
   views/forwarding.js — auto-forward rule management.

   Rules map a source channel to one or more target channels with
   edit and filter options. Every field below maps to a real
   ForwardingRuleDto property the API validates.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
let editingName = null;

function ruleTable(rows) {
  if (!rows.length) {
    return U.empty("forward", "No forwarding rules yet",
      "Create a rule to copy messages from one channel into others automatically.",
      `<div style="margin-top:14px"><button class="btn btn-primary" data-act="fwd-new">${U.icon("plus")} New rule</button></div>`);
  }

  const cols = [
    { key: "RuleName", label: "Rule", render: (v, r) =>
      `<div class="cell-title">${U.esc(v)}</div><div class="cell-sub">from ${r.SourceChannelId}</div>` },
    { key: "IsEnabled", label: "State", render: (v) =>
      U.badge(v ? "Active" : "Paused", v ? "ok" : "") },
    { key: "TargetChannelIds", label: "Targets", numeric: true, render: (v) =>
      Array.isArray(v) ? `${v.length} channel${v.length === 1 ? "" : "s"}` : "0" },
    { key: "FilterOptions", label: "Filter", render: (v) => {
      if (!v) return `<span class="badge">none</span>`;
      const bits = [];
      if (v.ContainsText) bits.push(U.truncate(v.ContainsText, 24));
      if (v.MaxMessageLength) bits.push(`≤ ${v.MaxMessageLength} chars`);
      if (v.AllowedSenderUserIds && v.AllowedSenderUserIds.length) bits.push(`${v.AllowedSenderUserIds.length} senders`);
      return bits.length ? `<span class="cell-sub">${U.esc(bits.join(" · "))}</span>` : `<span class="badge">none</span>`;
    } },
    { key: "EditOptions", label: "Edits", render: (v) => {
      if (!v) return `<span class="badge">none</span>`;
      const bits = [];
      if (v.PrependText) bits.push("prepend");
      if (v.AppendText) bits.push("append");
      if (v.RemoveLinks) bits.push("no links");
      if (v.StripFormatting) bits.push("plain");
      if (v.RemoveSourceForwardHeader) bits.push("no header");
      return bits.length ? `<span class="cell-sub">${U.esc(bits.join(" · "))}</span>` : `<span class="badge">none</span>`;
    } },
    { key: "__actions", label: "", render: (_v, r) =>
      `<div class="row-actions">
        <button class="btn btn-sm" data-act="fwd-toggle" data-name="${U.esc(r.RuleName)}" data-enabled="${!r.IsEnabled}" title="${r.IsEnabled ? "Pause" : "Resume"}">${U.icon(r.IsEnabled ? "pause" : "play")}</button>
        <button class="btn btn-sm" data-act="fwd-edit" data-name="${U.esc(r.RuleName)}" title="Edit">${U.icon("edit")}</button>
        <button class="btn btn-sm btn-danger" data-act="fwd-del" data-name="${U.esc(r.RuleName)}" title="Delete">${U.icon("trash")}</button>
      </div>` },
  ];

  return U.table({ columns: cols, rows, rowKey: (r) => r.RuleName, dense: true });
}

function formHtml(existing) {
  const isEdit = !!existing;
  const r = existing || {};
  const ed = r.EditOptions || {};
  const fi = r.FilterOptions || {};

  return `<div class="card" id="fwdFormCard">
    <div class="card-head">
      <h3 style="flex:1">${isEdit ? "Edit rule" : "New forwarding rule"}</h3>
      <button class="btn btn-sm btn-ghost" data-act="fwd-cancel">${U.icon("x")}</button>
    </div>
    <div class="card-body">
      <div class="grid grid-2">
        ${U.field({ id: "fw-name", label: "Rule name", value: r.RuleName ?? "",
          placeholder: "e.g. signals-to-main", required: true,
          hint: "Unique name used to identify the rule in the API." })}
        ${U.field({ id: "fw-source", label: "Source channel ID", type: "number", value: r.SourceChannelId ?? "",
          placeholder: "-1001234567890", required: true,
          hint: "The Telegram channel to copy messages from." })}
      </div>
      ${U.field({ id: "fw-targets", label: "Target channel IDs", mono: true,
        value: Array.isArray(r.TargetChannelIds) ? r.TargetChannelIds.join(", ") : "",
        placeholder: "-100111, -100222", required: true,
        hint: "Comma-separated list of channels to forward into." })}

      <label class="switch" for="fw-enabled">
        <input type="checkbox" id="fw-enabled" ${r.IsEnabled ? "checked" : ""}>
        <span class="switch-track"></span>
        <span class="switch-label">Enabled (messages are forwarded while the bot runs)</span>
      </label>

      <div class="divider"></div>
      <h3 style="margin-bottom:12px">Message edits</h3>
      <div class="grid grid-2">
        ${U.field({ id: "fw-prepend", label: "Prepend text", value: ed.PrependText ?? "", placeholder: "Optional" })}
        ${U.field({ id: "fw-append", label: "Append text", value: ed.AppendText ?? "", placeholder: "Optional" })}
      </div>
      ${U.field({ id: "fw-footer", label: "Custom footer", value: ed.CustomFooter ?? "", placeholder: "Optional text appended last" })}
      <div class="grid grid-2">
        ${U.switchToggle("fw-nofwd", ed.RemoveSourceForwardHeader, "Remove source forward header")}
        ${U.switchToggle("fw-nolinks", ed.RemoveLinks, "Remove links")}
        ${U.switchToggle("fw-plain", ed.StripFormatting, "Strip formatting")}
        ${U.switchToggle("fw-dropauthor", ed.DropAuthor, "Drop author")}
        ${U.switchToggle("fw-dropcaption", ed.DropMediaCaptions, "Drop media captions")}
        ${U.switchToggle("fw-noforwards", ed.NoForwards, "Send as a plain message (no forward tag)")}
      </div>

      <div class="divider"></div>
      <h3 style="margin-bottom:12px">Filters</h3>
      ${U.field({ id: "fw-contains", label: "Message must contain", value: fi.ContainsText ?? "",
        placeholder: "Optional text or regex" })}
      <label class="switch" for="fw-regex">
        <input type="checkbox" id="fw-regex" ${fi.ContainsTextIsRegex ? "checked" : ""}>
        <span class="switch-track"></span>
        <span class="switch-label">Treat as regular expression</span>
      </label>
      <div class="grid grid-2">
        ${U.field({ id: "fw-minlen", label: "Minimum length", type: "number", value: fi.MinMessageLength ?? "", placeholder: "any" })}
        ${U.field({ id: "fw-maxlen", label: "Maximum length", type: "number", value: fi.MaxMessageLength ?? "", placeholder: "any" })}
      </div>
      <div class="grid grid-2">
        ${U.switchToggle("fw-ignedit", fi.IgnoreEditedMessages, "Ignore edited messages")}
        ${U.switchToggle("fw-ignsvc", fi.IgnoreServiceMessages, "Ignore service messages")}
      </div>
      ${U.field({ id: "fw-allowed", label: "Allowed sender IDs", mono: true,
        value: Array.isArray(fi.AllowedSenderUserIds) ? fi.AllowedSenderUserIds.join(", ") : "",
        placeholder: "empty = everyone" })}
      ${U.field({ id: "fw-blocked", label: "Blocked sender IDs", mono: true,
        value: Array.isArray(fi.BlockedSenderUserIds) ? fi.BlockedSenderUserIds.join(", ") : "",
        placeholder: "empty = nobody" })}

      <div class="form-actions">
        <button class="btn" data-act="fwd-cancel">Cancel</button>
        <button class="btn btn-primary" data-act="fwd-save">${U.icon("save")} ${isEdit ? "Save rule" : "Create rule"}</button>
      </div>
    </div>
  </div>`;
}

async function load() {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view"><div class="view-head"><div><h2>Auto-Forward</h2><p>Loading rules…</p></div></div>
    <div class="card skel-card"><div class="skel skel-line" style="width:52%"></div><div class="skel skel-line" style="width:74%"></div><div class="skel skel-line" style="width:38%"></div></div></div>`;
  editingName = null;
  const api = window.FtbApi;
  render(await api.call(api.ENDPOINTS.forwarding.rules()));
}

function render(result) {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  const rows = result.ok && Array.isArray(result.data) ? result.data : [];
  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>Auto-Forward</h2><p>${rows.length} rule${rows.length === 1 ? "" : "s"} configured.</p></div>
      <span class="spacer"></span>
      <button class="btn btn-primary" data-act="fwd-new">${U.icon("plus")} New rule</button>
    </div>
    <div id="fwdFormSlot" class="hidden">${formHtml(null)}</div>
    ${result.ok ? ruleTable(rows) : U.errorState(result, { title: "Could not load rules", retry: true })}
  </div>`;
}

function openForm(name) {
  const slot = document.getElementById("fwdFormSlot");
  if (!slot) return;
  if (!name) { editingName = null; slot.innerHTML = formHtml(null); }
  else {
    editingName = name;
    const rows = (window.__fwdRows || []);
    const found = rows.find((x) => x.RuleName === name);
    slot.innerHTML = formHtml(found || null);
  }
  slot.classList.remove("hidden");
  slot.scrollIntoView({ behavior: "smooth", block: "start" });
}

function parseIds(raw) {
  return String(raw || "")
    .split(",")
    .map((s) => s.trim())
    .filter(Boolean)
    .map(Number)
    .filter((n) => Number.isFinite(n));
}

function collectPayload() {
  const num = (id) => {
    const v = Number(document.getElementById(id).value);
    return Number.isFinite(v) ? v : null;
  };
  return {
    RuleName: document.getElementById("fw-name").value.trim(),
    IsEnabled: document.getElementById("fw-enabled").checked,
    SourceChannelId: num("fw-source"),
    TargetChannelIds: parseIds(document.getElementById("fw-targets").value),
    EditOptions: {
      PrependText: document.getElementById("fw-prepend").value.trim() || null,
      AppendText: document.getElementById("fw-append").value.trim() || null,
      CustomFooter: document.getElementById("fw-footer").value.trim() || null,
      RemoveSourceForwardHeader: document.getElementById("fw-nofwd").checked,
      RemoveLinks: document.getElementById("fw-nolinks").checked,
      StripFormatting: document.getElementById("fw-plain").checked,
      DropAuthor: document.getElementById("fw-dropauthor").checked,
      DropMediaCaptions: document.getElementById("fw-dropcaption").checked,
      NoForwards: document.getElementById("fw-noforwards").checked,
    },
    FilterOptions: {
      AllowedMessageTypes: [],
      AllowedMimeTypes: [],
      AllowedSenderUserIds: parseIds(document.getElementById("fw-allowed").value),
      BlockedSenderUserIds: parseIds(document.getElementById("fw-blocked").value),
      ContainsText: document.getElementById("fw-contains").value.trim() || null,
      ContainsTextIsRegex: document.getElementById("fw-regex").checked,
      IgnoreEditedMessages: document.getElementById("fw-ignedit").checked,
      IgnoreServiceMessages: document.getElementById("fw-ignsvc").checked,
      MaxMessageLength: num("fw-maxlen"),
      MinMessageLength: num("fw-minlen"),
    },
  };
}

async function save() {
  const api = window.FtbApi;
  const p = collectPayload();
  if (!p.RuleName || !p.SourceChannelId || !p.TargetChannelIds.length) {
    window.FtbPanel.toast("err", "Missing fields", "Rule name, source channel and at least one target are required.");
    return;
  }
  const r = editingName
    ? await api.request("PUT", `/api/forwarding/rules/${encodeURIComponent(editingName)}`, { body: p })
    : await api.request("POST", "/api/forwarding/rules", { body: p });

  if (!r.ok) {
    window.FtbPanel.toast("err", "Save failed", (r.error || `HTTP ${r.status}`).slice(0, 180));
    return;
  }
  api.invalidate("/api/forwarding");
  window.FtbPanel.toast("ok", editingName ? "Rule updated" : "Rule created",
    "The running forwarder picks this up live; no restart needed.");
  editingName = null;
  load();
}

async function remove(name) {
  if (!confirm(`Delete the forwarding rule "${name}"?`)) return;
  const api = window.FtbApi;
  const r = await api.request("DELETE", `/api/forwarding/rules/${encodeURIComponent(name)}`);
  if (!r.ok) { window.FtbPanel.toast("err", "Delete failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/forwarding");
  window.FtbPanel.toast("ok", "Rule deleted", "");
  load();
}

async function toggle(name, enabled) {
  // Toggle is implemented as a targeted update so the rule keeps its
  // configured targets and filters; only IsEnabled changes.
  const api = window.FtbApi;
  const rows = window.__fwdRows || [];
  const found = rows.find((x) => x.RuleName === name);
  if (!found) { window.FtbPanel.toast("err", "Rule not found", ""); return; }

  const body = { ...found, IsEnabled: enabled };
  const r = await api.request("PUT", `/api/forwarding/rules/${encodeURIComponent(name)}`, { body });
  if (!r.ok) { window.FtbPanel.toast("err", "Toggle failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/forwarding");
  window.FtbPanel.toast(enabled ? "ok" : "warn", enabled ? "Rule active" : "Rule paused", "");
  load();
}

async function mount() {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.forwarding.rules());
  if (r.ok && Array.isArray(r.data)) window.__fwdRows = r.data;
  render(r);
}
function unmount() { editingName = null; }

window.FtbViews = window.FtbViews || {};
window.FtbViews.forwarding = {
  mount, unmount,
  "fwd-new": () => openForm(null),
  "fwd-cancel": () => { const s = document.getElementById("fwdFormSlot"); if (s) s.classList.add("hidden"); editingName = null; },
  "fwd-save": save,
  "fwd-del": (el) => remove(el.dataset.name),
  "fwd-toggle": (el) => toggle(el.dataset.name, el.dataset.enabled === "true"),
  "fwd-edit": (el) => openForm(el.dataset.name),
};
})();
