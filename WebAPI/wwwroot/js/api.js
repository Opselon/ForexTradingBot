/* ============================================================
   api.js — centralized API client for the control panel.

   Single source of truth for every backend call the panel makes.
   Views never call fetch() directly: they go through this module so
   that auth, error normalization, abort, retry and caching are all
   applied uniformly and no endpoint is spelled two different ways.
   ============================================================ */

(() => {
"use strict";

/* ------------------------------ types ------------------------------ */
/* JSDoc typedefs give the views real contracts without a build step.
   These describe API response shapes, and they are verified by the
   e2e tests (tests/e2e) against the live backend. */

/**
 * @typedef {Object} ApiResult
 * @property {boolean} ok
 * @property {*} data
 * @property {string|null} error
 * @property {number} status
 */

const TOKEN_KEY = "ftb.panel.token";
const CSRF_KEY = "ftb.panel.csrf";

/* ------------------------------ state ------------------------------ */
let baseUrl = "";
let authToken = read(TOKEN_KEY);
let csrfToken = read(CSRF_KEY);

/** In-flight requests, keyed by a dedupe key. */
const inflight = new Map();

/** Simple TTL cache for read-heavy endpoints (dashboard summaries). */
const cache = new Map();
const CACHE_DEFAULT_TTL = 15_000;

function read(key) {
  try { return localStorage.getItem(key) || null; }
  catch { return null; }
}
function write(key, value) {
  try {
    if (value === null) localStorage.removeItem(key);
    else localStorage.setItem(key, value);
  } catch { /* storage can be unavailable in private mode */ }
}

/* ------------------------------ errors ------------------------------ */
/**
 * A normalized error the UI can render without knowing wire formats.
 * ProblemDetails, validation dictionaries and plain strings all land here.
 */
class ApiError extends Error {
  constructor(message, status, details) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.details = details || null;
  }

  /** True when the caller is simply not authenticated anymore. */
  get isAuth() { return this.status === 401 || this.status === 403; }

  /** True when the service is alive but the resource is not configured. */
  get isMissing() { return this.status === 404; }

  /** True for transient network/timeout failures worth retrying. */
  get isTransient() {
    return this.status === 0 || this.status === 408 || this.status === 425 ||
           this.status === 429 || (this.status >= 500 && this.status <= 599);
  }

  /** A single human sentence, preferring the server's own wording. */
  get summary() {
    if (this.status === 0) return "The backend did not respond. Is the service running?";
    if (typeof this.details === "string" && this.details.length) return this.details;
    if (this.details && typeof this.details === "object") {
      // ValidationProblemDetails flattens errors into a {field:[msgs]} map.
      const vals = Object.values(this.details).flat();
      const joined = vals.filter(Boolean).join(" ");
      if (joined.length) return joined;
    }
    return this.message || "Request failed.";
  }
}

/* ------------------------------ core fetch ------------------------------ */
function buildHeaders(extra) {
  const headers = { "Content-Type": "application/json", "Accept": "application/json" };
  if (authToken) headers["Authorization"] = `Bearer ${authToken}`;
  if (csrfToken) headers["X-CSRF-TOKEN"] = csrfToken;
  return Object.assign(headers, extra || {});
}

/**
 * Runs one request and resolves to a normalized {@link ApiResult}.
 * Never rejects: every failure path becomes ok:false so callers can
 * use a single `if (!r.ok)` branch everywhere.
 */
async function raw(verb, path, { body, query, signal, timeoutMs = 20000 } = {}) {
  const url = baseUrl + path + (query ? "?" + toQuery(query) : "");
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  if (signal) signal.addEventListener("abort", () => controller.abort());

  const init = {
    method: verb,
    headers: buildHeaders(),
    credentials: "same-origin",
    signal: controller.signal,
  };
  if (body !== undefined && body !== null) init.body = JSON.stringify(body);

  try {
    const response = await fetch(url, init);
    const text = await response.text();
    let data = null;
    if (text) {
      try { data = JSON.parse(text); }
      catch { data = text; }
    }

    if (!response.ok) {
      const problem = data && typeof data === "object" ? data : null;
      const message = (problem && (problem.title || problem.message)) ||
                      (typeof data === "string" && data.length ? data : `HTTP ${response.status}`);
      const details = (problem && (problem.errors || problem.detail)) || null;
      return { ok: false, data: null, error: message, status: response.status, problem: details };
    }

    // Anti-forgery tokens are rotated by the backend after login.
    const newCsrf = response.headers.get("X-CSRF-TOKEN");
    if (newCsrf) { csrfToken = newCsrf; write(CSRF_KEY, newCsrf); }

    return { ok: true, data: data ?? null, error: null, status: response.status, problem: null };
  } catch (err) {
    if (err && err.name === "AbortError") {
      return { ok: false, data: null, error: "Request timed out", status: 0, problem: null, aborted: true };
    }
    return { ok: false, data: null, error: (err && err.message) || "Network error", status: 0, problem: null };
  } finally {
    clearTimeout(timer);
  }
}

function toQuery(obj) {
  const parts = [];
  for (const [key, value] of Object.entries(obj || {})) {
    if (value === undefined || value === null || value === "") continue;
    if (Array.isArray(value)) {
      for (const v of value) parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(v))}`);
    } else if (typeof value === "boolean") {
      parts.push(`${encodeURIComponent(key)}=${value ? "true" : "false"}`);
    } else {
      parts.push(`${encodeURIComponent(key)}=${encodeURIComponent(String(value))}`);
    }
  }
  return parts.join("&");
}

/**
 * Retries idempotent GETs a bounded number of times with backoff.
 * Non-GET calls are never retried automatically.
 */
async function withRetry(fn, attempts) {
  let last = null;
  for (let i = 0; i < attempts; i++) {
    const r = await fn();
    if (r.ok || !shouldRetry(r)) return r;
    last = r;
    // 400ms, 800ms, 1600ms… capped, plus jitter to avoid a thundering herd.
    const delay = Math.min(2000, 400 * Math.pow(2, i)) * (0.8 + Math.random() * 0.4);
    await new Promise((res) => setTimeout(res, delay));
  }
  return last;
}

function shouldRetry(result) {
  if (result.aborted) return false;
  if (result.error instanceof ApiError) return result.error.isTransient;
  // status 0 is a network failure; 5xx/429 are worth one more try.
  return result.status === 0 || result.status === 429 ||
         (result.status >= 500 && result.status <= 599);
}

/* ------------------------------ dedupe + cache ------------------------------ */
function dedupeKey(verb, url) {
  // Only collapse concurrent identical GETs; POST/PUT must always run.
  return verb === "GET" ? `GET ${url}` : null;
}

/**
 * The public call surface. Returns a promise for an {@link ApiResult}.
 * @param {string} verb
 * @param {string} path
 * @param {{body?:*, query?:*, signal?:AbortSignal, timeoutMs?:number, ttl?:number, retry?:number}} [opts]
 */
function request(verb, path, opts = {}) {
  const url = baseUrl + path + (opts.query ? "?" + toQuery(opts.query) : "");
  const key = dedupeKey(verb, url);

  // Cached read: hand back the same promise and the same value.
  if (key && opts.ttl !== undefined) {
    const hit = cache.get(key);
    const now = Date.now();
    if (hit && hit.expires > now) {
      return Promise.resolve(hit.value);
    }
  }

  // Coalesce identical concurrent reads.
  if (key && inflight.has(key)) return inflight.get(key);

  const attempts = Math.max(1, Math.min(4, opts.retry ?? (verb === "GET" ? 3 : 1)));
  const run = () => raw(verb, path, opts);

  const p = withRetry(run, attempts).then((result) => {
    if (key) inflight.delete(key);

    // Refresh the cache only on success; a stale entry beats a failure.
    if (key && opts.ttl !== undefined && result.ok) {
      cache.set(key, { value: result, expires: Date.now() + (opts.ttl ?? CACHE_DEFAULT_TTL) });
    }
    return result;
  }).catch((err) => {
    if (key) inflight.delete(key);
    return { ok: false, data: null, error: (err && err.message) || "Unexpected error", status: 0, problem: null };
  });

  if (key) inflight.set(key, p);
  return p;
}

/** Clears the read cache so the next call hits the backend. */
function invalidate(pathPrefix) {
  if (!pathPrefix) { cache.clear(); return; }
  for (const k of cache.keys()) {
    if (k.includes(pathPrefix)) cache.delete(k);
  }
  for (const k of inflight.keys()) {
    if (k.includes(pathPrefix)) inflight.delete(k);
  }
}

/* ------------------------------ auth ------------------------------ */
function setToken(token) {
  authToken = token || null;
  write(TOKEN_KEY, authToken);
  invalidate();
}
function clearToken() { setToken(null); }
function hasToken() { return !!authToken; }

/* ------------------------------ endpoint registry ------------------------------ */
/* One place where routes are named. If a route changes, it changes here.
   Every name maps to a documented backend controller action. */
const ENDPOINTS = {
  auth: {
    login: (u, p) => ["POST", "/api/auth/login", { body: { username: u, password: p }, retry: 1 }],
    logout: () => ["POST", "/api/auth/logout"],
    me: () => ["GET", "/api/auth/me"],
  },

  system: {
    info: () => ["GET", "/api/system/info"],
    health: () => ["GET", "/healthz"],
    restart: (delay) => ["POST", "/api/system/restart", { query: { delaySeconds: delay ?? 2 } }],
    update: () => ["POST", "/api/system/update"],
  },

  diagnostics: {
    connectivity: () => ["GET", "/api/diagnostics/connectivity-status"],
    hangfire: () => ["GET", "/api/diagnostics/hangfire-status"],
  },

  admin: {
    stats: () => ["GET", "/api/admin/stats"],
    dashboard: () => ["GET", "/api/admin/dashboard"],
  },

  rss: {
    list: () => ["GET", "/api/rss/sources", { ttl: 20000 }],
    create: () => ["POST", "/api/rss/sources"],
    update: (id) => ["PUT", `/api/rss/sources/${id}`],
    remove: (id) => ["DELETE", `/api/rss/sources/${id}`],
    toggle: (id, active) => ["POST", `/api/rss/sources/${id}/toggle`, { query: { active } }],
    fetch: (sourceId, force) => ["POST", "/api/admin/rss/fetch", { query: { rssSourceId: sourceId, forceFetch: !!force } }],
  },

  ai: {
    list: () => ["GET", "/api/ai/config", { ttl: 20000 }],
    create: () => ["POST", "/api/ai/config"],
    update: (id) => ["PUT", `/api/ai/config/${id}`],
    remove: (id) => ["DELETE", `/api/ai/config/${id}`],
    toggle: (id, enabled) => ["POST", `/api/ai/config/${id}/toggle`, { query: { enabled } }],
    test: (id) => ["POST", `/api/ai/config/${id}/test`],
  },

  forwarding: {
    rules: () => ["GET", "/api/forwarding/rules", { ttl: 20000 }],
    rule: (name) => ["GET", `/api/forwarding/rules/${encodeURIComponent(name)}`],
    bySource: (channelId) => ["GET", `/api/forwarding/rules/channel/${channelId}`],
    create: () => ["POST", "/api/forwarding/rules"],
    update: (name) => ["PUT", `/api/forwarding/rules/${encodeURIComponent(name)}`],
    remove: (name) => ["DELETE", `/api/forwarding/rules/${encodeURIComponent(name)}`],
    processBackground: () => ["POST", "/api/forwarding/process/background"],
  },

  secrets: {
    list: () => ["GET", "/api/secrets", { ttl: 30000 }],
    reveal: (key) => ["GET", `/api/secrets/${encodeURIComponent(key)}/reveal`],
    set: (key) => ["PUT", `/api/secrets/${encodeURIComponent(key)}`],
    remove: (key) => ["DELETE", `/api/secrets/${encodeURIComponent(key)}`],
    rotate: () => ["POST", "/api/secrets/rotate"],
  },

  settings: {
    all: () => ["GET", "/api/settings/all", { ttl: 30000 }],
    update: () => ["POST", "/api/settings/update"],
  },

  forceJoin: {
    get: () => ["GET", "/api/telegram/force-join"],
    save: () => ["POST", "/api/telegram/force-join"],
    toggle: (enabled) => ["POST", "/api/telegram/force-join/toggle", { query: { enabled } }],
  },

  telegram: {
    config: () => ["GET", "/api/telegram/configuration"],
    saveConfig: () => ["POST", "/api/telegram/configuration"],
  },

  logs: {
    list: () => ["GET", "/api/logs/list", { ttl: 30000 }],
    view: (fileName, lineCount) => ["GET", `/api/logs/view/${encodeURIComponent(fileName)}`, { query: { lineCount } }],
    zip: () => ["GET", "/api/logs/zip"],
  },

  config: {
    test: () => ["POST", "/api/config/test"],
    save: () => ["POST", "/api/config/save"],
  },

  users: {
    list: () => ["GET", "/api/users"],
    get: (id) => ["GET", `/api/users/${encodeURIComponent(id)}`],
    getByTelegram: (tgId, detail = false) => ["GET", `/api/users/telegram/${encodeURIComponent(tgId)}?detail=${detail}`],
    update: (id, data) => ["PUT", `/api/users/${encodeURIComponent(id)}`, { json: data }],
    delete: (id) => ["DELETE", `/api/users/${encodeURIComponent(id)}`],
    markUnreachable: (tgId, reason) => ["POST", `/api/users/unreachable?telegramId=${encodeURIComponent(tgId)}&reason=${encodeURIComponent(reason || "")}`],
  },

  gemini: {
    enhance: () => ["POST", "/api/gemini/enhance"],
    job: (jobId) => ["GET", `/api/gemini/job/${encodeURIComponent(jobId)}`],
    jobsStatus: () => ["POST", "/api/gemini/jobs/status"],
  },
};

/**
 * Calls a named endpoint and unwraps it for a view.
 * @param {[string,string,object]|() => [string,string,object]} spec
 * @returns {Promise<ApiResult>}
 */
function call(spec) {
  const [verb, path, opts] = typeof spec === "function" ? spec() : spec;
  return request(verb, path, opts || {});
}

/* ------------------------------ exports ------------------------------ */
window.FtbApi = {
  call,
  request,
  ENDPOINTS,
  invalidate,
  setBaseUrl: (u) => { baseUrl = u || ""; },
  setToken,
  clearToken,
  hasToken,
  ApiError,
};
})();
