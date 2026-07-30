using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BOCCHI.Modules.DevMap;
using Dalamud.Bindings.ImGui;
using ECommons.DalamudServices;
using Ocelot.Modules;
using Ocelot.Windows;

namespace BOCCHI.Modules.Telemetry;

[OcelotModule(950)]
public sealed class TelemetryModule : Module
{
    private const int CurrentConsentVersion = 2;
    private const string Endpoint =
        "https://h.lionwebsite.xyz/bocchi-telemetry/api/v1/markers";
    private const string SharedMapEndpoint =
        "https://h.lionwebsite.xyz/bocchi-telemetry/api/v1/markers?limit=10000";
    private static readonly TimeSpan UploadInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DownloadInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly CancellationTokenSource disposeToken = new();
    private DateTime nextUploadAt = DateTime.MinValue;
    private DateTime nextDownloadAt = DateTime.MinValue;
    private Task? uploadTask;
    private Task? downloadTask;
    private IReadOnlyList<TelemetryMarker> sharedMarkers = Array.Empty<TelemetryMarker>();
    private string? lastUploadedFingerprint;
    private string status = "尚未上传";
    private string sharedStatus = "尚未同步";
    private bool consentPopupRequested;

    public override TelemetryConfig Config
    {
        get => PluginConfig.TelemetryConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => true;
    }

    public TelemetryModule(Plugin plugin, Config config)
        : base(plugin, config)
    {
        Svc.PluginInterface.UiBuilder.Draw += DrawTelemetry;
    }

    public void SetEnabled(bool enabled, bool announce = true)
    {
        Config.ConsentVersion = CurrentConsentVersion;
        Config.Enabled = enabled;
        PluginConfig.Save();
        nextUploadAt = DateTime.MinValue;
        if (!enabled)
        {
            lastUploadedFingerprint = null;
            status = "已关闭，不会上传";
        }

        if (announce)
        {
            Svc.Chat.Print(
                enabled
                    ? "[BOCCHI] 匿名地图遥测已开启。不会上传角色名、CID、服务器或玩家坐标。"
                    : "[BOCCHI] 匿名地图遥测已关闭。"
            );
        }
    }

    public string GetStatus()
    {
        if (Config.ConsentVersion < CurrentConsentVersion)
        {
            return "尚未选择";
        }

        return Config.Enabled ? $"已开启；{status}" : "已关闭";
    }

    public IReadOnlyList<TelemetryMarker> GetSharedMarkersSnapshot()
    {
        return sharedMarkers;
    }

    public override bool RenderMainUi(RenderContext context)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("匿名地图遥测");
        var enabled = Config.Enabled;
        if (ImGui.Checkbox("上传已记录的游戏内容坐标##BOCCHI_TelemetryEnabled", ref enabled))
        {
            SetEnabled(enabled);
        }

        ImGui.TextWrapped(
            "仅上传地图标记、事件/对象 ID、静止未交战怪物的名称/等级和坐标；"
            + "不上传角色或账号信息。"
        );
        ImGui.TextDisabled(GetStatus());
        var showSharedMarkers = Config.ShowSharedMarkers;
        if (ImGui.Checkbox(
                "显示社区共享地图标记##BOCCHI_ShowSharedTelemetryMarkers",
                ref showSharedMarkers
            ))
        {
            Config.ShowSharedMarkers = showSharedMarkers;
            PluginConfig.Save();
            nextDownloadAt = DateTime.MinValue;
        }

