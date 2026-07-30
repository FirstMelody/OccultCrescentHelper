using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using BOCCHI.Data;
using BOCCHI.Modules.CriticalEncounters;
using BOCCHI.Modules.DevMap;
using Dalamud.Interface.Colors;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Ocelot.Modules;
using Ocelot.Windows;
using Pictomancy;

namespace BOCCHI.Modules.ForkedTower;

[OcelotModule]
public class ForkedTowerModule(Plugin plugin, Config config) : Module(plugin, config)
{
    public override ForkedTowerConfig Config
    {
        get => PluginConfig.ForkedTowerConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }

    public override bool IsEnabled
    {
        get => Config.IsPropertyEnabled(nameof(Config.Enabled));
    }

    public TowerRun TowerRun { get; private set; } = new("");

    private readonly Panel panel = new();
    private bool wasInForkedTower;
    private uint activeTowerTerritoryId;

    public override void PostInitialize()
    {
        GetModule<CriticalEncountersModule>().Tracker.OnBattleState += OnCriticalEncounterBattle;

        // International Dalamud may construct plugins on an IoC worker thread.
        // ObjectTable.LocalPlayer (read by StartNewRun) is framework-thread only.
        _ = Svc.Framework.RunOnFrameworkThread(StartNewRun);
    }

    public override void Update(UpdateContext context)
    {
        if (!WorldObjectScanGuard.IsSafe())
        {
            return;
        }

        TowerRun.Update(context);
    }

    public override void Render(RenderContext context)
    {
        if (!WorldObjectScanGuard.IsSafe() || !EnsureRunLifecycle())
        {
            return;
        }

        if (Config.DrawPotentialTrapPositions)
        {
            DrawPotentialTraps(context);
        }

        TowerRun.Render(context);
    }

    private void DrawPotentialTraps(RenderContext context)
    {
        var candidates = GetModule<DevMapModule>()
            .GetTowerTrapCandidates(
                Svc.ClientState.TerritoryType,
                Svc.ClientState.MapId,
                includeAllMaps: true
            )
            .AsEnumerable();

#if DEBUG
        if (!Config.IgnoreDrawRange)
        {
            candidates = candidates.Where(candidate =>
                Player.DistanceTo(candidate.Position) <= Config.TrapDrawRange
            );
        }
#else
        candidates = candidates.Where(candidate =>
            Player.DistanceTo(candidate.Position) <= Config.TrapDrawRange
        );
#endif

        foreach (var candidate in candidates)
        {
            var key = FormattableString.Invariant(
                $"BOCCHI.PotentialTrap.{candidate.GroupKey}.{candidate.Position.X:F2}:{candidate.Position.Y:F2}:{candidate.Position.Z:F2}.{candidate.Type}"
            );
            if (candidate.IsExcluded)
            {
                // Pictomancy destroys retained VFX when their key is no longer
                // submitted. Submitting an invisible, near-zero circle keeps the
                // old Omen alive and some clients do not apply that invalid scale.
                continue;
            }

            var radius = Math.Max(0.1f, candidate.MechanicRadius);
            var color = candidate.IsObservedInCurrentRun
                ? GetObservedTrapColor(candidate.Type)
                : GetTrapColor(candidate.Type);
            if (Config.DrawSimpleMode || Config.DrawOutlineForComplexMode)
            {
                context.DrawCircle(candidate.Position, radius, color);
            }

            if (!Config.DrawSimpleMode)
            {
                PctService.VfxRenderer.AddCircle(
                    key,
                    candidate.Position,
                    radius,
                    color
                );
            }
        }
    }

    private Vector4 GetTrapColor(ForkedTowerEventObjType type)
    {
        return type switch
        {
            ForkedTowerEventObjType.SmallTrap => Config.TrapDrawColor,
            ForkedTowerEventObjType.BigTrap => Config.BigTrapDrawColor,
            _ => new Vector4(4f, 7f, 1f, 1f),
        };
    }

    private static Vector4 GetObservedTrapColor(ForkedTowerEventObjType type)
    {
        return type == ForkedTowerEventObjType.BigTrap
            ? ImGuiColors.DalamudOrange
            : ImGuiColors.DPSRed;
    }

    public bool EnsureRunLifecycle()
    {
        var isInTower = ZoneData.IsInForkedTower();
        if (!isInTower)
        {
            wasInForkedTower = false;
            activeTowerTerritoryId = 0;
            return false;
        }

        var territoryId = Svc.ClientState.TerritoryType;
        if (!wasInForkedTower || activeTowerTerritoryId != territoryId)
        {
            StartNewRun();
        }

        return true;
    }


    public override bool RenderMainUi(RenderContext context)
    {
        panel.Draw(this);
        return true;
    }

    private void OnCriticalEncounterBattle(DynamicEvent ev)
    {
        if (ev.EventType < 4)
        {
            return;
        }

        StartNewRun();
    }

    public void StartNewRun()
    {
        TowerRun = new TowerRun(GenerateHash());
        wasInForkedTower = ZoneData.IsInForkedTower();
        activeTowerTerritoryId = wasInForkedTower
            ? Svc.ClientState.TerritoryType
            : 0;
    }

    private string GenerateHash()
    {
        using var sha256 = SHA256.Create();

        var timeBytes = BitConverter.GetBytes(DateTime.UtcNow.Ticks);
        var contentIdBytes = BitConverter.GetBytes(Player.CID);

        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(timeBytes);
            Array.Reverse(contentIdBytes);
        }

        var combined = new byte[timeBytes.Length + contentIdBytes.Length];
        Buffer.BlockCopy(timeBytes, 0, combined, 0, timeBytes.Length);
        Buffer.BlockCopy(contentIdBytes, 0, combined, timeBytes.Length, contentIdBytes.Length);

        var hashBytes = sha256.ComputeHash(combined);

        return Convert.ToBase64String(hashBytes);
    }

    public override void Dispose()
    {
        GetModule<CriticalEncountersModule>().Tracker.OnBattleState -= OnCriticalEncounterBattle;
    }
}
