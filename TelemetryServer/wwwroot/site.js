const state = { markers: [], colors: new Map(), points: [] };
const palette = ["#ffcc66", "#f1787d", "#72d6c9", "#87a9ff", "#c892ff", "#ff9f5a", "#88d66c", "#e5e7eb"];
const $ = id => document.getElementById(id);
const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

async function getJson(path) {
  const response = await fetch(path, { headers: { accept: "application/json" } });
  if (!response.ok) throw new Error(`${response.status}`);
  return response.json();
}

function colorFor(kind) {
  if (!state.colors.has(kind)) state.colors.set(kind, palette[state.colors.size % palette.length]);
  return state.colors.get(kind);
}

async function refresh() {
  try {
    const territory = $("territory").value.trim();
    const map = $("map").value.trim();
    const query = new URLSearchParams({ limit: "500" });
    if (territory) query.set("territoryId", territory);
    if (map) query.set("mapId", map);
    const [stats, data] = await Promise.all([
      getJson("./api/v1/stats"),
      getJson(`./api/v1/markers?${query}`),
    ]);
    state.markers = data.markers;
    $("unique").textContent = stats.uniqueMarkers.toLocaleString();
    $("reports").textContent = stats.totalReports.toLocaleString();
    $("visible").textContent = state.markers.length.toLocaleString();
    $("health").textContent = "服务在线";
    $("health").className = "health ok";
    renderKinds(stats.kinds);
    renderRows();
    drawPlot();
  } catch (error) {
    $("health").textContent = `连接失败 ${error.message}`;
    $("health").className = "health";
  }
}

function renderKinds(kinds) {
  $("kinds").innerHTML = kinds.map(item => `
    <div class="kind">
      <span class="swatch" style="background:${colorFor(item.kind)}"></span>
      <span title="${esc(item.kind)}">${esc(item.kind)}</span>
      <strong>${Number(item.count).toLocaleString()}</strong>
    </div>`).join("");
}

function renderRows() {
  $("rows").innerHTML = state.markers.map(marker => {
    const id = marker.name || [marker.baseId && `Base ${marker.baseId}`, marker.eventId && `Event ${marker.eventId}`].filter(Boolean).join(" / ") || "—";
    return `<tr>
      <td><span class="swatch" style="background:${colorFor(marker.kind)};display:inline-block;margin-right:7px"></span>${esc(marker.kind)}</td>
      <td>${marker.territoryId} / ${marker.mapId}</td>
      <td>${esc(id)}</td>
      <td>${marker.x.toFixed(2)}, ${marker.y.toFixed(2)}, ${marker.z.toFixed(2)}</td>
      <td>${marker.reportCount}</td>
    </tr>`;
  }).join("");
}

function drawPlot() {
  const canvas = $("plot");
  const rect = canvas.getBoundingClientRect();
  const dpr = Math.min(devicePixelRatio || 1, 2);
  canvas.width = Math.max(1, Math.floor(rect.width * dpr));
  canvas.height = Math.max(1, Math.floor(rect.height * dpr));
  const ctx = canvas.getContext("2d");
  ctx.scale(dpr, dpr);
  const width = rect.width, height = rect.height, pad = 34;
  ctx.clearRect(0, 0, width, height);
  ctx.strokeStyle = "#273144";
  ctx.fillStyle = "#8290a8";
  ctx.font = "11px system-ui";
  for (let i = 0; i <= 5; i++) {
    const x = pad + (width - pad * 2) * i / 5;
    const y = pad + (height - pad * 2) * i / 5;
    ctx.beginPath(); ctx.moveTo(x, pad); ctx.lineTo(x, height - pad); ctx.stroke();
    ctx.beginPath(); ctx.moveTo(pad, y); ctx.lineTo(width - pad, y); ctx.stroke();
  }
  if (!state.markers.length) {
    ctx.fillText("当前筛选没有数据", pad, pad);
    state.points = [];
    return;
  }
  const xs = state.markers.map(m => m.x), zs = state.markers.map(m => m.z);
  let minX = Math.min(...xs), maxX = Math.max(...xs), minZ = Math.min(...zs), maxZ = Math.max(...zs);
  if (minX === maxX) { minX--; maxX++; }
  if (minZ === maxZ) { minZ--; maxZ++; }
  state.points = state.markers.map(marker => {
    const x = pad + (marker.x - minX) / (maxX - minX) * (width - pad * 2);
    const y = height - pad - (marker.z - minZ) / (maxZ - minZ) * (height - pad * 2);
    ctx.fillStyle = colorFor(marker.kind);
    ctx.globalAlpha = .85;
    ctx.beginPath(); ctx.arc(x, y, 4.5, 0, Math.PI * 2); ctx.fill();
    return { x, y, marker };
  });
  ctx.globalAlpha = 1;
  ctx.fillStyle = "#8290a8";
  ctx.fillText(`X ${minX.toFixed(1)} → ${maxX.toFixed(1)}`, pad, height - 8);
  ctx.save(); ctx.translate(12, height - pad); ctx.rotate(-Math.PI / 2);
  ctx.fillText(`Z ${minZ.toFixed(1)} → ${maxZ.toFixed(1)}`, 0, 0); ctx.restore();
}

$("plot").addEventListener("mousemove", event => {
  const rect = event.currentTarget.getBoundingClientRect();
  const x = event.clientX - rect.left, y = event.clientY - rect.top;
  const hit = state.points.find(point => Math.hypot(point.x - x, point.y - y) < 8);
  const tip = $("tooltip");
  if (!hit) { tip.hidden = true; return; }
  const marker = hit.marker;
  tip.innerHTML = `<strong>${esc(marker.kind)}</strong><br>${esc(marker.name || "")}<br>
    T${marker.territoryId} / M${marker.mapId}<br>X ${marker.x.toFixed(2)} · Y ${marker.y.toFixed(2)} · Z ${marker.z.toFixed(2)}`;
  tip.style.left = `${Math.min(x + 18, rect.width - 290)}px`;
  tip.style.top = `${Math.max(8, y - 22)}px`;
  tip.hidden = false;
});
$("plot").addEventListener("mouseleave", () => $("tooltip").hidden = true);
$("refresh").addEventListener("click", refresh);
window.addEventListener("resize", drawPlot);
refresh();
