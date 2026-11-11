// SourceFlow Studio demo console. Plain DOM; every value is rendered with textContent (never as HTML).
"use strict";

const examples = [
  { name: "Accounts by balance", language: "sfql", params: {},
    query: 'from readmodel "account-summary"\n| project id, holder, balance, status\n| order by balance desc' },
  { name: "Totals by status", language: "sfql", params: {},
    query: 'from readmodel "account-summary"\n| summarize accounts = count(), total = sum(balance) by status\n| order by status' },
  { name: "Recent deposits (7 days)", language: "sfql", params: {},
    query: 'from events of "bank.account.deposited"\n| where time >= ago(7d)\n| project time, stream, amount = data.amount, balanceAfter = data.balanceAfter\n| order by time desc' },
  { name: "Daily volume by type", language: "sfql", params: {},
    query: 'from events of "bank.account.deposited", "bank.account.withdrawn"\n| where time >= ago(7d)\n| summarize n = count(), total = sum(data.amount) by type, day = bin(time, 1d)\n| order by day' },
  { name: "Withdrawals with holder (join)", language: "sfql", params: {},
    query: 'from readmodel "account-summary" as a\n| join events of "bank.account.withdrawn" as e on e.aggregatekey = a.id\n| project a.holder, amount = e.data.amount, e.time\n| order by e.time desc' },
  { name: "Transfers today", language: "sfql", params: {},
    query: 'from events of "bank.transfer.requested", "bank.transfer.completed", "bank.transfer.failed"\n| where time >= ago(1d)\n| project time, type, stream, data\n| order by time desc' },
  { name: "Account history (stream)", language: "sfql", params: { stream: "bank-account-<32 hex id>" },
    query: "from stream @stream\n| project version, type, time, data" },
  { name: "Account state as of version", language: "sfql", params: { id: "<account id>" },
    query: 'from aggregate "bank-account" id @id as of version 2' },
  { name: "SQL: grouping with HAVING", language: "sql", params: { min: 0 },
    query: 'SELECT status, COUNT(*) AS accounts, SUM(balance) AS total\nFROM readmodel."account-summary"\nGROUP BY status\nHAVING SUM(balance) > @min\nORDER BY total DESC' },
  { name: "SQL: accounts without deposits", language: "sql", params: {},
    query: 'SELECT a.holder, a.balance\nFROM readmodel."account-summary" a\nWHERE NOT EXISTS (SELECT 1 FROM events."bank.account.deposited" e WHERE e.aggregatekey = a.id)\nORDER BY a.holder' },
  { name: "Schema of an event type", language: "sfql", params: {},
    query: 'from schema of "bank.account.opened"' },
];

const state = { language: "sfql", continuation: null, lastRequest: null, activity: [], accounts: [] };
const $ = (id) => document.getElementById(id);

