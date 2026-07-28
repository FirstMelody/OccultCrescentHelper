const state = {
  markers: [],
  colors: new Map(),
  points: [],
  maps: [],
  selectedMap: null,
  mapImages: new Map(),
  markerIcons: new Map(),
  hiddenKinds: new Set(),
};
const palette = ["#ffcc66", "#f1787d", "#72d6c9", "#87a9ff", "#c892ff", "#ff9f5a", "#88d66c", "#e5e7eb"];
const kindIconIds = {
  BronzeChest: 60356,
  SilverChest: 60355,
  PotChest: 60354,
  UnknownChest: 60354,
  FortuneCarrotChest: 60354,
  FortuneCarrot: 25207,
  InvestigationLocation: 60474,
  Fate: 60502,
  CriticalEncounter: 63909,
};
const $ = id => document.getElementById(id);
const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);

async function getJson(path) {
  const separator = path.includes("?") ? "&" : "?";
  const response = await fetch(`${path}${separator}_=${Date.now()}`, {
    cache: "no-store",
    headers: {
      accept: "application/json",
      "cache-control": "no-cache, no-store, max-age=0",
      pragma: "no-cache",
    },
  });
  if (!response.ok) throw new Error(`${response.status}`);
  return response.json();
}

function colorFor(kind) {
  if (!state.colors.has(kind)) state.colors.set(kind, palette[state.colors.size % palette.length]);
  return state.colors.get(kind);
}

function isTrapKind(kind) {
  return kind === "SmallTrap" || kind === "BigTrap";
}

function iconIdForKind(kind) {
  return kindIconIds[kind] ?? null;
}

function isDrawableMarker(marker) {
  return isTrapKind(marker.kind)
    || iconIdForKind(marker.kind) !== null
    || marker.kind === "Monster"
    || marker.kind === "InvestigationLocation";
}