        ImGui.TextDisabled(Config.ShowSharedMarkers ? sharedStatus : "社区共享标记已隐藏");
        return true;
    }

    private void DrawTelemetry()
    {
        DrawConsentPopup();

        if (Config.ShowSharedMarkers
            && DateTime.UtcNow >= nextDownloadAt
            && downloadTask is not { IsCompleted: false })
        {
            nextDownloadAt = DateTime.UtcNow + DownloadInterval;
            downloadTask = DownloadSharedMapAsync(disposeToken.Token);
        }

        if (!Config.Enabled
            || Config.ConsentVersion < CurrentConsentVersion
            || DateTime.UtcNow < nextUploadAt
            || uploadTask is { IsCompleted: false })
        {
            return;
        }

        nextUploadAt = DateTime.UtcNow + UploadInterval;
        uploadTask = UploadSnapshotAsync(disposeToken.Token);
    }

    private async Task DownloadSharedMapAsync(CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{SharedMapEndpoint}&_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation(
                "Cache-Control",
                "no-cache, no-store, max-age=0"
            );
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            if (!response.IsSuccessStatusCode)
            {
                sharedStatus = $"社区地图同步失败：HTTP {(int)response.StatusCode}";
                return;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var payload = await JsonSerializer.DeserializeAsync<TelemetryMarkerResponse>(
                stream,
                JsonOptions,
                cancellationToken
            );
            sharedMarkers = (payload?.Markers ?? [])
                .Where(IsFinite)
                .ToArray();
            sharedStatus = $"社区地图：{sharedMarkers.Count} 个聚合点（5 秒同步）";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            sharedStatus = "社区地图同步失败，稍后重试";
            DebugLog.Warning(exception, "BOCCHI shared telemetry download failed");
        }
    }

    private void DrawConsentPopup()
    {
        if (Config.ConsentVersion >= CurrentConsentVersion)
        {
            return;
        }

        if (!consentPopupRequested)
        {
            ImGui.OpenPopup("BOCCHI 匿名地图遥测###BOCCHI_TelemetryConsent");
            consentPopupRequested = true;
        }

        var open = true;
        if (!ImGui.BeginPopupModal(
                "BOCCHI 匿名地图遥测###BOCCHI_TelemetryConsent",
                ref open,
                ImGuiWindowFlags.AlwaysAutoResize
            ))
        {
            return;
        }

        ImGui.TextWrapped(
            "是否帮助收集蜃景幻界地图资料？开启后，BOCCHI 会自动上传已记录的宝箱、胡萝卜、"
            + "FATE、CE、调查地点、静止未交战怪物的名称/等级，"
            + "以及 Tower EventObj 的游戏内容坐标。"
        );
        ImGui.Spacing();
        ImGui.TextWrapped(
            "不会上传角色名、Content ID、账号、服务器、聊天内容或玩家实时位置。"
        );
        ImGui.TextWrapped("数据将作为公开的聚合地图资料展示，可随时用 /bocchi telemetry off 关闭。");
        ImGui.Spacing();

        if (ImGui.Button("同意并开启##BOCCHI_TelemetryAccept"))
        {
            SetEnabled(true);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("不同意##BOCCHI_TelemetryDecline"))
        {
            SetEnabled(false, false);
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    private async Task UploadSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!Plugin.Modules.TryGetModule<DevMapModule>(out var devMap) || devMap == null)
            {
                status = "地图模块尚未就绪";
                return;
            }

            var batch = BuildBatch(devMap);
            if (batch.Markers.Count == 0)
            {
                status = "暂无可上传标记";
                return;
            }

            var json = JsonSerializer.Serialize(batch, JsonOptions);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            if (fingerprint == lastUploadedFingerprint)
            {
                status = "数据未变化";
                return;
            }

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await httpClient.PostAsync(Endpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                status = $"服务器返回 {(int)response.StatusCode}";
                DebugLog.Warning(
                    "BOCCHI telemetry upload failed with HTTP {StatusCode}",
                    (int)response.StatusCode
                );
                return;
            }

            lastUploadedFingerprint = fingerprint;
            status = $"最近成功上传 {batch.Markers.Count} 条";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            status = "上传失败，稍后重试";
            DebugLog.Warning(exception, "BOCCHI telemetry upload failed");
        }
    }

    private TelemetryBatch BuildBatch(DevMapModule devMap)
    {
        var batch = new TelemetryBatch
        {
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
        };

        batch.Markers.AddRange(
            devMap.GetTelemetryMarkersSnapshot()
                .Where(IsFinite)
                .Where(marker =>
                    marker.Type != DevMarkerType.Monster || marker.Level != 1
                )
                .Select(marker => new TelemetryMarker
                {
                    Source = marker.Type == DevMarkerType.Monster ? "monster" : "dev-map",
                    Kind = marker.Type.ToString(),
                    TerritoryId = marker.TerritoryId,
                    MapId = marker.MapId,
                    BaseId = marker.BaseId == 0 ? null : marker.BaseId,
                    EventId = marker.EventId == 0 ? null : marker.EventId,
                    Level = marker.Level == 0 ? null : marker.Level,
                    Name = EmptyToNull(marker.Name),
                    X = marker.X,
                    Y = marker.Y,
                    Z = marker.Z,
                })
        );

        if (Config.IncludeTowerObjects)
        {
            batch.Markers.AddRange(
                devMap.GetTelemetryTowerObjectsSnapshot()
                    .Where(IsFinite)
                    .Select(record => new TelemetryMarker
                    {
                        Source = "tower-eventobj",
                        Kind = record.Type.ToString(),
                        TerritoryId = record.TerritoryId,
                        MapId = record.MapId,
                        BaseId = record.BaseId == 0 ? null : record.BaseId,
                        Name = EmptyToNull(record.Name),
                        X = record.X,
                        Y = record.Y,
                        Z = record.Z,
                        HitboxRadius = float.IsFinite(record.HitboxRadius)
                            ? record.HitboxRadius
                            : null,
                        MechanicRadius = record.MechanicRadius is { } radius
                                          && float.IsFinite(radius)
                            ? radius
                            : null,
                    })
            );
        }

        batch.Markers = batch.Markers
            .OrderBy(marker => marker.TerritoryId)
            .ThenBy(marker => marker.MapId)
            .ThenBy(marker => marker.Source, StringComparer.Ordinal)
            .ThenBy(marker => marker.Kind, StringComparer.Ordinal)
            .ThenBy(marker => marker.BaseId)
            .ThenBy(marker => marker.EventId)
            .ThenBy(marker => marker.X)
            .ThenBy(marker => marker.Y)
            .ThenBy(marker => marker.Z)
            .ToList();
        return batch;
    }

    private static bool IsFinite(DevMapMarker marker)
    {
        return marker.TerritoryId > 0
               && marker.MapId > 0
               && float.IsFinite(marker.X)
               && float.IsFinite(marker.Y)
               && float.IsFinite(marker.Z);
    }

    private static bool IsFinite(ForkedTowerEventObjRecord record)
    {
        return record.TerritoryId > 0
               && record.MapId > 0
               && float.IsFinite(record.X)
               && float.IsFinite(record.Y)
               && float.IsFinite(record.Z);
    }

    private static bool IsFinite(TelemetryMarker marker)
    {
        return marker.TerritoryId > 0
               && marker.MapId > 0
               && float.IsFinite(marker.X)
               && float.IsFinite(marker.Y)
               && float.IsFinite(marker.Z);
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public override void Dispose()
    {
        Svc.PluginInterface.UiBuilder.Draw -= DrawTelemetry;
        disposeToken.Cancel();
        disposeToken.Dispose();
        httpClient.Dispose();
        base.Dispose();
    }
}
