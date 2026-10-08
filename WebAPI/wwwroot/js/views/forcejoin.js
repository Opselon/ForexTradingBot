/* ============================================================
   views/forcejoin.js — Telegram force-join configuration.

   Backed by /api/telegram/force-join (GET/POST/toggle). The endpoint
   falls back to a disabled default when the settings store is
   unreachable, so this view always renders real stored state.
   ============================================================ */

(() => {
"use strict";

const U = window.FtbUi;
const T = window.FtbPanel ? window.FtbPanel.toast : () => {};

let current = null;
let loading = true;

async function load(silent = false) {
  const api = window.FtbApi;
  const r = await api.call(api.ENDPOINTS.forceJoin.get());
  loading = false;
  current = r.ok ? r.data : null;
  if (!silent) render(r);
  return r;
}

function formHtml() {
  const c = current || {};
  const enabled = !!c.isEnabled;

  return `<div class="card">
    <div class="card-head"><h3 style="flex:1">Force join</h3>
      ${U.pill(enabled ? U.PHASES.RUNNING : U.PHASES.UNCONFIGURED,
        enabled ? "Users must join the channel before they can use the bot" : "Inactive — open access")}</div>
    <div class="card-body">
      ${U.note(enabled ? "ok" : "info",
        enabled
          ? "Every new user must join the configured channel before the bot responds. Existing users are unaffected."
          : "Enable this to require a channel join. The channel ID, invite link and message are all required.")}

      <div class="grid grid-2">
        ${U.field({ id: "fj-channel", label: "Channel ID", type: "number",
          value: c.channelId || c.ChannelId || "", placeholder: "-1001234567890", required: enabled,
          hint: "The numeric Telegram channel ID (supergroups and channels start with -100)." })}
        ${U.field({ id: "fj-link", label: "Invite link", type: "url",
          value: c.channelLink || c.ChannelLink || "", placeholder: "https://t.me/yourchannel", required: enabled,
          hint: "The link users are sent so they can join." })}
      </div>

      <div class="field">
        <label for="fj-msg">Message shown to new users</label>
        <textarea id="fj-msg" rows="4" placeholder="To use this bot you must first join our channel: {0}">${U.esc(c.message || c.Message || "")}</textarea>
        <p class="hint"><code>{0}</code> is replaced with the invite link when the bot sends this message.</p>
      </div>

      <label class="switch" for="fj-on">
        <input type="checkbox" id="fj-on" ${enabled ? "checked" : ""}>
        <span class="switch-track"></span>
        <span class="switch-label">Require channel join</span>
      </label>

      <div class="form-actions">
        <button class="btn btn-primary" data-act="fj-save">${U.icon("save")} Save settings</button>
      </div>
    </div>
  </div>`;
}

function render(result) {
  const el = document.getElementById("viewRoot");
  if (!el) return;

  const err = result && !result.ok;
  el.innerHTML = `<div class="view">
    <div class="view-head">
      <div><h2>Force Join</h2><p>Gate bot access behind a Telegram channel membership.</p></div>
      <span class="spacer"></span>
      ${current ? U.pill(current.isEnabled ? U.PHASES.RUNNING : U.PHASES.UNCONFIGURED) : ""}
    </div>
    ${loading ? `<div class="card skel-card"><div class="skel skel-line" style="width:50%"></div><div class="skel skel-line" style="width:72%"></div></div>`
      : err ? U.errorState(result, { title: "Could not read force-join settings", retry: true })
      : formHtml()}
  </div>`;
}

async function save() {
  const api = window.FtbApi;
  const channelId = Number(document.getElementById("fj-channel").value);
  const link = document.getElementById("fj-link").value.trim();
  const msg = document.getElementById("fj-msg").value;
  const enabled = document.getElementById("fj-on").checked;

  if (enabled && (!Number.isFinite(channelId) || !link || !msg)) {
    T("err", "Incomplete configuration", "An enabled force join needs a channel ID, invite link and message.");
    return;
  }

  const r = await api.call(api.ENDPOINTS.forceJoin.save(), {
    body: {
      IsEnabled: enabled,
      ChannelId: Number.isFinite(channelId) ? channelId : 0,
      ChannelLink: link,
      Message: msg,
    },
  });

  if (!r.ok) {
    T("err", "Save failed", (r.error || `HTTP ${r.status}`).slice(0, 200));
    return;
  }
  api.invalidate("/api/telegram/force-join");
  T("ok", "Force-join saved", enabled ? "New users must now join the channel." : "Force join is off; the bot is open.");
  await load(true);
  render({ ok: true });
}

function mount() {
  loading = true;
  render(null);
  load();
}

function unmount() { current = null; }

window.FtbViews = window.FtbViews || {};
window.FtbViews.forcejoin = { mount, unmount, "fj-save": save };
})();
