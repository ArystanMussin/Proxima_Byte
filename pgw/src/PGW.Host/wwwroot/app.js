const $ = (sel) => document.querySelector(sel);
let filter = "";

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
      row.innerHTML = `<span class="dot ${ok ? "good" : "bad"}"></span><span class="name">${name}</span><span class="err">${detail}</span>`;
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
      <td class="id">${t.id}</td>
      <td class="value">${fmtValue(t.value)}${t.units ? " " + t.units : ""}</td>
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
    row.innerHTML = `<span class="ts">${fmtTime(e.timestampUtc)}</span><span>[${e.source}] ${e.message}</span>`;
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
