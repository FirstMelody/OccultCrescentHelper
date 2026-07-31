using System;
using System.Collections.Generic;
using System.Linq;
using BOCCHI.Data;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using LuminaFate = Lumina.Excel.Sheets.Fate;

namespace BOCCHI.Modules.Fates;

public sealed class PotFateTracker : IDisposable
{
    private const long RespawnSeconds = 30 * 60;

    private static readonly Dictionary<uint, uint[]> PotFateIdsByTerritory = new()
    {
        [ZoneData.SOUTHHORN] = [1976, 1977],
        [ZoneData.NORTHHORN] = [2072, 2073],
    };

    private readonly FatesModule module;
    private readonly IDtrBarEntry dtrEntry;
    private DateTime nextScanAt = DateTime.MinValue;

    public PotFateSnapshot Snapshot { get; private set; } = PotFateSnapshot.Unsupported;

    public PotFateTracker(FatesModule module)
    {
        this.module = module;
        module.Config.PotFateSpawnTimes ??= [];

        dtrEntry = Svc.DtrBar.Get("BOCCHI 魔法罐临危受命计时器");
        dtrEntry.Tooltip = "BOCCHI · 魔法罐临危受命 30 分钟计时器";
        dtrEntry.Shown = false;

        Svc.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (DateTime.UtcNow < nextScanAt)
        {
            return;
        }

        nextScanAt = DateTime.UtcNow.AddSeconds(1);
        Update();
    }

    private void Update()
    {
        if (!WorldObjectScanGuard.IsSafe())
        {
            Snapshot = PotFateSnapshot.Unsupported;
            dtrEntry.Shown = false;
            return;
        }

        var territoryId = Svc.ClientState.TerritoryType;
        if (!PotFateIdsByTerritory.TryGetValue(territoryId, out var potFateIds)
            || ZoneData.IsInForkedTower())
        {
            Snapshot = PotFateSnapshot.Unsupported;
            dtrEntry.Shown = false;
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var currentFates = Svc.Fates
            .Where(fate => potFateIds.Contains((uint)fate.FateId))
            .ToList();

        if (currentFates.Count > 0)
        {
            var active = currentFates
                .OrderByDescending(fate => fate.StartTimeEpoch)
                .First();
            var activeId = (uint)active.FateId;
            var spawnTime = active.StartTimeEpoch > 0 ? active.StartTimeEpoch : now;
            var key = GetConfigKey(territoryId, activeId);

            if (!module.Config.PotFateSpawnTimes.TryGetValue(key, out var saved)
                || saved != spawnTime)
            {
                module.Config.PotFateSpawnTimes[key] = spawnTime;
                module.PluginConfig.Save();
            }

            Snapshot = new PotFateSnapshot(
                true,
                true,
                activeId,
                GetFateName(activeId, active.Name.ToString()),
                TimeSpan.Zero,
                true
            );
            UpdateDtr();
            return;
        }

        var history = potFateIds
            .Select(id => new
            {
                Id = id,
                SpawnTime = module.Config.PotFateSpawnTimes.GetValueOrDefault(
                    GetConfigKey(territoryId, id)
                ),
            })
            .ToList();
        var lastSpawn = history.Max(item => item.SpawnTime);
        if (lastSpawn <= 0)
        {
            Snapshot = new PotFateSnapshot(
                true,
                false,
                potFateIds[0],
                GetFateName(potFateIds[0]),
                TimeSpan.Zero,
                false
            );
            UpdateDtr();
            return;
        }

        var next = history
            .OrderBy(item => item.SpawnTime)
            .ThenBy(item => item.Id)
            .First();
        var remainingSeconds = Math.Max(0, lastSpawn + RespawnSeconds - now);
        Snapshot = new PotFateSnapshot(
            true,
            false,
            next.Id,
            GetFateName(next.Id),
            TimeSpan.FromSeconds(remainingSeconds),
            true
        );
        UpdateDtr();
    }

    private void UpdateDtr()
    {
        if (!module.IsEnabled || !module.Config.ShowPotFateTimerOnDtr)
        {
            dtrEntry.Shown = false;
            return;
        }

        if (Snapshot.IsActive)
        {
            dtrEntry.Text = $"魔法罐临危受命：进行中 · {Snapshot.FateName}";
        }
        else if (!Snapshot.HasHistory)
        {
            dtrEntry.Text = "魔法罐临危受命：--:--";
        }
        else if (Snapshot.Remaining > TimeSpan.Zero)
        {
            dtrEntry.Text =
                $"魔法罐临危受命：{Snapshot.Remaining:mm\\:ss} · {Snapshot.FateName}";
        }
        else
        {
            dtrEntry.Text = $"魔法罐临危受命：可触发 · {Snapshot.FateName}";
        }

        dtrEntry.Shown = true;
    }

    private static string GetConfigKey(uint territoryId, uint fateId)
    {
        return $"{territoryId}:{fateId}";
    }

    private static string GetFateName(uint fateId, string fallback = "")
    {
        try
        {
            var name = Svc.Data
                .GetExcelSheet<LuminaFate>()
                .GetRow(fateId)
                .Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? fallback : name;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(fallback) ? $"临危受命 {fateId}" : fallback;
        }
    }

    public void Dispose()
    {
        Svc.Framework.Update -= OnFrameworkUpdate;
        dtrEntry.Remove();
    }
}

public readonly record struct PotFateSnapshot(
    bool IsSupportedTerritory,
    bool IsActive,
    uint FateId,
    string FateName,
    TimeSpan Remaining,
    bool HasHistory
)
{
    public static PotFateSnapshot Unsupported { get; } =
        new(false, false, 0, "", TimeSpan.Zero, false);
}
