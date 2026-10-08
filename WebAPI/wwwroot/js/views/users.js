/**
 * Users Management View
 * Real API-backed user list, details, and operations.
 */
(function () {
  "use strict";

  const { call, ENDPOINTS } = window.FtbApi;
  const { esc, badge, emptyState, errorState, skeleton, confirmDialog, toast } = window.FtbUi;

  let state = {
    users: [],
    filter: "",
    loading: false,
    error: null,
  };

  async function load(container) {
    state.loading = true;
    state.error = null;
    render(container);

    try {
      const res = await call(ENDPOINTS.users.list());
      state.users = Array.isArray(res.data) ? res.data : [];
      state.loading = false;
      render(container);
    } catch (err) {
      state.error = err.message || "Failed to load users";
      state.loading = false;
      render(container);
    }
  }

  function render(container) {
    if (state.loading) {
      container.innerHTML = `
        <div class="panel-section">
          <h2>Users Management</h2>
          <p class="section-desc">Manage registered Telegram bot users, view subscriptions and balances.</p>
          ${skeleton(5)}
        </div>`;
      return;
    }

    if (state.error) {
      container.innerHTML = `
        <div class="panel-section">
          <h2>Users Management</h2>
          ${errorState(state.error, () => load(container))}
        </div>`;
      return;
    }

    const filtered = state.users.filter((u) => {
      if (!state.filter) return true;
      const q = state.filter.toLowerCase();
      return (
        (u.username && u.username.toLowerCase().includes(q)) ||
        (u.telegramId && String(u.telegramId).includes(q)) ||
        (u.email && u.email.toLowerCase().includes(q))
      );
    });

    const rows = filtered.map((u) => {
      const levelBadge = u.level === 2 ? badge("VIP", "success") : u.level === 1 ? badge("Premium", "info") : badge("Free", "secondary");
      const created = u.createdAt ? new Date(u.createdAt).toLocaleDateString() : "—";
      const balance = typeof u.tokenBalance === "number" ? u.tokenBalance.toFixed(2) : "0.00";
      const levelName = typeof u.level === "string" ? u.level : (["Free","Bronze","Silver","Gold","Platinum"][u.level] || u.level);

      return `
        <tr>
          <td><strong>${esc(u.username || "Anonymous")}</strong></td>
          <td><code>${esc(u.telegramId)}</code></td>
          <td>${levelBadge} <small class="muted">${esc(String(levelName))}</small></td>
          <td>${esc(balance)}</td>
          <td>${esc(created)}</td>
          <td class="table-actions">
            <button class="btn btn-sm btn-outline view-detail-btn" data-tg="${esc(u.telegramId)}">Details</button>
            <button class="btn btn-sm btn-outline level-btn" data-id="${esc(u.id)}" data-level="${esc(String(levelName))}">Level</button>
            <button class="btn btn-sm btn-outline subs-btn" data-id="${esc(u.id)}" data-user="${esc(u.username || u.telegramId)}">Subs</button>
            <button class="btn btn-sm btn-outline text-warning mark-unreachable-btn" data-tg="${esc(u.telegramId)}">Unreachable</button>
            <button class="btn btn-sm btn-outline text-danger delete-user-btn" data-id="${esc(u.id)}" data-user="${esc(u.username || u.telegramId)}">Delete</button>
          </td>
        </tr>`;
    }).join("");

    container.innerHTML = `
      <div class="panel-section">
        <div class="section-header">
          <div>
            <h2>Users Management</h2>
            <p class="section-desc">Total registered users: <strong>${state.users.length}</strong></p>
          </div>
          <div class="header-actions">
            <input type="text" id="userSearch" class="form-control" placeholder="Search by name, ID or email..." value="${esc(state.filter)}" style="width:260px;" />
            <button id="refreshUsersBtn" class="btn btn-outline">Refresh</button>
            <button id="registerUserBtn" class="btn btn-primary">+ Register user</button>
          </div>
        </div>

        <div id="registerForm" style="display:none;" class="card" style="margin-bottom:1rem;">
          <h3>Register new user</h3>
          <div class="form-grid">
            <div><label>Username</label><input id="reg-username" class="form-control" placeholder="johndoe123" /></div>
            <div><label>Telegram ID</label><input id="reg-tgid" class="form-control" placeholder="123456789" /></div>
            <div><label>Email</label><input id="reg-email" class="form-control" placeholder="john@example.com" /></div>
          </div>
          <div style="margin-top:0.75rem;display:flex;gap:0.5rem;">
            <button id="regSave" class="btn btn-primary">Create</button>
            <button id="regCancel" class="btn btn-outline">Cancel</button>
          </div>
        </div>

        ${filtered.length === 0 ? emptyState("No users found matching your search.") : `
          <div class="table-container">
            <table class="table">
              <thead>
                <tr>
                  <th>Username</th>
                  <th>Telegram ID</th>
                  <th>Level</th>
                  <th>Tokens</th>
                  <th>Joined</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                ${rows}
              </tbody>
            </table>
          </div>
        `}
      </div>

      <div id="userDetailModal" class="modal" style="display:none;">
        <div class="modal-content" style="max-width: 600px;">
          <div class="modal-header">
            <h3>User Details</h3>
            <button type="button" class="close-btn" id="closeDetailModal">&times;</button>
          </div>
          <div class="modal-body" id="userDetailBody">
            Loading...
          </div>
        </div>
      </div>
    `;

    bindEvents(container);
  }

  function bindEvents(container) {
    const searchInput = container.querySelector("#userSearch");
    if (searchInput) {
      searchInput.addEventListener("input", (e) => {
        state.filter = e.target.value;
        render(container);
      });
    }

    const refreshBtn = container.querySelector("#refreshUsersBtn");
    if (refreshBtn) {
      refreshBtn.addEventListener("click", () => load(container));
    }

    const regBtn = container.querySelector("#registerUserBtn");
    const regForm = container.querySelector("#registerForm");
    if (regBtn && regForm) {
      regBtn.addEventListener("click", () => {
        regForm.style.display = regForm.style.display === "none" ? "block" : "none";
      });
    }
    const regCancel = container.querySelector("#regCancel");
    if (regCancel && regForm) {
      regCancel.addEventListener("click", () => { regForm.style.display = "none"; });
    }
    const regSave = container.querySelector("#regSave");
    if (regSave) {
      regSave.addEventListener("click", async () => {
        const username = container.querySelector("#reg-username").value.trim();
        const telegramId = container.querySelector("#reg-tgid").value.trim();
        const email = container.querySelector("#reg-email").value.trim();
        if (!username || !telegramId || !email) {
          toast("All three fields are required", "error");
          return;
        }
        try {
          await call(ENDPOINTS.users.register({ username, telegramId, email }));
          toast(`User "${username}" registered`, "success");
          load(container);
        } catch (err) {
          toast(err.message || "Registration failed", "error");
        }
      });
    }

    container.querySelectorAll(".level-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const id = btn.getAttribute("data-id");
        const current = btn.getAttribute("data-level") || "Free";
        const next = prompt(
          "Access level — Free, Bronze, Silver, Gold, Platinum, Admin:",
          current
        );
        if (!next) return;
        try {
          await call(ENDPOINTS.users.setLevel(id, next.trim()));
          toast(`Level changed to ${next.trim()}`, "success");
          load(container);
        } catch (err) {
          toast(err.message || "Level change failed", "error");
        }
      });
    });

    container.querySelectorAll(".subs-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const id = btn.getAttribute("data-id");
        const name = btn.getAttribute("data-user");
        if (!modal || !modalBody) return;
        modal.style.display = "flex";
        modalBody.innerHTML = skeleton(3);
        try {
          const res = await call(ENDPOINTS.users.subscriptions(id));
          const subs = Array.isArray(res.data) ? res.data : [];
          modalBody.innerHTML = `
            <div><strong>Subscriptions for ${esc(name)}</strong></div>
            ${subs.length === 0 ? "<p class='muted'>No subscriptions.</p>" :
              `<ul class="detail-list">${subs.map((s) =>
                `<li>${esc(s.planName || "Plan")} — ${new Date(s.startDate).toLocaleDateString()} → ${new Date(s.endDate).toLocaleDateString()} (${s.isActive ? "Active" : "Expired"})
                 <button class="btn btn-sm btn-outline text-danger del-sub-btn" data-sub="${esc(s.id)}">Delete</button></li>`).join("")}</ul>`}
            <h4 style="margin-top:1rem;">New subscription (30 days)</h4>
            <div style="display:flex;gap:0.5rem;margin-top:0.5rem;">
              <button id="newSub30" class="btn btn-primary btn-sm">Create 30-day subscription</button>
            </div>`;
          const newBtn = modalBody.querySelector("#newSub30");
          if (newBtn) newBtn.addEventListener("click", async () => {
            try {
              const now = new Date();
              const end = new Date(now.getTime() + 30 * 864e5);
              await call(ENDPOINTS.users.createSubscription(id, { startDate: now.toISOString(), endDate: end.toISOString() }));
              toast("Subscription created", "success");
              modal.style.display = "none";
              load(container);
            } catch (err) { toast(err.message || "Create failed", "error"); }
          });
          modalBody.querySelectorAll(".del-sub-btn").forEach((d) => {
            d.addEventListener("click", async () => {
              const subId = d.getAttribute("data-sub");
              if (!await confirmDialog("Delete this subscription?")) return;
              try {
                await call(ENDPOINTS.users.deleteSubscription(subId));
                toast("Subscription deleted", "success");
                modal.style.display = "none";
                load(container);
              } catch (err) { toast(err.message || "Delete failed", "error"); }
            });
          });
        } catch (err) {
          modalBody.innerHTML = errorState("Failed to load subscriptions: " + err.message);
        }
      });
    });

    const modal = container.querySelector("#userDetailModal");
    const modalBody = container.querySelector("#userDetailBody");
    const closeBtn = container.querySelector("#closeDetailModal");
    if (closeBtn && modal) {
      closeBtn.addEventListener("click", () => {
        modal.style.display = "none";
      });
    }

    container.querySelectorAll(".view-detail-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const tgId = btn.getAttribute("data-tg");
        if (!modal || !modalBody) return;
        modal.style.display = "flex";
        modalBody.innerHTML = skeleton(3);

        try {
          const res = await call(ENDPOINTS.users.getByTelegram(tgId, true));
          const d = res.data;
          modalBody.innerHTML = `
            <div class="user-detail-grid">
              <div><strong>Telegram ID:</strong> <code>${esc(tgId)}</code></div>
              <div><strong>Name:</strong> ${esc(d.username || d.telegramUsername || "N/A")}</div>
              <div><strong>Joined:</strong> ${d.registeredAt || d.createdAt ? new Date(d.registeredAt || d.createdAt).toLocaleString() : "N/A"}</div>
              <div><strong>Wallet Balance:</strong> ${d.wallet?.balance ?? d.tokenBalance ?? 0} tokens</div>
              <div><strong>Active Subscription:</strong> ${d.activeSubscription ? `<span class="badge badge-success">${esc(d.activeSubscription.planName || "Active")}</span>` : '<span class="badge badge-secondary">None</span>'}</div>
            </div>
            ${d.subscriptions && d.subscriptions.length > 0 ? `
              <h4 style="margin-top:1rem;">Subscription History</h4>
              <ul class="detail-list">
                ${d.subscriptions.map((s) => `<li>${esc(s.planName || "Plan")} — Exp: ${new Date(s.endDate).toLocaleDateString()} (${s.isActive ? "Active" : "Expired"})</li>`).join("")}
              </ul>
            ` : ""}
          `;
        } catch (err) {
          modalBody.innerHTML = errorState("Failed to load user details: " + err.message);
        }
      });
    });

    container.querySelectorAll(".mark-unreachable-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const tgId = btn.getAttribute("data-tg");
        const confirmed = await confirmDialog(`Mark user ${tgId} as unreachable? This flags that the bot cannot message them.`);
        if (!confirmed) return;

        try {
          await call(ENDPOINTS.users.markUnreachable(tgId, "Admin flag"));
          toast(`User ${tgId} marked unreachable`, "success");
          load(container);
        } catch (err) {
          toast(err.message || "Failed to mark user", "error");
        }
      });
    });

    container.querySelectorAll(".delete-user-btn").forEach((btn) => {
      btn.addEventListener("click", async () => {
        const id = btn.getAttribute("data-id");
        const name = btn.getAttribute("data-user");
        const confirmed = await confirmDialog(`Permanently delete user "${name}"? This action cannot be undone.`);
        if (!confirmed) return;

        try {
          await call(ENDPOINTS.users.delete(id));
          toast(`User "${name}" deleted`, "success");
          load(container);
        } catch (err) {
          toast(err.message || "Failed to delete user", "error");
        }
      });
    });
  }

  window.FtbViews = window.FtbViews || {};
  window.FtbViews.users = {
    render: (root) => load(root),
  };
})();
