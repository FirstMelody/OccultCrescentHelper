using System.Numerics;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using XIVTreasure = Lumina.Excel.Sheets.Treasure;
using TreasureFlags = FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureFlags;

namespace BOCCHI.Modules.Treasure;

public class Treasure
{
    public ulong Id { get; private set; }

    private uint baseId;
    private Vector3 position;
    private string objectName = "";
    private bool valid;

    private TreasureFlags LastFlags = TreasureFlags.None;

    public Treasure(IGameObject obj)
    {
        Update(obj);
    }

    public unsafe bool Update(IGameObject obj)
    {
        Id = (ulong)(nuint)obj.Address;
        baseId = obj.BaseId;
        position = obj.Position;
        objectName = obj.Name.TextValue.Trim();
        valid = obj is { IsDead: false, IsTargetable: true };

        var gameObject = (GameObject*)(void*)obj.Address;
        if (gameObject == null)
        {
            return false;
        }

        var instance = (FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure*)gameObject;
        var currentFlags = instance->Flags;

        if (currentFlags != LastFlags)
        {
            var wasNotOpened = !LastFlags.HasFlag(TreasureFlags.Opened);
            var isNowOpened = currentFlags.HasFlag(TreasureFlags.Opened);

            LastFlags = currentFlags;

            if (wasNotOpened && isNowOpened)
            {
                return true;
            }
        }

        return false;
    }


    private XIVTreasure? GetData()
    {
        return Svc.Data.GetExcelSheet<XIVTreasure>().GetRowOrDefault(baseId);
    }

    public bool IsValid()
    {
        return valid;
    }

    public Vector3 GetPosition()
    {
        return position;
    }

    public uint GetBaseId()
    {
        return baseId;
    }

    public string GetObjectName()
    {
        return objectName;
    }

    private uint? GetModelId()
    {
        return GetData()?.SGB.RowId;
    }

    public TreasureType GetTreasureType()
    {
        switch (GetModelId() ?? 0)
        {
            case 1597:
                return TreasureType.Silver;
            case 1596:
                return TreasureType.Bronze;
            case 1598:
                return TreasureType.Gold;
            default:
                return TreasureType.Unknown;
        }
    }

    public Vector4 GetColor()
    {
        return GetTreasureType() switch
        {
            TreasureType.Bronze => TreasureModule.Bronze,
            TreasureType.Silver => TreasureModule.Silver,
            TreasureType.Gold => TreasureModule.Gold,
            _ => TreasureModule.Unknown,
        };
    }

    public string GetName()
    {
        return GetTreasureType() switch
        {
            TreasureType.Bronze => "青铜宝箱",
            TreasureType.Silver => "白银宝箱",
            TreasureType.Gold => "罐子宝箱",
            _ => "未知宝箱",
        };
    }
}
