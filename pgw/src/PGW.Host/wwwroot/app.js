const $ = (sel) => document.querySelector(sel);
let filter = "";

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

async function getJson(url) {
  const r = await fetch(url);
  if (!r.ok) throw new Error(`${url}: ${r.status}`);
  return r.json();
}

function qualityClass(q) {
  return q === "Good" ? "good" : q === "Uncertain" ? "uncertain" : "bad";
}

function fmtValue(v) {
  if (v === null || v === undefined) return "—";
  if (typeof v === "number") return Number.isInteger(v) ? v : v.toFixed(3);
  return String(v);
}

function fmtTime(iso) {
  if (!iso) return "";
  const d = new Date(iso);
  return d.toLocaleTimeString(undefined, { hour12: false }) + "." + String(d.getMilliseconds()).padStart(3, "0");
}

function renderStatus(status) {
  $("#gw-name").textContent = status.name ?? "PGW";
  $("#gw-meta").textContent =
    `uptime ${status.uptime_s}s · config v${status.version} · ${status.tags} tags · ${status.sources} sources · ${status.outputs} outputs`;
}

// Splits `_System.<name>.<field>` tags into per-source/per-output health groups.
function renderSystemGroups(tags) {
  const sources = new Map();
  const outputs = new Map();
  for (const t of tags) {
    const parts = t.id.split(".");
    if (parts[0] !== "_System" || parts.length !== 3) continue;
    const [, name, field] = parts;
    const bucket = field === "connected" || field === "error_count" || field === "last_error_code" ? sources : outputs;
    if (!bucket.has(name)) bucket.set(name, {});
    bucket.get(name)[field] = t.value;
  }

  const renderList = (map, el, kind) => {
    el.innerHTML = "";
    if (map.size === 0) { el.innerHTML = `<div class="empty">no ${kind}</div>`; return; }
    for (const [name, fields] of map) {
      const row = document.createElement("div");
      row.className = "source-row";
      const ok = kind === "sources" ? fields.connected === true : true;
      const detail = kind === "sources"
        ? (fields.connected ? "connected" : `errors: ${fields.error_count ?? 0}`)
        : `${fields.client_count ?? 0} client(s) · ${fields.requests_total ?? 0} req`;
      row.innerHTML = `<span class="dot ${ok ? "good" : "bad"}"></span><span class="name">${esc(name)}</span><span class="err">${esc(detail)}</span>`;
      el.appendChild(row);
    }
  };
  renderList(sources, $("#sources-list"), "sources");
  renderList(outputs, $("#outputs-list"), "outputs");
}