function kindVisual(kind) {
  const iconId = iconIdForKind(kind);
  if (iconId !== null) {
    return `<img class="kind-icon" src="./icons/${iconId}.webp" alt="">`;
  }
  if (isTrapKind(kind)) {
    return `<span class="trap-icon" aria-hidden="true"></span>`;
  }
  return `<span class="swatch" style="background:${colorFor(kind)}"></span>`;
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
    const statsQuery = new URLSearchParams(query);
    statsQuery.delete("limit");
    const [stats, data] = await Promise.all([
      getJson(`./api/v1/stats?${statsQuery}`),
      getJson(`./api/v1/markers?${query}`),
    ]);
    state.markers = data.markers;
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
    <button class="kind kind-toggle${state.hiddenKinds.has(item.kind) ? " is-hidden" : ""}"
      type="button" data-kind="${esc(item.kind)}"
      aria-pressed="${state.hiddenKinds.has(item.kind) ? "false" : "true"}">
      ${kindVisual(item.kind)}
      <span title="${esc(item.kind)}">${esc(item.kind)}</span>
      <strong>${Number(item.count).toLocaleString()}</strong>
    </button>`).join("");
}

function renderRows() {
  $("rows").innerHTML = state.markers
    .filter(marker => !state.hiddenKinds.has(marker.kind))
    .map(marker => {
    const id = marker.name || [marker.baseId && `Base ${marker.baseId}`, marker.eventId && `Event ${marker.eventId}`].filter(Boolean).join(" / ") || "—";
    return `<tr>
      <td><span class="row-kind">${kindVisual(marker.kind)}${esc(marker.kind)}</span></td>
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

function loadMarkerIcon(iconId) {
  if (state.markerIcons.has(iconId)) return state.markerIcons.get(iconId);
  const promise = new Promise(resolve => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => resolve(null);
    image.src = `./icons/${iconId}.webp`;
  });
  state.markerIcons.set(iconId, promise);
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

const monsterDisplayClusterRadius = 230;
const monsterLabelsPerCluster = 4;
const monsterLabelsPerColumn = 4;

function clusterMonsterMarkers(markers) {
  const centerOf = members => ({
    x: members.reduce((sum, marker) => sum + marker.x, 0) / members.length,
    y: members.reduce((sum, marker) => sum + marker.y, 0) / members.length,
    z: members.reduce((sum, marker) => sum + marker.z, 0) / members.length,
  });
  const clusters = [];
  const sortedMarkers = [...markers].sort((left, right) =>
    left.x - right.x || left.z - right.z || (left.baseId ?? 0) - (right.baseId ?? 0)
  );
  for (const marker of sortedMarkers) {
    let bestCluster = null;
    let bestDistance = Infinity;
    for (const cluster of clusters) {
      const members = [...cluster.members, marker];
      const center = centerOf(members);
      const distinctLabels = new Set(
        members.map(member => `${member.level ?? 0}|${member.name ?? ""}`)
      );
      if (distinctLabels.size > monsterLabelsPerCluster) continue;
      if (members.some(member =>
        Math.hypot(member.x - center.x, member.z - center.z) > monsterDisplayClusterRadius
      )) continue;
      const distance = Math.hypot(marker.x - cluster.center.x, marker.z - cluster.center.z);
      if (distance < bestDistance) {
        bestCluster = cluster;
        bestDistance = distance;
      }
    }
    if (!bestCluster) {
      clusters.push({
        members: [marker],
        center: { x: marker.x, y: marker.y, z: marker.z },
      });
    } else {
      bestCluster.members.push(marker);
      bestCluster.center = centerOf(bestCluster.members);
    }
  }
  return clusters;
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

  const drawableMarkers = state.markers.filter(marker =>
    isDrawableMarker(marker) && !state.hiddenKinds.has(marker.kind)
  );
  const iconIds = [
    ...new Set(
      drawableMarkers
        .map(marker => iconIdForKind(marker.kind))
        .filter(iconId => iconId !== null)
    ),
  ];
  const loadedIcons = new Map(
    await Promise.all(iconIds.map(async iconId => [iconId, await loadMarkerIcon(iconId)]))
  );
  const iconSize = Math.max(20, Math.min(34, width / 28));
  state.points = drawableMarkers.filter(marker => marker.kind !== "Monster").map(marker => {
    const point = markerToPixel(marker, map, width, height);
    if (isTrapKind(marker.kind)) {
      const mechanicRadius = marker.mechanicRadius > 0
        ? marker.mechanicRadius
        : marker.kind === "BigTrap" ? 30 : 7;
      const radius = mechanicRadius * (map.sizeFactor / 100) / 2048 * width;
      ctx.fillStyle = "#ff303028";
      ctx.strokeStyle = "#ff3030e8";
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(point.x, point.y, Math.max(3, radius), 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
      ctx.fillStyle = "#ff3030";
      ctx.strokeStyle = "#4a0000";
      ctx.lineWidth = 2;
      ctx.beginPath();
      ctx.arc(point.x, point.y, 5, 0, Math.PI * 2);
      ctx.fill();
      ctx.stroke();
    } else if (marker.kind === "Monster") {
      const label = `${marker.level ?? "?"} ${marker.name || "怪物"}`;
      const fontSize = Math.max(9, Math.min(13, width / 75));
      ctx.font = `600 ${fontSize}px "Microsoft YaHei", sans-serif`;
      ctx.textAlign = "center";
      ctx.textBaseline = "middle";
      ctx.lineWidth = 3;
      ctx.strokeStyle = "#071009dd";
      ctx.fillStyle = "#e8f2d0";
      ctx.strokeText(label, point.x, point.y);
      ctx.fillText(label, point.x, point.y);
      point.hitRadius = Math.max(12, ctx.measureText(label).width / 2);
    } else {
      const icon = loadedIcons.get(iconIdForKind(marker.kind));
      if (icon) {
        const renderedIconSize = marker.kind === "FortuneCarrot"
          ? iconSize * .72
          : iconSize;
        ctx.drawImage(
          icon,
          point.x - renderedIconSize / 2,
          point.y - renderedIconSize / 2,
          renderedIconSize,
          renderedIconSize
        );
      } else {
        ctx.fillStyle = colorFor(marker.kind);
        ctx.strokeStyle = "#10151f";
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.arc(point.x, point.y, 5, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
      }
    }
    return { ...point, marker };
  });
  for (const cluster of clusterMonsterMarkers(
    drawableMarkers.filter(marker => marker.kind === "Monster")
  )) {
    const labels = [...new Set(cluster.members.map(marker => {
      const name = marker.name || "怪物";
      return marker.level ? `${marker.level} ${name}` : name;
    }))].sort((left, right) => left.localeCompare(right, "zh-CN"));
    const point = markerToPixel(cluster.center, map, width, height);
    const fontSize = Math.max(8, Math.min(12, width / 80));
    const lineHeight = fontSize + 2;
    ctx.font = `600 ${fontSize}px "Microsoft YaHei", sans-serif`;
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#071009dd";
    ctx.fillStyle = "#e8f2d0";
    const columnCount = Math.ceil(labels.length / monsterLabelsPerColumn);
    const columnGap = Math.max(10, fontSize);
    const labelWidths = labels.map(label => ctx.measureText(label).width);
    const columnWidths = Array.from({ length: columnCount }, (_, column) =>
      Math.max(...labelWidths.slice(
        column * monsterLabelsPerColumn,
        (column + 1) * monsterLabelsPerColumn
      ))
    );
    const totalWidth = columnWidths.reduce((sum, value) => sum + value, 0)
      + columnGap * (columnCount - 1);
    let columnLeft = point.x - totalWidth / 2;
    labels.forEach((label, index) => {
      const column = Math.floor(index / monsterLabelsPerColumn);
      const row = index % monsterLabelsPerColumn;
      const rowsInColumn = Math.min(
        monsterLabelsPerColumn,
        labels.length - column * monsterLabelsPerColumn
      );
      const x = columnLeft + columnWidths[column] / 2;
      const y = point.y + (row - (rowsInColumn - 1) / 2) * lineHeight;
      ctx.strokeText(label, x, y);
      ctx.fillText(label, x, y);
      if (row === rowsInColumn - 1) {
        columnLeft += columnWidths[column] + columnGap;
      }
    });
    state.points.push({
      ...point,
      marker: cluster.members[0],
      monsterCluster: cluster.members,
      hitRadius: Math.max(
        12,
        totalWidth / 2,
        Math.min(labels.length, monsterLabelsPerColumn) * lineHeight / 2
      ),
    });
  }
  ctx.strokeStyle = "#12182688";
  ctx.lineWidth = 1;
  ctx.strokeRect(.5, .5, width - 1, height - 1);
}

$("plot").addEventListener("mousemove", event => {
  const rect = event.currentTarget.getBoundingClientRect();
  const x = event.clientX - rect.left, y = event.clientY - rect.top;
  const hit = state.points.find(point =>
    Math.hypot(point.x - x, point.y - y) < (point.hitRadius ?? 10)
  );
  const tip = $("tooltip");
  if (!hit) { tip.hidden = true; return; }
  const marker = hit.marker;
  if (hit.monsterCluster) {
    const monsters = [...new Set(hit.monsterCluster.map(item => {
      const levelText = item.level ? `${item.level} ` : "";
      return `${levelText}${item.name || "怪物"}`;
    }))].sort((left, right) => left.localeCompare(right, "zh-CN"));
    const provisionalText = hit.monsterCluster.some(item => item.reportCount < 2)
      ? "<br>含单来源暂定统计"
      : "";
    const clusterCenter = hit.monsterCluster.reduce((sum, item) => ({
      x: sum.x + item.x / hit.monsterCluster.length,
      y: sum.y + item.y / hit.monsterCluster.length,
      z: sum.z + item.z / hit.monsterCluster.length,
    }), { x: 0, y: 0, z: 0 });
    tip.innerHTML = `<strong>怪物生成区域</strong><br>${monsters.map(esc).join("<br>")}${provisionalText}<br>
      T${marker.territoryId} / M${marker.mapId}<br>中心 X ${clusterCenter.x.toFixed(2)} · Y ${clusterCenter.y.toFixed(2)} · Z ${clusterCenter.z.toFixed(2)}`;
    tip.style.left = `${Math.min(x + 18, rect.width - 290)}px`;
    tip.style.top = `${Math.max(8, y - 22)}px`;
    tip.hidden = false;
    return;
  }
  const level = marker.level ? ` · 等级 ${marker.level}` : "";
  const provisional = marker.kind === "Monster" && marker.reportCount < 2
    ? " · 单来源暂定"
    : "";
  tip.innerHTML = `<strong>${esc(marker.kind)}${level}${provisional}</strong><br>${esc(marker.name || "")}<br>
    T${marker.territoryId} / M${marker.mapId}<br>X ${marker.x.toFixed(2)} · Y ${marker.y.toFixed(2)} · Z ${marker.z.toFixed(2)}`;
  tip.style.left = `${Math.min(x + 18, rect.width - 290)}px`;
  tip.style.top = `${Math.max(8, y - 22)}px`;
  tip.hidden = false;
});
$("plot").addEventListener("mouseleave", () => $("tooltip").hidden = true);
$("mapSelect").addEventListener("change", refresh);
$("refresh").addEventListener("click", refresh);
$("kinds").addEventListener("click", event => {
  const button = event.target.closest("[data-kind]");
  if (!button) return;
  const kind = button.dataset.kind;
  if (state.hiddenKinds.has(kind)) state.hiddenKinds.delete(kind);
  else state.hiddenKinds.add(kind);
  renderKinds(
    [...new Set(state.markers.map(marker => marker.kind))]
      .map(kindName => ({
        kind: kindName,
        count: state.markers.filter(marker => marker.kind === kindName).length,
      }))
  );
  renderRows();
  drawMap();
});
window.addEventListener("resize", () => drawMap());
refresh();
setInterval(() => {
  if (document.visibilityState === "visible") refresh();
}, 5000);
