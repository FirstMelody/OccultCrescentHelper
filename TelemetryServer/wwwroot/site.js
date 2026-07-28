const state = {
  markers: [],
  colors: new Map(),
  points: [],
  maps: [],
  selectedMap: null,
  mapImages: new Map(),
};
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

function normalizeMap(raw) {
  return {
    territoryId: raw.territoryId ?? raw.TerritoryId,
    mapId: raw.mapId ?? raw.MapId,
    mapResourceId: raw.mapResourceId ?? raw.MapResourceId,
    placeName: raw.placeName ?? raw.PlaceName,
    contentName: raw.contentName ?? raw.ContentName,
    sizeFactor: raw.sizeFactor ?? raw.SizeFactor ?? 100,
    offsetX: raw.offsetX ?? raw.OffsetX ?? 0,
    offsetY: raw.offsetY ?? raw.OffsetY ?? 0,
    image: raw.image ?? raw.Image,
    width: raw.width ?? raw.Width ?? 2048,
    height: raw.height ?? raw.Height ?? 2048,
  };
}

async function loadCatalog() {
  const catalog = await getJson("./maps/catalog.json");
  state.maps = (catalog.maps ?? []).map(normalizeMap);
  const picker = $("mapSelect");
  picker.innerHTML = state.maps.map(map =>
    `<option value="${map.territoryId}:${map.mapId}">${esc(map.contentName)} · ${esc(map.placeName)}</option>`
  ).join("");
  const north = state.maps.find(map => map.territoryId === 1346) ?? state.maps[0];
  if (north) picker.value = `${north.territoryId}:${north.mapId}`;
  selectCurrentMap();
}

function selectCurrentMap() {
  const [territoryId, mapId] = $("mapSelect").value.split(":").map(Number);
  state.selectedMap = state.maps.find(map => map.territoryId === territoryId && map.mapId === mapId) ?? null;
  const map = state.selectedMap;
  $("territory").textContent = map?.territoryId ?? "—";
  $("map").textContent = map?.mapId ?? "—";
  $("mapResource").textContent = map?.mapResourceId ?? "—";
  $("mapTitle").textContent = map ? `${map.placeName}点位` : "游戏地图点位";
}

async function refresh() {
  try {
    if (!state.maps.length) await loadCatalog();
    selectCurrentMap();
    const map = state.selectedMap;
    const query = new URLSearchParams({ limit: "1000" });
    if (map) {
      query.set("territoryId", map.territoryId);
      query.set("mapId", map.mapId);
    }
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
    await drawMap();
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

function loadMapImage(map) {
  if (state.mapImages.has(map.image)) return state.mapImages.get(map.image);
  const promise = new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = reject;
    image.src = `./maps/${map.image}`;
  });
  state.mapImages.set(map.image, promise);
  return promise;
}

function markerToPixel(marker, map, width, height) {
  const scale = map.sizeFactor / 100;
  const mapX = marker.x * scale + map.offsetX * (scale - 1);
  const mapY = marker.z * scale + map.offsetY * (scale - 1);
  return {
    x: (mapX + 1024) / 2048 * width,
    y: (mapY + 1024) / 2048 * height,
  };
}

async function drawMap() {
  const canvas = $("plot");
  const map = state.selectedMap;
  if (!map) return;
  const rect = canvas.getBoundingClientRect();
  const dpr = Math.min(devicePixelRatio || 1, 2);
  const width = rect.width;
  const height = rect.width;
  canvas.width = Math.max(1, Math.floor(width * dpr));
  canvas.height = Math.max(1, Math.floor(height * dpr));
  const ctx = canvas.getContext("2d");
  ctx.scale(dpr, dpr);
  ctx.clearRect(0, 0, width, height);

  const image = await loadMapImage(map);
  ctx.drawImage(image, 0, 0, width, height);
  ctx.fillStyle = "#07101a12";
  ctx.fillRect(0, 0, width, height);

  state.points = state.markers.map(marker => {
    const point = markerToPixel(marker, map, width, height);
    const color = colorFor(marker.kind);
    if (marker.mechanicRadius > 0) {
      const radius = marker.mechanicRadius * (map.sizeFactor / 100) / 2048 * width;
      ctx.fillStyle = `${color}28`;
      ctx.strokeStyle = `${color}bb`;
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.arc(point.x, point.y, Math.max(3, radius), 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    }
    ctx.fillStyle = color;
    ctx.strokeStyle = "#111827dd";
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.arc(point.x, point.y, 6, 0, Math.PI * 2);
    ctx.stroke();
    ctx.fill();
    ctx.strokeStyle = "#ffffffee";
    ctx.lineWidth = 1.2;
    ctx.beginPath();
    ctx.arc(point.x, point.y, 6, 0, Math.PI * 2);
    ctx.stroke();
    return { ...point, marker };
  });

  ctx.strokeStyle = "#12182688";
  ctx.lineWidth = 1;
  ctx.strokeRect(.5, .5, width - 1, height - 1);
}

$("plot").addEventListener("mousemove", event => {
  const rect = event.currentTarget.getBoundingClientRect();
  const x = event.clientX - rect.left, y = event.clientY - rect.top;
  const hit = state.points.find(point => Math.hypot(point.x - x, point.y - y) < 10);
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
$("mapSelect").addEventListener("change", refresh);
$("refresh").addEventListener("click", refresh);
window.addEventListener("resize", () => drawMap());
refresh();
