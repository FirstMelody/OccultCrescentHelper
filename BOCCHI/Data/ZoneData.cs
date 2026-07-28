using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;

namespace BOCCHI.Data;

public static class ZoneData
{
    public const uint SOUTHHORN = 1252;

    private static Config? config;

    public static void Initialize(Config pluginConfig)
    {
        config = pluginConfig;
    }

    // This can and should be filled using layout files or excel data
    public readonly static Dictionary<uint, Vector3> Aetherytes = new()
    {
        { SOUTHHORN, new Vector3(830.75f, 72.98f, -695.98f) },
    };

    public readonly static Dictionary<uint, Vector3> StartingLocations = new()
    {
        { SOUTHHORN, new Vector3(850.33f, 72.99f, -704.07f) },
    };

    // Zone functions
    public static bool IsInSouthHorn()
    {
        return Svc.ClientState.TerritoryType == SOUTHHORN;
    }

    public static bool IsNorthernExpeditionTerritory(uint territoryId)
    {
        return config?.NorthernExpeditionTerritoryId is > 0
               && territoryId == config.NorthernExpeditionTerritoryId;
    }

    public static bool IsInNorthernExpedition()
    {
        return Svc.Objects.LocalPlayer != null
               && IsNorthernExpeditionTerritory(Svc.ClientState.TerritoryType);
    }

    public static bool IsPluginTerritory(uint territoryId)
    {
        return territoryId == SOUTHHORN
               || IsNorthernExpeditionTerritory(territoryId)
               || IsForkedTowerBloodTerritory(territoryId);
    }

    public static bool IsInPluginTerritory()
    {
        return Svc.Objects.LocalPlayer != null && IsPluginTerritory(Svc.ClientState.TerritoryType);
    }

    public static bool IsInOccultCrescent()
    {
        return Svc.Objects.LocalPlayer != null && IsInSouthHorn();
    }

    // Tower functions
    public static bool IsForkedTowerBloodTerritory(uint territoryId)
    {
        return config?.ForkedTowerBloodTerritoryId is > 0
               && territoryId == config.ForkedTowerBloodTerritoryId;
    }

    public static bool HasForkedTowerBloodStatus()
    {
        var player = Svc.Objects.LocalPlayer;
        return player != null && player.StatusList.HasAny(
            PlayerStatus.DutiesAsAssigned,
            PlayerStatus.ResurrectionDenied,
            PlayerStatus.ResurrectionRestricted
        );
    }

    public static bool IsInForkedTowerBlood()
    {
        var player = Svc.Objects.LocalPlayer;
        if (player == null)
        {
            return false;
        }

        var territoryId = Svc.ClientState.TerritoryType;
        if (config?.ForceForkedTowerBloodTerritory == true
            && IsForkedTowerBloodTerritory(territoryId))
        {
            return true;
        }

        return HasForkedTowerBloodStatus()
               && (territoryId == SOUTHHORN
                   || IsNorthernExpeditionTerritory(territoryId)
                   || IsForkedTowerBloodTerritory(territoryId));
    }

    public static bool IsInForkedTower()
    {
        return IsInForkedTowerBlood();
    }

    private static string GetCurrentZoneName()
    {
        if (IsInSouthHorn())
        {
            return "South Horn";
        }

        throw new Exception("Unknown Zone");
    }

    public static string GetCurrentZoneDataDirectory()
    {
        var directory = Path.Join(Svc.PluginInterface.AssemblyLocation.DirectoryName, "Data", GetCurrentZoneName().Replace(" ", ""));
        Directory.CreateDirectory(directory);

        return directory;
    }

    public static Aethernet GetClosestAethernetShard(Vector3 position)
    {
        return AethernetData.All().OrderBy((data) => Vector3.Distance(position, data.Position)).First()!.Aethernet;
    }

    public static IList<IGameObject> GetNearbyAethernetShards(float range = 4.3f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        return Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj)
            .Where(o => AethernetData.All().Select((datum) => datum.BaseId).Contains(o.BaseId))
            .Where(o => Vector3.Distance(o.Position, playerPos) <= range)
            .ToList();
    }

    public static bool IsNearAethernetShard(Aethernet aethernet, float range = 4.3f)
    {
        return GetNearbyAethernetShards(range).Any(o => o.BaseId == aethernet.GetData().BaseId);
    }

    public static IList<IGameObject> GetNearbyKnowledgeCrystal(float range = 4.5f)
    {
        var playerPos = Svc.Objects.LocalPlayer?.Position ?? Vector3.Zero;

        return Svc.Objects
            .Where(o => o.ObjectKind == ObjectKind.EventObj)
            .Where(o => o.BaseId == (uint)OccultObjectType.KnowledgeCrystal)
            .Where(o => Vector3.Distance(o.Position, playerPos) <= range)
            .ToList();
    }

    public static bool IsNearKnowledgeCrystal(float range = 4.5f)
    {
        return GetNearbyKnowledgeCrystal(range).Any();
    }
}
