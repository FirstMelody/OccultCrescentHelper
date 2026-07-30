const state = {
  markers: [],
  trapCandidates: [],
  trapGroups: [],
  colors: new Map(),
  points: [],
  maps: [],
  selectedMap: null,
  mapImages: new Map(),
  mapSamplers: new Map(),
  markerIcons: new Map(),
  hiddenKinds: new Set(),
  showTrapRanges: true,
  viewport: {
    zoom: 1,
    panX: 0,
    panY: 0,
    dragging: false,
    pointerId: null,
    lastX: 0,
    lastY: 0,
  },
};
const palette = ["#ffcc66", "#f1787d", "#72d6c9", "#87a9ff", "#c892ff", "#ff9f5a", "#88d66c", "#e5e7eb"];
const kindIconIds = {
  BronzeChest: 60356,
  SilverChest: 60355,
  PotChest: 60354,
  RerollChest: 61473,
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
    const trapClass = kind === "SmallTrap" ? " small-trap" : " big-trap";
    return `<span class="trap-icon${trapClass}" aria-hidden="true"></span>`;
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

function normalizeTrapCandidate(raw) {
  return {
    source: "tower-candidate",
    kind: raw.kind,
    territoryId: raw.territoryId,
    mapId: raw.mapId,
    baseId: raw.baseId ?? null,
    eventId: null,
    level: null,
    name: raw.name ?? "",
    x: raw.x,
    y: raw.y,
    z: raw.z,
    hitboxRadius: null,
    mechanicRadius: raw.mechanicRadius,
    reportCount: 0,
    candidateStatus: raw.status ?? "inferred",
    candidateGroup: raw.group ?? "",
    candidateNote: raw.note ?? "",
  };
}

function normalizeTrapGroup(raw) {
  return {
    id: raw.id,
    name: raw.name ?? raw.id,
    territoryId: raw.territoryId,
    mapId: raw.mapId,
    mapIds: raw.mapIds ?? [raw.mapId],
    kind: raw.kind,
    kinds: raw.kinds ?? (raw.kind ? [raw.kind] : []),
    maxActive: Math.max(1, raw.maxActive ?? 1),
    crossMap: raw.crossMap ?? false,
    showCandidates: raw.showCandidates ?? true,
    displayMode: raw.displayMode ?? "",
    ruleText: raw.ruleText ?? "",
    positions: raw.positions ?? [],
  };
}

async function loadCatalog() {
  const [catalog, candidates] = await Promise.all([
    getJson("./maps/catalog.json"),
    getJson("./maps/tower-trap-candidates.json"),
  ]);
  state.maps = (catalog.maps ?? []).map(normalizeMap);
  state.trapCandidates = (candidates.candidates ?? []).map(normalizeTrapCandidate);
  state.trapGroups = (candidates.groups ?? []).map(normalizeTrapGroup);
  const picker = $("mapSelect");
  picker.innerHTML = state.maps.map(map =>
    `<option value="${map.territoryId}:${map.mapId}">${esc(map.contentName)} · ${esc(map.placeName)}</option>`
  ).join("");
  const north = state.maps.find(map => map.territoryId === 1346) ?? state.maps[0];
  if (north) picker.value = `${north.territoryId}:${north.mapId}`;
  selectCurrentMap();
}

function trapCandidatesForMap(map) {
  const explicitCandidates = state.trapCandidates.filter(candidate =>
    candidate.territoryId === map.territoryId
    && candidate.mapId === map.mapId
  );
  const groupCandidates = state.trapGroups.flatMap(group =>
    group.showCandidates && group.territoryId === map.territoryId
      ? group.positions
        .filter(position => (position.mapId ?? group.mapId) === map.mapId)
        .flatMap(position => (position.kinds ?? group.kinds).map(kind =>
          normalizeTrapCandidate({
            territoryId: group.territoryId,
            mapId: map.mapId,
            kind,
            baseId: kind === "BigTrap" ? 2014585 : 2014584,
            x: position.x,
            y: position.y,
            z: position.z,
            mechanicRadius: kind === "BigTrap" ? 30 : 7,
            status: "inferred",
            group: group.id,
            note: `${group.name}候选点`,
          })
        ))
      : []
  );
  const candidates = [];
  for (const candidate of [...explicitCandidates, ...groupCandidates]) {
    if (!candidates.some(existing =>
      existing.kind === candidate.kind
      && existing.mapId === candidate.mapId
      && Math.hypot(
        existing.x - candidate.x,
        existing.y - candidate.y,
        existing.z - candidate.z
      ) <= .1
    )) {
      candidates.push(candidate);
    }
  }
  return candidates.filter(candidate =>
    !state.markers.some(marker =>
      marker.kind === candidate.kind
      && marker.territoryId === candidate.territoryId
      && marker.mapId === candidate.mapId
      && Math.hypot(
        marker.x - candidate.x,
        marker.y - candidate.y,
        marker.z - candidate.z
      ) <= .75
    )
  );
}

function trapGroupForMarker(marker) {
  if (!isTrapKind(marker.kind)) return null;
  return state.trapGroups.find(group =>
    group.territoryId === marker.territoryId
    && group.mapIds.includes(marker.mapId)
    && group.kinds.includes(marker.kind)
    && group.positions.some(position =>
      (position.mapId ?? group.mapId) === marker.mapId
      && Math.hypot(
          marker.x - position.x,
          marker.y - position.y,
          marker.z - position.z
        ) <= .75
    )
  ) ?? null;
}

function collapseTrapVariantMarkers(markers) {
  const collapsed = [];
  for (const marker of markers) {
    if (marker.trapGroup?.displayMode !== "small-big-swap") {
      collapsed.push(marker);
      continue;
    }
    const existing = collapsed.find(item =>
      item.trapGroup?.id === marker.trapGroup.id
      && Math.hypot(
        item.x - marker.x,
        item.y - marker.y,
        item.z - marker.z
      ) <= .1
    );
    if (existing) {
      if (!existing.trapVariantKinds.includes(marker.kind)) {
        existing.trapVariantKinds.push(marker.kind);
      }
    } else {
      collapsed.push({ ...marker, trapVariantKinds: [marker.kind] });
    }
  }
  return collapsed;
}

function selectCurrentMap() {
  const [territoryId, mapId] = $("mapSelect").value.split(":").map(Number);
  const previousKey = state.selectedMap
    ? `${state.selectedMap.territoryId}:${state.selectedMap.mapId}`
    : null;
  state.selectedMap = state.maps.find(map => map.territoryId === territoryId && map.mapId === mapId) ?? null;
  const nextKey = state.selectedMap
    ? `${state.selectedMap.territoryId}:${state.selectedMap.mapId}`
    : null;
  if (previousKey !== null && previousKey !== nextKey) resetViewport();
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
  // Map offsets are world-space origins, so they are scaled together with XYZ.
  // The old (scale - 1) form happened to work on the offset-free outdoor maps,
  // but shifted every SizeFactor=200 Tower marker by one full map offset.
  const mapX = (marker.x + map.offsetX) * scale;
  const mapY = (marker.z + map.offsetY) * scale;
  return {
    x: (mapX + 1024) / 2048 * width,
    y: (mapY + 1024) / 2048 * height,
  };
}

function viewportPoint(point, width, height) {
  const { zoom, panX, panY } = state.viewport;
  return {
    x: width / 2 + (point.x - width / 2) * zoom + panX,
    y: height / 2 + (point.y - height / 2) * zoom + panY,
  };
}

function clampViewport(width, height) {
  const maxPanX = width * (state.viewport.zoom - 1) / 2;
  const maxPanY = height * (state.viewport.zoom - 1) / 2;
  state.viewport.panX = Math.max(-maxPanX, Math.min(maxPanX, state.viewport.panX));
  state.viewport.panY = Math.max(-maxPanY, Math.min(maxPanY, state.viewport.panY));
}

function updateZoomLabel() {
  const label = $("zoomLevel");
  if (label) label.textContent = `${Math.round(state.viewport.zoom * 100)}%`;
}

function resetViewport() {
  state.viewport.zoom = 1;
  state.viewport.panX = 0;
  state.viewport.panY = 0;
  updateZoomLabel();
}

function zoomAt(x, y, nextZoom) {
  const canvas = $("plot");
  const rect = canvas.getBoundingClientRect();
  const oldZoom = state.viewport.zoom;
  const zoom = Math.max(1, Math.min(5, nextZoom));
  if (Math.abs(zoom - oldZoom) < .001) return;
  const relativeX = x - rect.width / 2;
  const relativeY = y - rect.height / 2;
  state.viewport.panX = relativeX
    - (relativeX - state.viewport.panX) * zoom / oldZoom;
  state.viewport.panY = relativeY
    - (relativeY - state.viewport.panY) * zoom / oldZoom;
  state.viewport.zoom = zoom;
  clampViewport(rect.width, rect.height);
  updateZoomLabel();
  drawMap();
}

function mapSamplerFor(map, image) {
  if (state.mapSamplers.has(map.image)) return state.mapSamplers.get(map.image);
  const canvas = document.createElement("canvas");
  canvas.width = image.naturalWidth || image.width;
  canvas.height = image.naturalHeight || image.height;
  const context = canvas.getContext("2d", { willReadFrequently: true });
  context.drawImage(image, 0, 0);
  const sampler = { canvas, context };
  state.mapSamplers.set(map.image, sampler);
  return sampler;
}

function isLikelyOnTowerPath(marker, map, image) {
  if (!isTrapKind(marker.kind) || map.sizeFactor <= 100) return true;
  const sampler = mapSamplerFor(map, image);
  const point = markerToPixel(marker, map, sampler.canvas.width, sampler.canvas.height);
  const sampleRadius = 12;
  const left = Math.max(0, Math.floor(point.x - sampleRadius));
  const top = Math.max(0, Math.floor(point.y - sampleRadius));
  const right = Math.min(sampler.canvas.width, Math.ceil(point.x + sampleRadius));
  const bottom = Math.min(sampler.canvas.height, Math.ceil(point.y + sampleRadius));
  if (right <= left || bottom <= top) return false;
  const pixels = sampler.context.getImageData(left, top, right - left, bottom - top).data;
  for (let index = 0; index < pixels.length; index += 4) {
    // Tower walkable paths are the pale overlay (high red and blue channels).
    // Nearby-floor EventObjs remain in the object table but land on parchment.
    if (pixels[index] >= 215 && pixels[index + 2] >= 145) return true;
  }
  return false;
}

const monsterDisplayClusterRadius = 230;
const monsterCollisionOnlyZoom = 1.5;
const monsterLabelCollisionGap = 3;
const monsterLabelsPerCluster = 4;
const monsterLabelsPerColumn = 4;

function monsterLabel(marker) {
  const name = marker.name || "怪物";
  return marker.level ? `${marker.level} ${name}` : name;
}

function monsterLabelBounds(marker, map, width, height, ctx, fontSize) {
  const point = viewportPoint(
    markerToPixel(marker, map, width, height),
    width,
    height
  );
  const textWidth = ctx.measureText(monsterLabel(marker)).width;
  const lineHeight = fontSize + 2;
  const originX = point.x + 4;
  const originY = point.y + 4;
  return {
    minX: originX - monsterLabelCollisionGap,
    minY: originY - monsterLabelCollisionGap,
    maxX: originX + textWidth + monsterLabelCollisionGap,
    maxY: originY + lineHeight + monsterLabelCollisionGap,
  };
}

function boundsOverlap(left, right) {
  return left.minX <= right.maxX
    && left.maxX >= right.minX
    && left.minY <= right.maxY
    && left.maxY >= right.minY;
}

function clusterMonsterMarkers(markers, map, width, height, ctx, fontSize) {
  const centerOf = members => ({
    x: members.reduce((sum, marker) => sum + marker.x, 0) / members.length,
    y: members.reduce((sum, marker) => sum + marker.y, 0) / members.length,
    z: members.reduce((sum, marker) => sum + marker.z, 0) / members.length,
  });
  const collisionOnly = state.viewport.zoom >= monsterCollisionOnlyZoom;
  const bounds = collisionOnly
    ? new Map(markers.map(marker => [
        marker,
        monsterLabelBounds(marker, map, width, height, ctx, fontSize),
      ]))
    : null;
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
        members.map(monsterLabel)
      );
      if (distinctLabels.size > monsterLabelsPerCluster) continue;
      let distance;
      if (collisionOnly) {
        if (!cluster.members.some(member =>
          boundsOverlap(bounds.get(member), bounds.get(marker))
        )) continue;
        const clusterPoint = viewportPoint(
          markerToPixel(cluster.center, map, width, height),
          width,
          height
        );
        const markerPoint = viewportPoint(
          markerToPixel(marker, map, width, height),
          width,
          height
        );
        distance = Math.hypot(
          markerPoint.x - clusterPoint.x,
          markerPoint.y - clusterPoint.y
        );
      } else {
        if (members.some(member =>
          Math.hypot(member.x - center.x, member.z - center.z) > monsterDisplayClusterRadius
        )) continue;
        distance = Math.hypot(marker.x - cluster.center.x, marker.z - cluster.center.z);
      }
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
  clampViewport(width, height);

  const image = await loadMapImage(map);
  const imageOrigin = viewportPoint({ x: 0, y: 0 }, width, height);
  ctx.drawImage(
    image,
    imageOrigin.x,
    imageOrigin.y,
    width * state.viewport.zoom,
    height * state.viewport.zoom
  );
  ctx.fillStyle = "#07101a12";
  ctx.fillRect(0, 0, width, height);

  const drawableMarkers = collapseTrapVariantMarkers(
    [...state.markers, ...trapCandidatesForMap(map)]
      .map(marker => ({ ...marker, trapGroup: trapGroupForMarker(marker) }))
      .filter(marker =>
        isDrawableMarker(marker)
        && !(marker.kind === "Monster" && marker.level === 1)
        && !state.hiddenKinds.has(marker.kind)
        && isLikelyOnTowerPath(marker, map, image)
      )
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
    const point = viewportPoint(markerToPixel(marker, map, width, height), width, height);
    if (isTrapKind(marker.kind)) {
      const trapKinds = marker.trapVariantKinds ?? [marker.kind];
      const hasSmallTrap = trapKinds.includes("SmallTrap");
      const hasBigTrap = trapKinds.includes("BigTrap");
      if (state.showTrapRanges) {
        for (const kind of ["BigTrap", "SmallTrap"].filter(kind => trapKinds.includes(kind))) {
          const mechanicRadius = kind === "BigTrap" ? 30 : 7;
          const radius = mechanicRadius * (map.sizeFactor / 100) / 2048
            * width * state.viewport.zoom;
          ctx.fillStyle = kind === "SmallTrap" ? "#ffd43b20" : "#ff303028";
          ctx.strokeStyle = kind === "SmallTrap" ? "#ffc400e8" : "#ff3030e8";
          ctx.lineWidth = 2;
          ctx.beginPath();
          ctx.arc(point.x, point.y, Math.max(3, radius), 0, Math.PI * 2);
          ctx.fill();
          ctx.stroke();
        }
      }
      ctx.lineWidth = 2;
      if (hasSmallTrap && hasBigTrap) {
        ctx.fillStyle = "#ffd43b";
        ctx.beginPath();
        ctx.arc(point.x, point.y, 5, Math.PI / 2, Math.PI * 1.5);
        ctx.lineTo(point.x, point.y);
        ctx.closePath();
        ctx.fill();
        ctx.fillStyle = "#ff3030";
        ctx.beginPath();
        ctx.arc(point.x, point.y, 5, -Math.PI / 2, Math.PI / 2);
        ctx.lineTo(point.x, point.y);
        ctx.closePath();
        ctx.fill();
        ctx.strokeStyle = "#4a0000";
        ctx.beginPath();
        ctx.arc(point.x, point.y, 5, 0, Math.PI * 2);
        ctx.stroke();
      } else {
        ctx.fillStyle = hasSmallTrap ? "#ffd43b" : "#ff3030";
        ctx.strokeStyle = hasSmallTrap ? "#5c4300" : "#4a0000";
        ctx.beginPath();
        ctx.arc(point.x, point.y, 5, 0, Math.PI * 2);
        ctx.fill();
        ctx.stroke();
      }
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
  const monsterFontSize = Math.max(8, Math.min(12, width / 80));
  ctx.font = `600 ${monsterFontSize}px "Microsoft YaHei", sans-serif`;
  for (const cluster of clusterMonsterMarkers(
    drawableMarkers.filter(marker => marker.kind === "Monster"),
    map,
    width,
    height,
    ctx,
    monsterFontSize
  )) {
    const labels = [...new Set(cluster.members.map(monsterLabel))]
      .sort((left, right) => left.localeCompare(right, "zh-CN"));
    const point = viewportPoint(
      markerToPixel(cluster.center, map, width, height),
      width,
      height
    );
    const lineHeight = monsterFontSize + 2;
    ctx.font = `600 ${monsterFontSize}px "Microsoft YaHei", sans-serif`;
    ctx.textAlign = "left";
    ctx.textBaseline = "top";
    ctx.lineWidth = 3;
    ctx.strokeStyle = "#071009dd";
    ctx.fillStyle = "#e8f2d0";
    const columnCount = Math.ceil(labels.length / monsterLabelsPerColumn);
    const columnGap = Math.max(10, monsterFontSize);
    const labelWidths = labels.map(label => ctx.measureText(label).width);
    const columnWidths = Array.from({ length: columnCount }, (_, column) =>
      Math.max(...labelWidths.slice(
        column * monsterLabelsPerColumn,
        (column + 1) * monsterLabelsPerColumn
      ))
    );
    const totalWidth = columnWidths.reduce((sum, value) => sum + value, 0)
      + columnGap * (columnCount - 1);
    const totalHeight = Math.min(labels.length, monsterLabelsPerColumn) * lineHeight;
    const blockOrigin = { x: point.x + 4, y: point.y + 4 };
    let columnLeft = blockOrigin.x;
    labels.forEach((label, index) => {
      const column = Math.floor(index / monsterLabelsPerColumn);
      const row = index % monsterLabelsPerColumn;
      const rowsInColumn = Math.min(
        monsterLabelsPerColumn,
        labels.length - column * monsterLabelsPerColumn
      );
      const x = columnLeft;
      const y = blockOrigin.y + row * lineHeight;
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
      hitBounds: {
        minX: blockOrigin.x - 3,
        minY: blockOrigin.y - 2,
        maxX: blockOrigin.x + totalWidth + 3,
        maxY: blockOrigin.y + totalHeight + 2,
      },
    });
  }
  ctx.strokeStyle = "#12182688";
  ctx.lineWidth = 1;
  ctx.strokeRect(.5, .5, width - 1, height - 1);
}

$("plot").addEventListener("mousemove", event => {
  const rect = event.currentTarget.getBoundingClientRect();
  const x = event.clientX - rect.left, y = event.clientY - rect.top;
  const hit = state.points.find(point => point.hitBounds
    ? x >= point.hitBounds.minX
      && x <= point.hitBounds.maxX
      && y >= point.hitBounds.minY
      && y <= point.hitBounds.maxY
    : Math.hypot(point.x - x, point.y - y) < (point.hitRadius ?? 10)
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
  const displayKind = marker.trapVariantKinds?.length > 1
    ? "SmallTrap / BigTrap"
    : marker.kind;
  const level = marker.level ? ` · 等级 ${marker.level}` : "";
  const candidateLabel = marker.candidateStatus === "inferred" ? " · 推算候选" : "";
  const candidateDetails = marker.candidateStatus === "inferred"
    ? `<br>${esc(marker.candidateNote || "由同组点位间距推算，尚无实测刷出记录")}`
    : "";
  const trapGroupDetails = marker.trapGroup
    ? `<br>${esc(marker.trapGroup.name)} · ${esc(
        marker.trapGroup.ruleText
        || `每次有且仅有 ${marker.trapGroup.maxActive} 个生成`
      )}`
    : "";
  const provisional = marker.kind === "Monster" && marker.reportCount < 2
    ? " · 单来源暂定"
    : "";
  tip.innerHTML = `<strong>${esc(displayKind)}${level}${provisional}${candidateLabel}</strong>${candidateDetails}${trapGroupDetails}<br>${esc(marker.name || "")}<br>
    T${marker.territoryId} / M${marker.mapId}<br>X ${marker.x.toFixed(2)} · Y ${marker.y.toFixed(2)} · Z ${marker.z.toFixed(2)}`;
  tip.style.left = `${Math.min(x + 18, rect.width - 290)}px`;
  tip.style.top = `${Math.max(8, y - 22)}px`;
  tip.hidden = false;
});
$("plot").addEventListener("mouseleave", () => $("tooltip").hidden = true);
$("plot").addEventListener("wheel", event => {
  event.preventDefault();
  const rect = event.currentTarget.getBoundingClientRect();
  const factor = event.deltaY < 0 ? 1.2 : 1 / 1.2;
  zoomAt(
    event.clientX - rect.left,
    event.clientY - rect.top,
    state.viewport.zoom * factor
  );
}, { passive: false });
$("plot").addEventListener("pointerdown", event => {
  if (state.viewport.zoom <= 1) return;
  state.viewport.dragging = true;
  state.viewport.pointerId = event.pointerId;
  state.viewport.lastX = event.clientX;
  state.viewport.lastY = event.clientY;
  event.currentTarget.setPointerCapture(event.pointerId);
  event.currentTarget.classList.add("is-dragging");
});
$("plot").addEventListener("pointermove", event => {
  if (!state.viewport.dragging || event.pointerId !== state.viewport.pointerId) return;
  state.viewport.panX += event.clientX - state.viewport.lastX;
  state.viewport.panY += event.clientY - state.viewport.lastY;
  state.viewport.lastX = event.clientX;
  state.viewport.lastY = event.clientY;
  const rect = event.currentTarget.getBoundingClientRect();
  clampViewport(rect.width, rect.height);
  $("tooltip").hidden = true;
  drawMap();
});
function stopViewportDrag(event) {
  if (event.pointerId !== state.viewport.pointerId) return;
  state.viewport.dragging = false;
  state.viewport.pointerId = null;
  event.currentTarget.classList.remove("is-dragging");
}
$("plot").addEventListener("pointerup", stopViewportDrag);
$("plot").addEventListener("pointercancel", stopViewportDrag);
$("zoomIn").addEventListener("click", () => {
  const rect = $("plot").getBoundingClientRect();
  zoomAt(rect.width / 2, rect.height / 2, state.viewport.zoom * 1.25);
});
$("zoomOut").addEventListener("click", () => {
  const rect = $("plot").getBoundingClientRect();
  zoomAt(rect.width / 2, rect.height / 2, state.viewport.zoom / 1.25);
});
$("zoomReset").addEventListener("click", () => {
  resetViewport();
  drawMap();
});
$("showTrapRanges").addEventListener("change", event => {
  state.showTrapRanges = event.currentTarget.checked;
  drawMap();
});
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