function el(tag, attrs = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(attrs)) {
    if (key === "class") node.className = value;
    else if (key === "text") node.textContent = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  for (const child of children) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function storage(action, key, value) {
  try {
    if (action === "get") return JSON.parse(localStorage.getItem(key) ?? "null");
    localStorage.setItem(key, JSON.stringify(value));
  } catch { /* storage unavailable: the console still works */ }
  return null;
}

const compact = (guid) => guid.replaceAll("-", "").toLowerCase();
const dashed = (hex) => hex.length === 32 ? `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}` : hex;

function uuidv7() {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  let ms = Date.now();
  for (let i = 5; i >= 0; i--) { bytes[i] = ms & 0xff; ms = Math.floor(ms / 256); }
  bytes[6] = (bytes[6] & 0x0f) | 0x70;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  return dashed([...bytes].map((b) => b.toString(16).padStart(2, "0")).join(""));
}

// ---------------------------------------------------------------- tabs

for (const tab of document.querySelectorAll(".tab")) {
  tab.addEventListener("click", () => showView(tab.dataset.view));
}

function showView(view) {
  for (const tab of document.querySelectorAll(".tab")) {
    const active = tab.dataset.view === view;
    tab.classList.toggle("active", active);
    tab.setAttribute("aria-selected", String(active));
  }
  for (const section of document.querySelectorAll(".view")) {
    section.classList.toggle("active", section.id === `view-${view}`);
  }
  if (view === "schema" && !state.schemaLoaded) loadSchema();
  if (view === "commands") refreshAccounts();
}

// ---------------------------------------------------------------- health

async function checkHealth() {
  const dot = $("health-dot");
  try {
    const response = await fetch("api/health", { cache: "no-store" });
    const up = response.ok;
    dot.className = `dot ${up ? "up" : "down"}`;
    $("health-text").textContent = up ? "Bank service is up" : `Bank service: HTTP ${response.status}`;
  } catch {
    dot.className = "dot down";
    $("health-text").textContent = "Bank service is not reachable";
  }
}

// ---------------------------------------------------------------- query

function setLanguage(language) {
  state.language = language;
  for (const seg of document.querySelectorAll(".seg")) {
    const active = seg.dataset.language === language;
    seg.classList.toggle("active", active);
    seg.setAttribute("aria-checked", String(active));
  }
}

for (const seg of document.querySelectorAll(".seg")) {
  seg.addEventListener("click", () => setLanguage(seg.dataset.language));
}

function loadExample(example) {
  setLanguage(example.language);
  $("query").value = example.query;
  $("params").value = JSON.stringify(example.params, null, 2);
}

function fillExamples() {
  const select = $("examples");
  select.append(el("option", { value: "", text: "Choose an example…" }));
  examples.forEach((example, i) => select.append(el("option", { value: String(i), text: example.name })));
  select.addEventListener("change", () => {
    if (select.value !== "") loadExample(examples[Number(select.value)]);
  });
}

function readParams() {
  const text = $("params").value.trim();
  if (text === "") return {};
  const value = JSON.parse(text);
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error("Parameters must be a JSON object.");
  return value;
}

async function postJson(path, body, headers = {}) {
  const response = await fetch(path, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
  let payload = null;
  const type = response.headers.get("Content-Type") ?? "";
  if (type.includes("json")) payload = await response.json();
  else if (response.status !== 204) payload = { title: await response.text() };
  return { response, payload };
}

function showDiagnostics(problem, ok = false) {
  const box = $("diagnostics");
  box.replaceChildren();
  box.classList.toggle("ok", ok);
  if (!problem) { box.hidden = true; return; }
  box.hidden = false;
  if (problem.title) box.append(el("div", { class: "code", text: problem.title }));
  for (const d of problem.diagnostics ?? []) {
    const where = d.line > 0 ? ` (line ${d.line}, column ${d.column})` : "";
    box.append(el("div", {}, el("span", { class: "code", text: d.code }), ` ${d.message}${where}`));
  }
}

async function runQuery(continuation = null) {
  let parameters;
  try { parameters = readParams(); } catch (e) { showDiagnostics({ title: e.message }); return; }
  const request = {
    query: $("query").value,
    language: state.language,
    parameters,
    pageSize: Number($("page-size").value) || 25,
    continuation,
  };
  $("run").disabled = true;
  $("result-meta").textContent = "Running…";
  const started = performance.now();
  try {
    const { response, payload } = await postJson("api/query", request);
    const elapsed = Math.round(performance.now() - started);
    if (!response.ok) {
      showDiagnostics(payload ?? { title: `HTTP ${response.status}` });
      $("result-meta").textContent = `Failed (HTTP ${response.status}).`;
      if (!continuation) $("results").replaceChildren();
      $("next-page").disabled = true;
      return;
    }
    showDiagnostics(null);
    state.lastRequest = request;
    state.continuation = payload.continuation;
    renderTable(payload, continuation !== null);
    const sources = (payload.consistency?.sources ?? [])
      .map((s) => `${s.source}: ${s.level}${s.stale ? " (stale)" : ""}`).join(" · ");
    const rows = $("results").tBodies[0]?.rows.length ?? 0;
    $("result-meta").textContent = `${rows} row${rows === 1 ? "" : "s"} · ${elapsed} ms · ${sources} · ${payload.queryHash.slice(0, 19)}…`;
    $("next-page").disabled = !payload.continuation;
  } catch (e) {
    showDiagnostics({ title: `The request failed: ${e.message}` });
    $("result-meta").textContent = "Failed.";
  } finally {
    $("run").disabled = false;
  }
}

function renderTable(page, append) {
  const table = $("results");
  if (!append) {
    table.replaceChildren();
    const head = el("tr");
    for (const column of page.columns) head.append(el("th", {}, column.name, el("small", { text: column.type })));
    table.append(el("thead", {}, head), el("tbody"));
  }
  const body = table.tBodies[0];
  for (const row of page.rows) {
    const tr = el("tr");
    row.forEach((value, i) => tr.append(cell(value, page.columns[i]?.type)));
    body.append(tr);
  }
  if (!append && page.rows.length === 0) {
    body.append(el("tr", {}, el("td", { class: "null", colspan: String(page.columns.length || 1), text: "No rows." })));
  }
}

function cell(value, type) {
  if (value === null || value === undefined) return el("td", { class: "null", text: "null" });
  if (value === "[redacted]") return el("td", { class: "redacted", text: value, title: "Personal data: needs the sourceflow.query.pii permission" });
  if (typeof value === "object") return el("td", { class: "json", text: JSON.stringify(value, null, 1) });
  const numeric = type === "integer" || type === "decimal";
  return el("td", { class: numeric ? "num" : "", text: String(value) });
}

async function validateQuery() {
  let parameters;
  try { parameters = readParams(); } catch (e) { showDiagnostics({ title: e.message }); return; }
  const { response, payload } = await postJson("api/query/validate", { query: $("query").value, language: state.language, parameters });
  if (!response.ok) { showDiagnostics(payload ?? { title: `HTTP ${response.status}` }); return; }
  if (payload.valid) {
    const columns = payload.columns.map((c) => `${c.name}: ${c.type}`).join(", ");
    showDiagnostics({ title: `Valid. Columns: ${columns}` }, true);
  } else {
    showDiagnostics({ title: "Not valid.", diagnostics: payload.diagnostics });
  }
}

$("run").addEventListener("click", () => runQuery());
$("validate").addEventListener("click", validateQuery);
$("next-page").addEventListener("click", () => { if (state.continuation) runQuery(state.continuation); });
$("query").addEventListener("keydown", (e) => {
  if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) { e.preventDefault(); runQuery(); }
});

function openQuery(query, params = {}, language = "sfql") {
  loadExample({ query, params, language });
  showView("query");
  runQuery();
}

// ---------------------------------------------------------------- schema

async function loadSchema() {
  $("schema-meta").textContent = "Loading the catalog…";
  try {
    const response = await fetch("api/query/schema", { cache: "no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    const schema = await response.json();
    state.schemaLoaded = true;
    renderSchema(schema);
    $("schema-meta").textContent =
      `${schema.events.length} event types · ${schema.readModels.length} read models · ${schema.aggregates.length} aggregates · catalog ${schema.fingerprint.slice(0, 12)}`;
  } catch (e) {
    $("schema-meta").textContent = `Could not load the catalog: ${e.message}`;
  }
}

function fieldList(fields) {
  const list = el("ul", { class: "fields" });
  for (const field of fields) {
    const classes = [field.personalData ? "pii" : "", field.queryable ? "idx" : ""].join(" ").trim();
    const title = [field.personalData ? "personal data" : "", field.queryable ? "[Queryable]" : ""].filter(Boolean).join(", ");
    list.append(el("li", { class: classes, title }, field.name, " ", el("span", { class: "t", text: field.type })));
  }
  return list;
}

function renderSchema(schema) {
  const events = $("schema-events");
  events.replaceChildren();
  for (const type of schema.events) {
    const latest = type.versions[type.versions.length - 1];
    events.append(el("div", { class: "card" },
      el("div", { class: "card-head" },
        el("span", { class: "title", text: type.name }),
        el("button", { class: "secondary", text: "Query", onclick: () =>
          openQuery(`from events of "${type.name}"\n| where time >= ago(7d)\n| order by time desc`) })),
      fieldList(latest.fields),
      el("div", { class: "version", text: `version${type.versions.length > 1 ? "s" : ""} ${type.versions.map((v) => v.version).join(", ")}` })));
  }

  const readModels = $("schema-readmodels");
  readModels.replaceChildren();
  for (const model of schema.readModels) {
    readModels.append(el("div", { class: "card" },
      el("div", { class: "card-head" },
        el("span", { class: "title", text: model.name }),
        el("button", { class: "secondary", text: "Query", onclick: () => openQuery(`from readmodel "${model.name}"`) })),
      fieldList(model.fields),
      el("div", { class: "version", text: `collection ${model.collection}` })));
  }

  const aggregates = $("schema-aggregates");
  aggregates.replaceChildren();
  for (const aggregate of schema.aggregates) {
    aggregates.append(el("div", { class: "card" },
      el("div", { class: "card-head" },
        el("span", { class: "title", text: aggregate.name }),
        el("button", { class: "secondary", text: "Query", onclick: () =>
          openQuery(`from aggregate "${aggregate.name}" id @id`, { id: "<id>" }) })),
      fieldList(aggregate.fields)));
  }
}

$("reload-schema").addEventListener("click", loadSchema);

// ---------------------------------------------------------------- commands

const commands = {
  open: (f) => ({ path: `api/accounts/${uuidv7()}`, body: { holder: f.holder, initialDeposit: Number(f.initialDeposit) }, label: `Open account for ${f.holder}` }),
  deposit: (f) => ({ path: `api/accounts/${f.accountId}/deposits`, body: { amount: Number(f.amount) }, label: `Deposit ${f.amount}` }),
  withdraw: (f) => ({ path: `api/accounts/${f.accountId}/withdrawals`, body: { amount: Number(f.amount) }, label: `Withdraw ${f.amount}` }),
  close: (f) => ({ path: `api/accounts/${f.accountId}/close`, body: { reason: f.reason }, label: "Close account" }),
  transfer: (f) => ({ path: `api/transfers/${uuidv7()}`, body: { from: f.from, to: f.to, amount: Number(f.amount) }, label: `Transfer ${f.amount}` }),
};

async function send(kind, fields) {
  const command = commands[kind](fields);
  const accountId = command.path.match(/accounts\/([0-9a-f-]+)/i)?.[1] ?? fields.from ?? null;
  const { response, payload } = await postJson(command.path, command.body, { "Idempotency-Key": uuidv7() });
  const token = response.headers.get("SourceFlow-Consistency");
  const entry = {
    at: new Date().toISOString(), label: command.label, account: accountId,
    status: response.ok ? payload?.status ?? `HTTP ${response.status}` : (payload?.title ?? `HTTP ${response.status}`),
    ok: response.ok, version: payload?.version ?? null, token, detail: response.ok ? null : payload?.detail ?? null,
  };
  if (kind === "open" && response.ok) {
    state.accounts.push({ id: accountId, holder: fields.holder });
    storage("set", "studio.accounts", state.accounts);
    fillAccountSelects();
  }
  record(entry);
  return entry;
}

function record(entry) {
  state.activity.unshift(entry);
  storage("set", "studio.activity", state.activity.slice(0, 200));
  renderActivity();
}

function showCommandResult(entry) {
  const box = $("command-result");
  box.hidden = false;
  box.replaceChildren(
    el("div", {}, el("span", { class: `status ${entry.ok ? "ok" : "bad"}`, text: entry.status }), ` · ${entry.label}`,
      entry.version !== null ? ` · stream version ${entry.version}` : ""),
    entry.detail ? el("div", { class: "muted", text: entry.detail }) : null,
    entry.token ? el("div", {}, "Consistency token ", el("span", { class: "token", text: entry.token })) : null);
  if (entry.ok && entry.account) {
    const actions = el("div", { class: "actions" },
      el("button", { class: "secondary", text: "Read my write (account summary)", onclick: () => openQuery(
        'from readmodel "account-summary"\n| where id = @id\noptions (consistency = at_least(@token), timeout = 5s)',
        { id: compact(entry.account), token: entry.token ?? "0:0" }) }),
      el("button", { class: "secondary", text: "Aggregate state", onclick: () =>
        openQuery('from aggregate "bank-account" id @id', { id: entry.account }) }),
      el("button", { class: "secondary", text: "Event stream", onclick: () =>
        openQuery("from stream @stream\n| project version, type, time, data", { stream: `bank-account-${compact(entry.account)}` }) }));
    box.append(actions);
  }
}

for (const form of document.querySelectorAll("form[data-command]")) {
  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const fields = Object.fromEntries(new FormData(form).entries());
    const button = form.querySelector("button");
    button.disabled = true;
    try {
      showCommandResult(await send(form.dataset.command, fields));
    } catch (error) {
      showCommandResult({ ok: false, status: "Request failed", label: error.message, version: null });
    } finally {
      button.disabled = false;
    }
  });
}

function fillAccountSelects() {
  for (const select of document.querySelectorAll("select.accounts")) {
    const current = select.value;
    select.replaceChildren(...state.accounts.map((a) => el("option", { value: a.id, text: `${a.holder} · ${a.id.slice(0, 8)}…${a.balance !== undefined ? ` · ${a.balance}` : ""}` })));
    if (state.accounts.some((a) => a.id === current)) select.value = current;
  }
  const to = document.querySelector('form[data-command="transfer"] select[name="to"]');
  if (to && to.options.length > 1 && to.selectedIndex === 0) to.selectedIndex = 1;
}

async function refreshAccounts() {
  try {
    const { response, payload } = await postJson("api/query", {
      query: 'from readmodel "account-summary" | where status = 1 | project id, holder, balance | order by holder', pageSize: 500,
    });
    if (!response.ok) return;
    state.accounts = payload.rows.map(([id, holder, balance]) => ({ id: dashed(id), holder, balance }));
    storage("set", "studio.accounts", state.accounts);
    fillAccountSelects();
  } catch { /* keep the cached list */ }
}

async function seed() {
  const button = $("seed");
  button.disabled = true;
  try {
    const people = [["Ada Lovelace", 1200], ["Grace Hopper", 850], ["Edsger Dijkstra", 400], ["Barbara Liskov", 2300]];
    const opened = [];
    for (const [holder, deposit] of people) {
      const entry = await send("open", { holder, initialDeposit: deposit });
      if (entry.ok) opened.push(entry.account);
    }
    for (let i = 0; i < opened.length; i++) {
      await send("deposit", { accountId: opened[i], amount: 100 + i * 75 });
      await send("withdraw", { accountId: opened[i], amount: 40 + i * 10 });
    }
    if (opened.length > 1) await send("transfer", { from: opened[0], to: opened[1], amount: 300 });
    const last = await send("transfer", { from: opened[2], to: opened[3], amount: 125 });
    showCommandResult({ ...last, label: `Seeded ${opened.length} accounts with deposits, withdrawals and transfers` });
  } finally {
    button.disabled = false;
  }
}

$("seed").addEventListener("click", seed);

// ---------------------------------------------------------------- activity

function renderActivity() {
  $("activity-count").textContent = String(state.activity.length);
  const table = $("activity");
  table.replaceChildren(el("thead", {}, el("tr", {},
    el("th", { text: "Time" }), el("th", { text: "Command" }), el("th", { text: "Account" }),
    el("th", { text: "Result" }), el("th", { text: "Version" }), el("th", { text: "Consistency token" }))));
  const body = el("tbody");
  for (const entry of state.activity) {
    body.append(el("tr", {},
      el("td", { text: new Date(entry.at).toLocaleTimeString() }),
      el("td", { text: entry.label }),
      el("td", { class: "json", text: entry.account ?? "" }),
      el("td", { class: entry.ok ? "" : "redacted", text: entry.status }),
      el("td", { class: "num", text: entry.version ?? "" }),
      el("td", { class: "json", text: entry.token ?? "" })));
  }
  if (state.activity.length === 0) body.append(el("tr", {}, el("td", { class: "null", colspan: "6", text: "No commands yet." })));
  table.append(body);
}

$("clear-activity").addEventListener("click", () => {
  state.activity = [];
  storage("set", "studio.activity", []);
  renderActivity();
});

// ---------------------------------------------------------------- start

state.accounts = storage("get", "studio.accounts") ?? [];
state.activity = storage("get", "studio.activity") ?? [];
fillExamples();
loadExample(examples[0]);
fillAccountSelects();
renderActivity();
checkHealth();
setInterval(checkHealth, 10000);
