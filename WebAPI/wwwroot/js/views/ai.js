/* ============================================================
   views/ai.js — AI provider and analysis-prompt management.

   The API returns ApiKeyMasked + HasKey and never the plaintext key,
   so this view can display configuration without ever holding a
   secret. The key field is required only when creating or rotating.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
let editingId = null;

function statePill(cfg) {
  if (!cfg) return U.pill(U.PHASES.UNKNOWN);
  if (!cfg.IsEnabled) return U.pill(U.PHASES.UNCONFIGURED, "Disabled");
  if (!cfg.HasKey) return U.pill(U.PHASES.ERROR, "No API key stored — this provider cannot run");
  return U.pill(U.PHASES.RUNNING, "Enabled");
}

function providerTable(rows) {
  if (!rows.length) {
    return U.empty("key", "No AI providers configured",
      "Add a provider to enable AI-assisted analysis and prompt templates.",
      `<div style="margin-top:14px"><button class="btn btn-primary" data-act="ai-new">${U.icon("plus")} Add provider</button></div>`);
  }

  const cols = [
    { key: "ProviderName", label: "Provider", render: (v, r) =>
      `<div class="cell-title">${U.esc(v)}</div><div class="cell-sub">${U.esc(r.ModelName || "no model")}</div>` },
    { key: "HasKey", label: "API key", render: (v, r) =>
      v
        ? `<span class="secret-mask" title="The key is stored server-side and never sent to the panel">${U.esc(r.ApiKeyMasked || "••••")}</span>`
        : `<span class="badge err">missing</span>` },
    { key: "IsEnabled", label: "State", render: (v, r) => statePill(r) },
    { key: "PromptTemplate", label: "Prompt", render: (v) =>
      v && String(v).trim().length
        ? `<span class="badge ok">set</span> <span class="cell-sub">${U.truncate(v, 42)}</span>`
        : `<span class="badge">none</span>` },
    { key: "LastUpdatedAt", label: "Updated", render: (v) =>
      `<span title="${U.fmtDate(v)}">${U.fmtRelative(v)}</span>` },
    { key: "__actions", label: "", render: (_v, r) =>
      `<div class="row-actions">
        <button class="btn btn-sm" data-act="ai-toggle" data-id="${r.Id}" data-enabled="${!r.IsEnabled}" title="${r.IsEnabled ? "Disable" : "Enable"}">${U.icon(r.IsEnabled ? "pause" : "play")}</button>
        <button class="btn btn-sm" data-act="ai-edit" data-id="${r.Id}" title="Edit">${U.icon("edit")}</button>
        <button class="btn btn-sm btn-danger" data-act="ai-del" data-id="${r.Id}" title="Delete">${U.icon("trash")}</button>
      </div>` },
  ];

  return U.table({ columns: cols, rows, rowKey: (r) => r.Id, dense: true });
}

function formHtml(existing) {
  const isEdit = !!existing;
  const c = existing || {};

  return `<div class="card" id="aiFormCard">
    <div class="card-head">
      <h3 style="flex:1">${isEdit ? "Edit provider" : "Add an AI provider"}</h3>
      <button class="btn btn-sm btn-ghost" data-act="ai-cancel">${U.icon("x")}</button>
    </div>
    <div class="card-body">
      ${isEdit ? U.note("info",
        "The stored API key is never sent back to the panel. Leave the key field empty to keep the current key, or type a new one to rotate it.") : ""}
      <div class="grid grid-2">
        ${U.field({ id: "ai-provider", label: "Provider", value: c.ProviderName ?? "",
          placeholder: "e.g. OpenAI, Gemini, DeepSeek", required: true })}
        ${U.field({ id: "ai-model", label: "Model", value: c.ModelName ?? "",
          placeholder: "e.g. gpt-4o-mini", required: true })}
      </div>
      ${U.field({ id: "ai-key", label: isEdit ? "API key (leave blank to keep current)" : "API key",
        type: "password", isSecret: true, placeholder: isEdit ? "unchanged" : "sk-…",
        required: !isEdit,
        hint: "Stored server-side only. The panel receives a masked representation and a HasKey flag." })}
      ${U.field({ id: "ai-desc", label: "Description", value: c.Description ?? "",
        placeholder: "Optional note" })}
      <div class="field">
        <label for="ai-prompt">Analysis prompt template</label>
        <textarea id="ai-prompt" rows="7" placeholder="System prompt used when analysing a signal or news item.">${U.esc(c.PromptTemplate ?? "")}</textarea>
        <p class="hint">This is the live prompt the analysis pipeline reads. Variables like the signal text are substituted server-side.</p>
      </div>
      <label class="switch" for="ai-enabled">
        <input type="checkbox" id="ai-enabled" ${c.IsEnabled ? "checked" : ""}>
        <span class="switch-track"></span>
        <span class="switch-label">Enabled</span>
      </label>
      <div class="form-actions">
        <button class="btn" data-act="ai-cancel">Cancel</button>
        <button class="btn btn-primary" data-act="ai-save">${U.icon("save")} ${isEdit ? "Save changes" : "Add provider"}</button>
      </div>
    </div>
  </div>`;
}

async function load() {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  el.innerHTML = `<div class="view">
    <div class="view-head"><div><h2>AI Providers</h2><p>Loading providers…</p></div></div>
    <div class="card skel-card"><div class="skel skel-line" style="width:55%"></div><div class="skel skel-line" style="width:78%"></div><div class="skel skel-line" style="width:40%"></div></div>
  </div>`;
  editingId = null;
  const api = window.FtbApi;
  render(await api.call(api.ENDPOINTS.ai.list()));
}

function render(result) {
  const el = document.getElementById("viewRoot");
  if (!el) return;
  const rows = result.ok && Array.isArray(result.data) ? result.data : [];

  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>AI Providers</h2>
      <p>${rows.length} provider${rows.length === 1 ? "" : "s"} configured. Keys are stored server-side and shown masked.</p></div>
      <span class="spacer"></span>
      <button class="btn btn-primary" data-act="ai-new">${U.icon("plus")} Add provider</button>
    </div>
    <div id="aiFormSlot" class="hidden">${formHtml(null)}</div>
    ${result.ok ? providerTable(rows)
                : U.errorState(result, { title: "Could not load providers", retry: true })}
  </div>`;
}

function openForm(id) {
  const slot = document.getElementById("aiFormSlot");
  if (!slot) return;
  if (!id) { editingId = null; slot.innerHTML = formHtml(null); }
  else {
    editingId = id;
    // The list endpoint returns masked keys, which is exactly what the edit form needs.
    const rows = (window.__aiRows || []);
    const found = rows.find((x) => String(x.Id) === String(id));
    slot.innerHTML = formHtml(found || null);
    if (found) {
      const k = document.getElementById("ai-key");
      if (k) { k.required = false; k.value = ""; }
    }
  }
  slot.classList.remove("hidden");
  slot.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function save() {
  const api = window.FtbApi;
  const provider = document.getElementById("ai-provider").value.trim();
  const model = document.getElementById("ai-model").value.trim();
  const key = document.getElementById("ai-key").value;
  const prompt = document.getElementById("ai-prompt").value;

  if (!provider || !model) {
    window.FtbPanel.toast("err", "Missing fields", "Provider and model are required.");
    return;
  }
  if (!editingId && !key) {
    window.FtbPanel.toast("err", "API key required", "Store a key when adding a provider.");
    return;
  }

  const payload = {
    ProviderName: provider,
    ModelName: model,
    IsEnabled: document.getElementById("ai-enabled").checked,
    PromptTemplate: prompt,
    Description: document.getElementById("ai-desc").value.trim() || null,
  };
  if (key) payload.ApiKey = key; // omitted → server keeps the existing key

  const r = editingId
    ? await api.request("PUT", `/api/ai/config/${editingId}`, { body: payload })
    : await api.request("POST", "/api/ai/config", { body: payload });

  if (!r.ok) {
    window.FtbPanel.toast("err", "Save failed", (r.error || `HTTP ${r.status}`).slice(0, 160));
    return;
  }
  api.invalidate("/api/ai");
  window.FtbPanel.toast("ok", editingId ? "Provider updated" : "Provider added",
    editingId ? "Changes apply to the next analysis run." : "Store the key server-side only.");
  editingId = null;
  load();
}

async function remove(id) {
  if (!confirm("Delete this AI provider configuration? Its prompt template is lost.")) return;
  const api = window.FtbApi;
  const r = await api.request("DELETE", `/api/ai/config/${id}`);
  if (!r.ok) { window.FtbPanel.toast("err", "Delete failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/ai");
  window.FtbPanel.toast("ok", "Provider deleted", "");
  load();
}

async function toggle(id, enabled) {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.ai.toggle(id, enabled));
  if (!r.ok) { window.FtbPanel.toast("err", "Toggle failed", (r.error || `HTTP ${r.status}`)); return; }
  api.invalidate("/api/ai");
  window.FtbPanel.toast(enabled ? "ok" : "warn", enabled ? "Provider enabled" : "Provider disabled",
    enabled ? "It will be used for the next analysis." : "Analysis falls back to other providers.");
  load();
}

function mount() { load().then(() => { /* rows cached for the edit form */ }); }

async function loadAndCache() {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.ai.list());
  if (r.ok && Array.isArray(r.data)) window.__aiRows = r.data;
  return r;
}

function unmount() { editingId = null; }

window.FtbViews = window.FtbViews || {};
window.FtbViews.ai = {
  mount: async () => {
    const el = document.getElementById("viewRoot");
    if (el) el.innerHTML = `<div class="view"><div class="view-head"><div><h2>AI Providers</h2><p>Loading…</p></div></div><div class="card skel-card"><div class="skel skel-line" style="width:55%"></div><div class="skel skel-line" style="width:78%"></div></div></div>`;
    const r = await loadAndCache();
    render(r);
  },
  unmount,
  "ai-new": () => openForm(null),
  "ai-cancel": () => { const s = document.getElementById("aiFormSlot"); if (s) s.classList.add("hidden"); editingId = null; },
  "ai-save": save,
  "ai-del": (el) => remove(el.dataset.id),
  "ai-toggle": (el) => toggle(el.dataset.id, el.dataset.enabled === "true"),
  "ai-edit": (el) => openForm(el.dataset.id),
};
})();