function renderTags(tags) {
  const tbody = $("#tags-body");
  const rows = tags
    .filter((t) => !t.id.startsWith("_System") && t.id.toLowerCase().includes(filter))
    .sort((a, b) => a.id.localeCompare(b.id));

  tbody.innerHTML = "";
  if (rows.length === 0) { tbody.innerHTML = `<tr><td colspan="6" class="empty">no matching tags</td></tr>`; return; }

  for (const t of rows) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td class="id">${esc(t.id)}</td>
      <td class="value">${esc(fmtValue(t.value))}${t.units ? " " + esc(t.units) : ""}</td>
      <td><span class="quality ${qualityClass(t.quality)}">● ${t.quality}</span></td>
      <td>${t.access}</td>
      <td>${fmtTime(t.timestamp)}</td>`;
    tbody.appendChild(tr);
  }
}

function renderLog(entries) {
  const el = $("#log-list");
  el.innerHTML = "";
  if (entries.length === 0) { el.innerHTML = `<div class="empty">no events yet</div>`; return; }
  for (const e of [...entries].reverse()) {
    const row = document.createElement("div");
    row.className = `log-row ${e.level}`;
    row.innerHTML = `<span class="ts">${fmtTime(e.timestampUtc)}</span><span>[${esc(e.source)}] ${esc(e.message)}</span>`;
    el.appendChild(row);
  }
}

async function tick() {
  try {
    const [status, tags, log] = await Promise.all([
      getJson("/runtime/status"),
      getJson("/runtime/tags"),
      getJson("/runtime/event-log?last=50"),
    ]);
    renderStatus(status);
    renderSystemGroups(tags);
    renderTags(tags);
    renderLog(log);
    $("#conn-dot").className = "dot good";
  } catch {
    $("#conn-dot").className = "dot bad";
  }
}

$("#search").addEventListener("input", (e) => {
  filter = e.target.value.toLowerCase();
  tick();
});

tick();
setInterval(tick, 1500);

// ============================================================================================
// Tabs
// ============================================================================================

document.querySelectorAll(".tab-btn").forEach((btn) => {
  btn.addEventListener("click", () => {
    document.querySelectorAll(".tab-btn").forEach((b) => b.classList.remove("active"));
    btn.classList.add("active");
    const view = btn.dataset.view;
    $("#view-monitor").hidden = view !== "monitor";
    $("#view-config").hidden = view !== "config";
    $("#view-scanner").hidden = view !== "scanner";
    if (view === "config") loadConfig();
    if (view !== "scanner") stopScan();
  });
});

// ============================================================================================
// Config editor — add/edit/delete sources, tags, outputs, register-map entries.
// Every mutation POSTs/PUTs/DELETEs straight to disk (§14.2 write side); it does NOT restart
// drivers on its own, so ten quick edits don't mean ten reconnects — the banner below batches
// them behind one explicit Reload.
// ============================================================================================

let cfgSources = [];
let cfgOutputs = [];

const TYPE_OPTIONS = ["bool", "int16", "uint16", "int32", "uint32", "int64", "float32", "float64", "string"]
  .map((t) => ({ value: t, label: t }));
const AREA_OPTIONS = [
  { value: "HR", label: "Holding Register (HR)" },
  { value: "IR", label: "Input Register (IR)" },
  { value: "DI", label: "Discrete Input (DI)" },
  { value: "CO", label: "Coil (CO)" },
];
const DRIVER_LABELS = { modbus_tcp_client: "Modbus TCP", modbus_rtu_client: "Modbus RTU", mercury_client: "Меркурий", opcua_client: "OPC UA" };
const MERCURY_PARAM_OPTIONS = [
  { value: "energy_active_total", label: "Энергия, активная, суммарно" },
  { value: "energy_reactive_total", label: "Энергия, реактивная, суммарно" },
  { value: "voltage_a", label: "Напряжение, фаза A" },
  { value: "voltage_b", label: "Напряжение, фаза B" },
  { value: "voltage_c", label: "Напряжение, фаза C" },
  { value: "current_a", label: "Ток, фаза A" },
  { value: "current_b", label: "Ток, фаза B" },
  { value: "current_c", label: "Ток, фаза C" },
  { value: "power_total", label: "Мощность, суммарно" },
  { value: "power_a", label: "Мощность, фаза A" },
  { value: "power_b", label: "Мощность, фаза B" },
  { value: "power_c", label: "Мощность, фаза C" },
  { value: "frequency", label: "Частота сети" },
];

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error([].concat(data.errors ?? data.error ?? res.statusText).join("; "));
  $("#reload-banner").hidden = false;
  return data;
}

$("#reload-btn").addEventListener("click", async () => {
  await fetch("/runtime/reload", { method: "POST" });
  $("#reload-banner").hidden = true;
  await loadConfig();
  tick();
});

async function loadConfig() {
  [cfgSources, cfgOutputs] = await Promise.all([getJson("/config/channels"), getJson("/config/outputs")]);
  renderCfgSources();
  renderCfgOutputs();
}

// Re-rendered from scratch after every edit (simplest correct option), so "expanded" state has to be
// tracked here rather than left to the DOM, or every add/edit/delete would visibly collapse the panel.
const openSourceNames = new Set();
const openOutputNames = new Set();

function toggleEntity(entity, e, name, openSet) {
  if (e.target.closest("[data-act]")) return;
  entity.classList.toggle("open");
  if (entity.classList.contains("open")) openSet.add(name); else openSet.delete(name);
}

function renderCfgSources() {
  const el = $("#cfg-sources");
  el.innerHTML = "";
  if (cfgSources.length === 0) { el.innerHTML = `<div class="empty">нет источников</div>`; return; }

  for (const s of cfgSources) {
    const summary = s.driver === "opcua_client"
      ? (s.settings.endpoint ?? "")
      : s.driver === "modbus_rtu_client" || s.driver === "mercury_client"
      ? `${s.settings.serial_port ?? "?"} @ ${s.settings.baud_rate ?? "9600"} addr ${s.settings.unit_id ?? "1"}`
      : `${s.settings.host ?? "?"}:${s.settings.port ?? "?"} unit ${s.settings.unit_id ?? "1"}`;

    const entity = document.createElement("div");
    entity.className = "entity" + (openSourceNames.has(s.name) ? " open" : "");
    entity.innerHTML = `
      <div class="entity-row">
        <span class="caret">▶</span>
        <span class="title">${esc(s.name)}</span>
        <span class="subtitle">${esc(DRIVER_LABELS[s.driver] ?? s.driver)} · ${esc(summary)} · ${s.tags.length} тег(ов)</span>
        <span class="actions">
          <button class="icon-btn" data-act="edit-source" title="изменить">✎</button>
          <button class="icon-btn danger" data-act="del-source" title="удалить">✕</button>
        </span>
      </div>
      <div class="entity-children">
        <div class="tags-box"></div>
        <button class="icon-btn add-child" data-act="add-tag">+ тег</button>
      </div>`;
    entity.querySelector(".entity-row").addEventListener("click", (e) => toggleEntity(entity, e, s.name, openSourceNames));
    entity.querySelector('[data-act="edit-source"]').addEventListener("click", () => editSource(s));
    entity.querySelector('[data-act="del-source"]').addEventListener("click", () => deleteSource(s));
    entity.querySelector('[data-act="add-tag"]').addEventListener("click", () => editTag(s, null));

    const tagsBox = entity.querySelector(".tags-box");
    for (const t of s.tags) {
      const loc = s.driver === "opcua_client" ? t.node_id
        : s.driver === "mercury_client" ? t.param
        : `${t.area}:${t.address}${t.bit != null ? "." + t.bit : ""}`;
      const row = document.createElement("div");
      row.className = "child-row";
      row.innerHTML = `
        <span class="name">${esc(t.name)}</span>
        <span class="desc">${esc(t.type)} · ${esc(loc ?? "")}${t.access === "RW" ? " · RW" : ""}</span>
        <span class="actions">
          <button class="icon-btn" data-act="edit" title="изменить">✎</button>
          <button class="icon-btn danger" data-act="del" title="удалить">✕</button>
        </span>`;
      row.querySelector('[data-act="edit"]').addEventListener("click", () => editTag(s, t));
      row.querySelector('[data-act="del"]').addEventListener("click", () => deleteTag(s, t));
      tagsBox.appendChild(row);
    }
    el.appendChild(entity);
  }
}

function renderCfgOutputs() {
  const el = $("#cfg-outputs");
  el.innerHTML = "";
  if (cfgOutputs.length === 0) { el.innerHTML = `<div class="empty">нет выходов</div>`; return; }

  for (const o of cfgOutputs) {
    const entity = document.createElement("div");
    entity.className = "entity" + (openOutputNames.has(o.name) ? " open" : "");
    entity.innerHTML = `
      <div class="entity-row">
        <span class="caret">▶</span>
        <span class="title">${esc(o.name)}</span>
        <span class="subtitle">Modbus TCP Server · ${esc(o.settings.bind ?? "0.0.0.0")}:${esc(o.settings.port ?? "502")} · ${o.map.length} записей карты</span>
        <span class="actions">
          <button class="icon-btn" data-act="edit-output" title="изменить">✎</button>
          <button class="icon-btn danger" data-act="del-output" title="удалить">✕</button>
        </span>
      </div>
      <div class="entity-children">
        <div class="map-box"></div>
        <button class="icon-btn add-child" data-act="add-map">+ запись карты</button>
      </div>`;
    entity.querySelector(".entity-row").addEventListener("click", (e) => toggleEntity(entity, e, o.name, openOutputNames));
    entity.querySelector('[data-act="edit-output"]').addEventListener("click", () => editOutput(o));
    entity.querySelector('[data-act="del-output"]').addEventListener("click", () => deleteOutput(o));
    entity.querySelector('[data-act="add-map"]').addEventListener("click", () => editMapEntry(o, null));

    const mapBox = entity.querySelector(".map-box");
    for (const m of o.map) {
      const row = document.createElement("div");
      row.className = "child-row";
      row.innerHTML = `
        <span class="name">${esc(m.tag)}</span>
        <span class="desc">unit ${esc(m.unit_id)} · ${esc(m.area)}:${esc(m.address)} · ${esc(m.type)}${m.rw ? " · RW" : ""}</span>
        <span class="actions">
          <button class="icon-btn" data-act="edit" title="изменить">✎</button>
          <button class="icon-btn danger" data-act="del" title="удалить">✕</button>
        </span>`;
      row.querySelector('[data-act="edit"]').addEventListener("click", () => editMapEntry(o, m));
      row.querySelector('[data-act="del"]').addEventListener("click", () => deleteMapEntry(o, m));
      mapBox.appendChild(row);
    }
    el.appendChild(entity);
  }
}

// ---- Field specs (kept deliberately flat: both driver's fields shown together, unused ones are
// simply not sent — far less code than a dynamic show/hide-by-driver form). ----

function sourceFields() {
  return [
    { key: "name", label: "Имя источника", type: "text" },
    { key: "driver", label: "Драйвер", type: "select", options: [
      { value: "modbus_tcp_client", label: "Modbus TCP Client" },
      { value: "modbus_rtu_client", label: "Modbus RTU (RS-485/serial)" },
      { value: "mercury_client", label: "Меркурий (RS-485, счётчики электроэнергии)" },
      { value: "opcua_client", label: "OPC UA Client" },
    ] },
    { key: "host", label: "Host/IP (Modbus TCP)", type: "text" },
    { key: "port", label: "Port (Modbus TCP)", type: "number" },
    { key: "serial_port", label: "Serial port (Modbus RTU / Меркурий)", type: "text", placeholder: "COM3 или /dev/ttyUSB0" },
    { key: "baud_rate", label: "Baud rate (Modbus RTU / Меркурий)", type: "number", placeholder: "9600" },
    { key: "parity", label: "Parity (Modbus RTU / Меркурий)", type: "select", options: [
      { value: "even", label: "Even" },
      { value: "odd", label: "Odd" },
      { value: "none", label: "None" },
    ] },
    { key: "stop_bits", label: "Stop bits (Modbus RTU / Меркурий)", type: "select", options: [
      { value: "one", label: "1" },
      { value: "two", label: "2" },
    ] },
    { key: "unit_id", label: "Unit ID / адрес (Modbus / Меркурий)", type: "number" },
    { key: "access_level", label: "Уровень доступа (Меркурий)", type: "select", options: [
      { value: "1", label: "1 — чтение (по умолчанию)" },
      { value: "2", label: "2 — админ" },
    ] },
    { key: "password", label: "Пароль, 6 цифр (Меркурий, необязательно)", type: "text", placeholder: "по умолчанию для уровня 1" },
    { key: "scan_rate_ms", label: "Scan rate, ms (Modbus / Меркурий)", type: "number" },
    { key: "endpoint", label: "Endpoint URL (OPC UA)", type: "text", placeholder: "opc.tcp://host:4840" },
    { key: "__extra", label: "Доп. поля (JSON)", type: "textarea", placeholder: '{"timeout_ms": 1000, "retries": 2}' },
  ];
}

function tagFields() {
  return [
    { key: "name", label: "Имя тега", type: "text" },
    { key: "type", label: "Тип", type: "select", options: TYPE_OPTIONS },
    { key: "access", label: "Доступ", type: "select", options: [{ value: "RO", label: "RO" }, { value: "RW", label: "RW" }] },
    { key: "area", label: "Область (Modbus)", type: "select", options: AREA_OPTIONS },
    { key: "address", label: "Адрес (Modbus)", type: "number" },
    { key: "node_id", label: "NodeId (OPC UA)", type: "text", placeholder: "ns=2;s=Device.Tag" },
    { key: "param", label: "Параметр (Меркурий)", type: "select", options: MERCURY_PARAM_OPTIONS },
    { key: "units", label: "Единицы измерения", type: "text" },
    { key: "__extra", label: "Доп. поля (JSON)", type: "textarea", placeholder: '{"deadband": 0.5, "write_min": 0, "write_max": 100}' },
  ];
}

function outputFields() {
  return [
    { key: "name", label: "Имя выхода", type: "text" },
    { key: "bind", label: "Bind address", type: "text", placeholder: "0.0.0.0" },
    { key: "port", label: "Port", type: "number" },
    { key: "read_only", label: "Только чтение (read_only)", type: "checkbox" },
    { key: "__extra", label: "Доп. поля (JSON)", type: "textarea", placeholder: '{"max_connections": 16}' },
  ];
}

function mapEntryFields() {
  return [
    { key: "tag", label: "Tag ID", type: "text", placeholder: "ctp_12.T1_supply" },
    { key: "unit_id", label: "Unit ID", type: "number" },
    { key: "area", label: "Область", type: "select", options: AREA_OPTIONS },
    { key: "address", label: "Адрес", type: "number" },
    { key: "type", label: "Тип", type: "select", options: TYPE_OPTIONS },
    { key: "rw", label: "Разрешить запись (rw)", type: "checkbox" },
    { key: "__extra", label: "Доп. поля (JSON)", type: "textarea", placeholder: '{"on_bad": "hold"}' },
  ];
}

// ---- Generic modal form ----

const dialog = $("#form-dialog");
const formEl = $("#form-el");
$("#form-cancel").addEventListener("click", () => dialog.close());

function renderField(f, value) {
  const wrap = document.createElement("div");
  wrap.className = "field" + (f.type === "checkbox" ? " checkbox" : "");
  const id = "f_" + f.key;
  if (f.type === "select") {
    wrap.innerHTML = `<label for="${id}">${esc(f.label)}</label>
      <select id="${id}" data-key="${f.key}">${f.options.map((o) => `<option value="${esc(o.value)}">${esc(o.label)}</option>`).join("")}</select>`;
  } else if (f.type === "textarea") {
    wrap.innerHTML = `<label for="${id}">${esc(f.label)}</label><textarea id="${id}" data-key="${f.key}" placeholder="${esc(f.placeholder ?? "")}"></textarea>`;
  } else if (f.type === "checkbox") {
    wrap.innerHTML = `<input type="checkbox" id="${id}" data-key="${f.key}"><label for="${id}">${esc(f.label)}</label>`;
  } else {
    wrap.innerHTML = `<label for="${id}">${esc(f.label)}</label><input type="${f.type}" id="${id}" data-key="${f.key}" placeholder="${esc(f.placeholder ?? "")}">`;
  }
  const input = wrap.querySelector("[data-key]");
  if (value !== undefined && value !== null) {
    if (f.type === "checkbox") input.checked = Boolean(value);
    else input.value = value;
  }
  return wrap;
}

function collectForm(fields) {
  const out = {};
  for (const f of fields) {
    if (f.key === "__extra") continue;
    const input = $(`[data-key="${f.key}"]`);
    if (f.type === "checkbox") { out[f.key] = input.checked; continue; }
    const raw = input.value.trim();
    if (raw === "") continue;
    out[f.key] = f.type === "number" ? Number(raw) : raw;
  }
  const extraInput = document.querySelector('[data-key="__extra"]');
  if (extraInput && extraInput.value.trim()) {
    let extra;
    try { extra = JSON.parse(extraInput.value); }
    catch { throw new Error("«Доп. поля (JSON)» — некорректный JSON"); }
    return { ...extra, ...out };
  }
  return out;
}

function openForm(title, fields, values, onSubmit) {
  $("#form-title").textContent = title;
  $("#form-error").hidden = true;
  const box = $("#form-fields");
  box.innerHTML = "";
  for (const f of fields) box.appendChild(renderField(f, values?.[f.key]));

  formEl.onsubmit = async (e) => {
    e.preventDefault();
    try {
      const payload = collectForm(fields);
      await onSubmit(payload);
      dialog.close();
    } catch (err) {
      $("#form-error").textContent = err.message || String(err);
      $("#form-error").hidden = false;
    }
  };
  dialog.showModal();
}

// ---- CRUD actions ----

$("#add-source-btn").addEventListener("click", () => editSource(null));
$("#add-output-btn").addEventListener("click", () => editOutput(null));

function editSource(existing) {
  const values = existing ? { ...existing, ...existing.settings } : { driver: "modbus_tcp_client" };
  openForm(existing ? `Источник: ${existing.name}` : "Новый источник", sourceFields(), values, async (payload) => {
    if (existing) await api("PUT", `/config/channels/${encodeURIComponent(existing.name)}`, payload);
    else await api("POST", "/config/channels", payload);
    await loadConfig();
  });
}

function deleteSource(s) {
  if (!confirm(`Удалить источник «${s.name}» и все его теги (${s.tags.length})?`)) return;
  api("DELETE", `/config/channels/${encodeURIComponent(s.name)}`).then(loadConfig);
}

function editTag(source, existing) {
  openForm(existing ? `Тег: ${source.name}.${existing.name}` : `Новый тег в ${source.name}`, tagFields(), existing, async (payload) => {
    if (existing) await api("PUT", `/config/channels/${encodeURIComponent(source.name)}/tags/${encodeURIComponent(existing.name)}`, payload);
    else await api("POST", `/config/channels/${encodeURIComponent(source.name)}/tags`, payload);
    await loadConfig();
  });

  // OPC UA tags need a NodeId, which is unguessable without seeing the server's address space —
  // wire the already-built /config/opcua/browse endpoint into the form instead of leaving it
  // reachable only via curl.
  if (source.driver === "opcua_client") {
    const nodeInput = document.querySelector('[data-key="node_id"]');
    if (nodeInput) {
      const btn = document.createElement("button");
      btn.type = "button";
      btn.id = "node-id-browse-btn";
      btn.className = "icon-btn";
      btn.textContent = "Обзор…";
      btn.addEventListener("click", () => openBrowseDialog(source.name, (nodeId) => { nodeInput.value = nodeId; }));
      nodeInput.insertAdjacentElement("afterend", btn);
    }
  }
}

function deleteTag(source, tag) {
  if (!confirm(`Удалить тег «${tag.name}»?`)) return;
  api("DELETE", `/config/channels/${encodeURIComponent(source.name)}/tags/${encodeURIComponent(tag.name)}`).then(loadConfig);
}

function editOutput(existing) {
  const values = existing ? { ...existing, ...existing.settings } : {};
  openForm(existing ? `Выход: ${existing.name}` : "Новый выход", outputFields(), values, async (payload) => {
    payload.interface = "modbus_tcp_server";
    if (existing) await api("PUT", `/config/outputs/${encodeURIComponent(existing.name)}`, payload);
    else await api("POST", "/config/outputs", payload);
    await loadConfig();
  });
}

function deleteOutput(o) {
  if (!confirm(`Удалить выход «${o.name}» и карту регистров (${o.map.length} записей)?`)) return;
  api("DELETE", `/config/outputs/${encodeURIComponent(o.name)}`).then(loadConfig);
}

function editMapEntry(output, existing) {
  openForm(existing ? `Карта: ${existing.tag}` : `Новая запись карты в ${output.name}`, mapEntryFields(), existing, async (payload) => {
    if (existing) await api("PUT", `/config/outputs/${encodeURIComponent(output.name)}/map/${encodeURIComponent(existing.tag)}`, payload);
    else await api("POST", `/config/outputs/${encodeURIComponent(output.name)}/map`, payload);
    await loadConfig();
  });
}

function deleteMapEntry(output, entry) {
  if (!confirm(`Удалить запись карты «${entry.tag}»?`)) return;
  api("DELETE", `/config/outputs/${encodeURIComponent(output.name)}/map/${encodeURIComponent(entry.tag)}`).then(loadConfig);
}

// ============================================================================================
// Modbus scanner — a built-in ModScan: read/write any device by IP:port, independent of
// project.yaml. Talks to /tools/modbus/*, which pools one TCP connection per host:port on the
// server side so a 1x/sec poll from here doesn't reopen a socket every tick.
// ============================================================================================

const SCAN_AREA_OPTIONS = [
  { value: "HR", label: "Holding Register (HR) — R/W" },
  { value: "IR", label: "Input Register (IR) — RO" },
  { value: "CO", label: "Coil (CO) — R/W" },
  { value: "DI", label: "Discrete Input (DI) — RO" },
];
const WORD_ORDER_OPTIONS = ["ABCD", "CDAB", "BADC", "DCBA"];
const NUMERIC_TYPES = ["int16", "uint16", "int32", "uint32", "int64", "uint64", "float32", "float64"];

let scanTimer = null;

async function postJson(url, body) {
  const res = await fetch(url, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || res.statusText);
  return data;
}

function isBitArea(area) { return area === "CO" || area === "DI"; }

function initScanner() {
  $("#sc-area").innerHTML = SCAN_AREA_OPTIONS.map((o) => `<option value="${o.value}">${esc(o.label)}</option>`).join("");
  $("#sc-type").innerHTML = TYPE_OPTIONS.filter((o) => o.value !== "bool" && o.value !== "string")
    .map((o) => `<option value="${o.value}">${esc(o.label)}</option>`).join("");
  $("#sc-order").innerHTML = WORD_ORDER_OPTIONS.map((o) => `<option value="${o}">${o}</option>`).join("");

  $("#sc-area").addEventListener("change", () => {
    const bit = isBitArea($("#sc-area").value);
    $("#sc-type-field").hidden = bit;
    $("#sc-order-field").hidden = bit;
  });
}

function scanTarget() {
  return {
    host: $("#sc-host").value.trim(),
    port: Number($("#sc-port").value) || 502,
    unit_id: Number($("#sc-unit").value) || 1,
    area: $("#sc-area").value,
    address: Number($("#sc-address").value) || 0,
    length: Number($("#sc-length").value) || 1,
    type: $("#sc-type").value,
    word_order: $("#sc-order").value,
  };
}

function setScanStatus(ok, text) {
  $("#sc-status").innerHTML = `<span class="dot ${ok ? "good" : "bad"}"></span>${esc(text)}`;
}

async function scanOnce() {
  const target = scanTarget();
  if (!target.host) { setScanStatus(false, "укажи host"); return; }
  try {
    const res = await postJson("/tools/modbus/read", target);
    if (!res.ok) {
      // Leave the last-known rows on screen (a live PLC/pooled connection can hiccup transiently and
      // recover next tick) but mark them stale, so a failed read is never mistaken for "value didn't
      // change" — that would be actively misleading right after a write.
      $("#sc-body").classList.add("stale");
      setScanStatus(false, res.error);
      return;
    }
    $("#sc-body").classList.remove("stale");
    renderScanRows(target, res.rows);
    setScanStatus(true, `${res.latency_ms} мс · ${res.rows.length} значений`);
  } catch (err) {
    $("#sc-body").classList.add("stale");
    setScanStatus(false, err.message || String(err));
  }
}

function renderScanRows(target, rows) {
  const tbody = $("#sc-body");
  tbody.innerHTML = "";
  if (rows.length === 0) { tbody.innerHTML = `<tr><td colspan="5" class="empty">нет данных</td></tr>`; return; }
  const writable = target.area === "HR" || target.area === "CO";
  for (const r of rows) {
    const tr = document.createElement("tr");
    tr.innerHTML = `
      <td class="id">${r.address}</td>
      <td class="value">${esc(r.hex)}</td>
      <td class="value">${esc(r.binary)}</td>
      <td class="value">${esc(fmtValue(r.value))}</td>
      <td>${writable ? `<button class="icon-btn" title="записать">✎</button>` : ""}</td>`;
    if (writable) tr.querySelector(".icon-btn").addEventListener("click", () => openScanWriteForm(target, r));
    tbody.appendChild(tr);
  }
}

function openScanWriteForm(target, row) {
  const isBit = target.area === "CO";
  const fields = isBit
    ? [{ key: "value", label: `Значение — адрес ${row.address} (coil)`, type: "select",
         options: [{ value: "true", label: "1 / ON" }, { value: "false", label: "0 / OFF" }] }]
    : [{ key: "value", label: `Значение — адрес ${row.address} (${target.type})`, type: "text" }];
  const initial = { value: isBit ? String(Boolean(row.value)) : row.value };

  openForm(`Запись: ${target.area}:${row.address}`, fields, initial, async (payload) => {
    let value = payload.value;
    if (isBit) value = value === "true" || value === true;
    else if (NUMERIC_TYPES.includes(target.type)) value = Number(value);

    const res = await postJson("/tools/modbus/write", {
      host: target.host, port: target.port, unit_id: target.unit_id, area: target.area,
      address: row.address, type: target.type, word_order: target.word_order, value,
    });
    if (!res.ok) throw new Error(res.error);
    await scanOnce();
  });
}

function stopScan() {
  if (!scanTimer) return;
  clearInterval(scanTimer);
  scanTimer = null;
  $("#sc-start").hidden = false;
  $("#sc-stop").hidden = true;
}

initScanner();

$("#sc-start").addEventListener("click", () => {
  const ms = Math.max(200, Number($("#sc-interval").value) || 1000);
  scanOnce();
  scanTimer = setInterval(scanOnce, ms);
  $("#sc-start").hidden = true;
  $("#sc-stop").hidden = false;
});
$("#sc-stop").addEventListener("click", stopScan);
$("#sc-once").addEventListener("click", scanOnce);
$("#sc-disconnect").addEventListener("click", async () => {
  stopScan();
  const host = $("#sc-host").value.trim();
  if (host) await postJson("/tools/modbus/close", { host, port: Number($("#sc-port").value) || 502 }).catch(() => {});
  setScanStatus(true, "отключено");
  $("#sc-body").innerHTML = `<tr><td colspan="5" class="empty">нет данных — заполни адрес устройства и нажми «Опрос»</td></tr>`;
});

// ============================================================================================
// OPC UA address-space browse — expandable tree over the existing /config/opcua/browse endpoint
// (browses one level at a time, lazily, same as any real OPC UA client: eagerly recursing the
// whole tree up front could mean thousands of requests against a big PLC address space).
// ============================================================================================

const browseDialog = $("#browse-dialog");
$("#browse-close").addEventListener("click", () => browseDialog.close());

function openBrowseDialog(sourceName, onSelect) {
  const tree = $("#browse-tree");
  tree.innerHTML = "";
  browseDialog.showModal();
  loadBrowseLevel(tree, sourceName, null, 0, onSelect);
}

async function loadBrowseLevel(container, sourceName, nodeId, depth, onSelect) {
  container.innerHTML = `<div class="empty">загрузка…</div>`;
  let nodes;
  try {
    nodes = await postJson("/config/opcua/browse", { source: sourceName, node_id: nodeId });
  } catch (err) {
    container.innerHTML = `<div class="empty">${esc(err.message)}</div>`;
    return;
  }

  container.innerHTML = "";
  if (nodes.length === 0) { container.innerHTML = `<div class="empty">пусто</div>`; return; }

  for (const n of nodes) container.appendChild(renderBrowseNode(n, sourceName, depth, onSelect));
}

function renderBrowseNode(n, sourceName, depth, onSelect) {
  const wrap = document.createDocumentFragment();
  const row = document.createElement("div");
  row.className = "browse-row";
  row.style.paddingLeft = `${8 + depth * 16}px`;
  row.innerHTML = `
    <span class="browse-caret">${n.hasChildren ? "▶" : "•"}</span>
    <span class="browse-name">${esc(n.browseName)}</span>
    <span class="browse-class">${esc(n.nodeClass)}</span>
    <span class="browse-id">${esc(n.nodeId)}</span>
    <button type="button" class="icon-btn" data-act="select">выбрать</button>`;

  row.querySelector('[data-act="select"]').addEventListener("click", () => {
    onSelect(n.nodeId, n.browseName);
    browseDialog.close();
  });

  const childBox = document.createElement("div");
  childBox.hidden = true;
  if (n.hasChildren) {
    const caret = row.querySelector(".browse-caret");
    caret.addEventListener("click", async () => {
      childBox.hidden = !childBox.hidden;
      caret.textContent = childBox.hidden ? "▶" : "▼";
      if (!childBox.hidden && !childBox.dataset.loaded) {
        childBox.dataset.loaded = "1";
        await loadBrowseLevel(childBox, sourceName, n.nodeId, depth + 1, onSelect);
      }
    });
  }

  wrap.appendChild(row);
  wrap.appendChild(childBox);
  return wrap;
}
