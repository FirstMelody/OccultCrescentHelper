using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace BOCCHI.Modules.Treasure;

public class TreasureTracker : IDisposable
{
    public List<Treasure> Treasures { get; private set; } = [];

    public bool CountInitialised { get; private set; } = false;

    public int BronzeChests { get; private set; } = 0;

    public int SilverChests { get; private set; } = 0;

    private readonly TimeSpan ParseWideTextCooldown = TimeSpan.FromSeconds(5);

    private DateTime LastParseWideText = DateTime.MinValue;

    public TreasureTracker()
    {
        Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "_WideText", OnWideTextPostDraw);
    }

    public void Tick(Plugin plugin)
    {
        if (!WorldObjectScanGuard.IsSafe())
        {
            Treasures = [];
            return;
        }

        var known = Treasures.ToDictionary(treasure => treasure.Id);
        var seen = new HashSet<ulong>();
        foreach (var obj in Svc.Objects)
        {
            if (obj.Address == nint.Zero
                || obj.ObjectKind != ObjectKind.Treasure)
            {
                continue;
            }

            // GameObjectId dereferences native object memory and was the exact
            // source of the zone-entry crash. The object-table address is
            // sufficient as an identity for this short-lived live snapshot.
            var id = (ulong)(nuint)obj.Address;
            seen.Add(id);
            if (!known.TryGetValue(id, out var treasure))
            {
                treasure = new Treasure(obj);
                if (treasure.IsValid())
                {
                    Treasures.Add(treasure);
                    known[id] = treasure;
                }

                continue;
            }

            if (!treasure.Update(obj))
            {
                continue;
            }

            if (treasure.GetTreasureType() == TreasureType.Bronze)
            {
                BronzeChests = Math.Max(0, BronzeChests - 1);
            }
            else if (treasure.GetTreasureType() == TreasureType.Silver)
            {
                SilverChests = Math.Max(0, SilverChests - 1);
            }
        }

        Treasures.RemoveAll(treasure =>
            !seen.Contains(treasure.Id) || !treasure.IsValid()
        );
        Treasures = Treasures
            .OrderBy(treasure => Player.DistanceTo(treasure.GetPosition()))
            .ToList();
    }

    private unsafe void OnWideTextPostDraw(AddonEvent type, AddonArgs args)
    {
        if (!ZoneData.IsInOccultCrescent())
        {
            return;
        }

        var addon = (AtkUnitBase*)args.Addon.Address;
        if (addon == null || !addon->IsVisible)
        {
            return;
        }

        var timeSinceLast = DateTime.Now - LastParseWideText;
        if (timeSinceLast < ParseWideTextCooldown)
        {
            return;
        }

        LastParseWideText = DateTime.Now;

        var pattern = LogMessageHelper.GetLogMessagePattern(10965);
        var node = addon->GetNodeById(3);
        var textNode = node == null ? null : node->GetAsAtkTextNode();
        if (textNode == null)
        {
            return;
        }

        var text = textNode->NodeText.ToString();
        var match = Regex.Match(text, pattern);

        if (!match.Success)
        {
            return;
        }

        SilverChests = int.Parse(match.Groups[1].Value);
        BronzeChests = int.Parse(match.Groups[2].Value);
        CountInitialised = true;
    }

    public void Dispose()
    {
        Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, "_WideText", OnWideTextPostDraw);
    }
}
