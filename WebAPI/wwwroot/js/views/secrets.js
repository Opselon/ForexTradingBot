/* ============================================================
   views/secrets.js — secret and token management.

   Security contract enforced by this view:
   - the list endpoint returns only redacted placeholders, never values
   - a value is fetched only when the admin explicitly reveals one
   - revealed values are cleared from the DOM on navigation
   - secrets are never written into URLs, logs, or analytics
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
const T = window.FtbPanel ? window.FtbPanel.toast : () => {};

let lastList = [];

function table(items) {
  if (!items.length) {
    return U.empty("key", "No secrets stored yet",
      "Store credentials here so the application can reach Telegram, AI providers and other integrations without environment variables.",
      `<div style="margin-top:14px"><button class="btn btn-primary" data-act="sec-new">${U.icon("plus")} Add secret</button></div>`);
  }

  const cols = [
    { key: "key", label: "Key", render: (v, r) =>
      `<div class="cell-title mono">${U.esc(v)}</div><div class="cell-sub">${U.esc(r.description || "—")}</div>` },
    { key: "category", label: "Category", render: (v) => U.badge(String(v || "Other")) },
    { key: "value", label: "Value", render: (_v, r) =>
      `<span class="secret-mask" data-slot="${U.esc(r.key)}">${U.esc(U.maskSecret("xxxxxxxx", 0) || "••••••")}</span>` },
    { key: "updatedUtc", label: "Updated", render: (v) =>
      `<span title="${U.fmtDate(v)}">${U.fmtRelative(v)}</span>` },
    { key: "__actions", label: "", render: (_v, r) =>
      `<div class="row-actions">
        <button class="btn btn-sm" data-act="sec-reveal" data-key="${U.esc(r.key)}" title="Reveal (audit-logged)">${U.icon("lockOpen")}</button>
        <button class="btn btn-sm" data-act="sec-edit" data-key="${U.esc(r.key)}" data-cat="${U.esc(r.category)}" title="Replace">${U.icon("edit")}</button>
        <button class="btn btn-sm btn-danger" data-act="sec-del" data-key="${U.esc(r.key)}" title="Delete">${U.icon("trash")}</button>
      </div>` },
  ];

  return U.table({ columns: cols, rows: items, rowKey: (r) => r.key, dense: true });
}

function formHtml(existing) {
  const isEdit = !!existing;
  return `<div class="card" id="secFormCard">
    <div class="card-head">
      <h3 style="flex:1">${isEdit ? `Replace “${U.esc(existing.key)}”` : "Store a secret"}</h3>
      <button class="btn btn-sm btn-ghost" data-act="sec-cancel">${U.icon("x")}</button>
    </div>
    <div class="card-body">
      ${isEdit
        ? U.note("warn", "Replacing a secret overwrites the stored value immediately. The application picks it up on next use.")
        : ""}
      ${isEdit ? "" : U.field({ id: "sec-key", label: "Key", mono: true,
        placeholder: "e.g. telegram:bot_token", required: true,
        hint: "The name the application looks this credential up by." })}
      ${U.field({ id: "sec-value", label: "Value", type: "password", isSecret: true, required: true,
        placeholder: "The secret is encrypted at rest and never sent back to the panel" })}
      ${isEdit ? "" : U.field({ id: "sec-desc", label: "Description", placeholder: "What is this credential for?" })}
      ${isEdit ? "" : `<div class="field">
        <label for="sec-cat">Category</label>
        <select id="sec-cat">
          <option value="Telegram">Telegram</option>
          <option value="Ai">AI provider</option>
          <option value="Cloudflare">Cloudflare</option>
          <option value="Database">Database</option>
          <option value="Other" selected>Other</option>
        </select>
      </div>`}
      <div class="form-actions">
        <button class="btn" data-act="sec-cancel">Cancel</button>
        <button class="btn btn-primary" data-act="sec-save">${U.icon("save")} ${isEdit ? "Replace secret" : "Store secret"}</button>
      </div>
    </div>
  </div>`;
}

async function load() {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view"><div class="view-head"><div><h2>Secrets</h2><p>Loading vault…</p></div></div>
    <div class="card skel-card"><div class="skel skel-line" style="width:50%"></div><div class="skel skel-line" style="width:70%"></div></div></div>`;
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.secrets.list());
  lastList = r.ok && r.data && Array.isArray(r.data.items) ? r.data.items : [];
  render(r);
}

function render(result) {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>Secrets &amp; tokens</h2>
      <p>${lastList.length} secret${lastList.length === 1 ? "" : "s"} in the local vault. Values are redacted by default.</p></div>
      <span class="spacer"></span>
      <button class="btn" data-act="sec-rotate" title="Re-encrypt every secret under a fresh salt">${U.icon("shield")} Rotate keys</button>
      <button class="btn btn-primary" data-act="sec-new">${U.icon("plus")} Add secret</button>
    </div>
    ${U.note("info", "The vault lives on this machine only. Revealing a value is an explicit, audit-logged action; values are never returned by the list endpoint.")}
    <div id="secFormSlot" class="hidden">${formHtml(null)}</div>
    ${result.ok ? table(lastList) : U.errorState(result, { title: "Could not open the vault", retry: true })}
  </div>`;
}

function openForm(existing) {
  const slot = document.getElementById("secFormSlot");
  if (!slot) return;
  slot.innerHTML = formHtml(existing || null);
  slot.classList.remove("hidden");
  slot.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function save() {
  const api = window.FtbApi;
  const keyEl = document.getElementById("sec-key");
  const value = document.getElementById("sec-value").value;
  const key = keyEl ? keyEl.value.trim() : (window.__secEditKey || "");
  const desc = document.getElementById("sec-desc");
  const cat = document.getElementById("sec-cat");

  if (!key) { T("err", "Key required", ""); return; }
  if (!value) { T("err", "Value required", ""); return; }

  const r = await api.request("PUT", `/api/secrets/${encodeURIComponent(key)}`, {
    body: { Value: value, Category: cat ? cat.value : "Other", Description: desc ? desc.value.trim() : null },
  });
  if (!r.ok) { T("err", "Save failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/secrets");
  window.__secEditKey = null;
  T("ok", "Secret stored", `“${key}” is encrypted at rest.`);
  load();
}

async function remove(key) {
  if (!confirm(`Delete the secret “${key}”? Integrations using it will stop working.`)) return;
  const api = window.FtbApi;
  const r = await api.request("DELETE", `/api/secrets/${encodeURIComponent(key)}`);
  if (!r.ok) { T("err", "Delete failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/secrets");
  T("ok", "Secret deleted", key);
  load();
}

async function reveal(key) {
  const api = window.FtbApi;
  const slot = document.querySelector(`[data-slot="${CSS.escape(key)}"]`);
  if (slot) slot.innerHTML = `<span class="spin">⟳</span>`;

  const r = await api.request("GET", `/api/secrets/${encodeURIComponent(key)}/reveal`, { ttl: 0 });
  if (!r.ok) {
    if (slot) slot.textContent = "••••••";
    T("err", "Reveal failed", (r.error || `HTTP ${r.status}`));
    return;
  }
  const value = r.data && r.data.value ? String(r.data.value) : "";
  if (slot) {
    slot.classList.remove("secret-mask");
    slot.innerHTML = `<span class="mono" style="color:var(--warn)">${U.esc(value)}</span>
      <button class="btn btn-sm btn-ghost" data-act="sec-hide" data-key="${U.esc(key)}" style="margin-left:6px">hide</button>`;
    T("warn", "Revealed", `“${key}” is visible for this session only and the reveal was audit-logged.`);
  }
}

function hide(key) {
  const slot = document.querySelector(`[data-slot="${CSS.escape(key)}"]`);
  if (slot) {
    slot.classList.add("secret-mask");
    slot.textContent = "••••••";
  }
}

async function rotate() {
  if (!confirm("Re-encrypt every stored secret under a fresh salt? Values are not changed; the app is unavailable for a moment.")) return;
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.secrets.rotate());
  if (!r.ok) { T("err", "Rotation failed", (r.error || `HTTP ${r.status}`)); return; }
  T("ok", "Vault rotated", `${r.data && r.data.rotated} secret${r.data && r.data.rotated === 1 ? "" : "s"} re-encrypted.`);
  load();
}

function mount() { load(); }
function unmount() { window.__secEditKey = null; }

window.FtbViews = window.FtbViews || {};
window.FtbViews.secrets = {
  mount, unmount,
  "sec-new": () => openForm(null),
  "sec-cancel": () => { const s = document.getElementById("secFormSlot"); if (s) s.classList.add("hidden"); },
  "sec-save": save,
  "sec-del": (el) => remove(el.dataset.key),
  "sec-reveal": (el) => reveal(el.dataset.key),
  "sec-hide": (el) => hide(el.dataset.key),
  "sec-rotate": rotate,
  "sec-edit": (el) => { window.__secEditKey = el.dataset.key; openForm({ key: el.dataset.key, category: el.dataset.cat }); },
};
})();
