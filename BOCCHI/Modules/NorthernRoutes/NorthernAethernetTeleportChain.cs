using System;
using System.Linq;
using System.Numerics;
using BOCCHI.ActionHelpers;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.NeoTaskManager;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using ECommons.UIHelpers;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Ocelot.Chain;
using Ocelot.Chain.ChainEx;
using DalamudObjectKind = Dalamud.Game.ClientState.Objects.Enums.ObjectKind;

namespace BOCCHI.Modules.NorthernRoutes;

/// <summary>
/// Uses a North expedition magic route through the native game UI.
/// Lifestream does not know the North territory yet, so source discovery,
/// interaction, and destination selection are deliberately handled here.
/// </summary>
public sealed class NorthernAethernetTeleportChain(
    NorthernAethernetRoute sourceRoute,
    NorthernAethernetRoute destinationRoute
) : ChainFactory
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SelectTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ArrivalTimeout = TimeSpan.FromSeconds(20);
    private static DateTime nextPanelReadErrorLogAt = DateTime.MinValue;

    protected override unsafe Chain Create(Chain chain)
    {
        var failed = false;
        var teleportIssued = false;
        var sawTeleportBusyState = false;
        var openDeadline = DateTime.MinValue;
        var selectDeadline = DateTime.MinValue;
        var teleportRequestedAt = DateTime.MinValue;
        var nextInteractionAt = DateTime.MinValue;
        nint selectedAddon = 0;
        uint selectedCallback = 0;

        return chain
            .ConditionalThen(_ => Player.Mounted, _ => Actions.Unmount.Cast())
            .Wait(500)
            .Then(_ => openDeadline = DateTime.UtcNow + OpenTimeout)
            .Then(new TaskManagerTask(
                () =>
                {
                    if (TryGetTelepotTown(out _))
                    {
                        return true;
                    }

                    if (TrySelectAethernetMenu(out var selectedEntry))
                    {
                        DebugLog.Debug(
                            $"Northern magic route: 已选择中间菜单 {selectedEntry}"
                        );
                        return false;
                    }

                    if (DateTime.UtcNow >= openDeadline)
                    {
                        failed = true;
                        DebugLog.Debug(
                            $"Northern magic route: 无法打开传送面板，"
                            + $"源魔路={sourceRoute.Name}; {DescribeSourceState(sourceRoute)}"
                        );
                        return true;
                    }

                    if (DateTime.UtcNow < nextInteractionAt)
                    {
                        return false;
                    }

                    var source = FindSourceObject(sourceRoute);
                    if (source == null || Player.DistanceTo(source) > 6f)
                    {
                        return false;
                    }

                    if (Svc.Targets.Target?.Address != source.Address)
                    {
                        Svc.Targets.Target = source;
                        nextInteractionAt =
                            DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
                        return false;
                    }

                    unsafe
                    {
                        TargetSystem.Instance()->InteractWithObject(
                            (GameObject*)(void*)source.Address,
                            false
                        );
                    }

                    nextInteractionAt = DateTime.UtcNow + TimeSpan.FromSeconds(1);
                    return false;
                },
                new TaskManagerConfiguration
                {
                    TimeLimitMS = 10000,
                    ShowError = false,
                }
            ))
            .Then(_ => selectDeadline = DateTime.UtcNow + SelectTimeout)
            .Then(new TaskManagerTask(
                () =>
                {
                    if (failed)
                    {
                        return true;
                    }

                    var selectionState = TryFindDestination(
                            destinationRoute.Name,
                            out var addon,
                            out var callback,
                            out var callbackRequired
                        );
                    if (selectionState == DestinationSelectionState.Selecting)
                    {
                        return false;
                    }

                    if (selectionState == DestinationSelectionState.Activated)
                    {
                        if (callbackRequired)
                        {
                            unsafe
                            {
                                Callback.Fire(addon, true, 11, callback);
                            }

                            selectedAddon = (nint)addon;
                            selectedCallback = callback;
                        }

                        teleportIssued = true;
                        teleportRequestedAt = DateTime.UtcNow;
                        DebugLog.Debug(
                            $"Northern magic route: 已选择 {destinationRoute.Name}"
                        );
                        return true;
                    }

                    if (DateTime.UtcNow < selectDeadline)
                    {
                        return false;
                    }

                    failed = true;
                    DebugLog.Debug(
                        $"Northern magic route: 传送面板中未找到 "
                        + $"{destinationRoute.Name}; "
                        + $"可见条目={DescribeVisibleDestinations()}"
                    );
                    return true;
                },
                new TaskManagerConfiguration
                {
                    TimeLimitMS = 10000,
                    ShowError = false,
                }
            ))
            .Wait(250)
            .ConditionalThen(
                _ => teleportIssued && IsSameVisibleAddon(selectedAddon),
                _ =>
                {
                    unsafe
                    {
                        Callback.Fire(
                            (AtkUnitBase*)selectedAddon,
                            true,
                            11,
                            selectedCallback
                        );
                    }
                }
            )
            .ConditionalThen(
                _ => teleportIssued,
                new TaskManagerTask(
                    () =>
                    {
                        var busy = Svc.Condition[ConditionFlag.BetweenAreas]
                                   || Svc.Condition[ConditionFlag.BetweenAreas51];
                        sawTeleportBusyState |= busy;

                        var arrival = NorthernRouteStore.GetArrivalPosition(
                            destinationRoute
                        );
                        if (!busy
                            && destinationRoute.HasArrival
                            && Player.DistanceTo(arrival) <= 25f)
                        {
                            return true;
                        }

                        if (sawTeleportBusyState && !busy)
                        {
                            if (destinationRoute.HasArrival
                                && Player.DistanceTo(arrival) > 25f)
                            {
                                DebugLog.Debug(
                                    $"Northern magic route arrival mismatch: "
                                    + $"selected={destinationRoute.Name}, "
                                    + $"player={Player.Position}, "
                                    + $"expected={arrival}, "
                                    + $"distance={Player.DistanceTo(arrival):F1}"
                                );
                            }

                            return true;
                        }

                        return DateTime.UtcNow - teleportRequestedAt
                               >= ArrivalTimeout;
                    },
                    new TaskManagerConfiguration
                    {
                        TimeLimitMS = 25000,
                        ShowError = false,
                    }
                )
            );
    }

    private static IGameObject? FindSourceObject(NorthernAethernetRoute route)
    {
        var expected = NorthernRouteStore.GetInteractionPosition(route);
        return Svc.Objects
            .Where(obj =>
                obj.ObjectKind is DalamudObjectKind.EventObj or DalamudObjectKind.Aetheryte
                && (
                    route.BaseId == 0
                    || obj.BaseId == route.BaseId
                    || string.Equals(
                        obj.Name.TextValue,
                        "简易魔路",
                        StringComparison.Ordinal
                    )
                )
                && Vector3.Distance(obj.Position, expected) <= 10f
            )
            .OrderBy(obj => Vector3.Distance(obj.Position, expected))
            .FirstOrDefault();
    }

    private static string DescribeSourceState(NorthernAethernetRoute route)
    {
        var expected = NorthernRouteStore.GetInteractionPosition(route);
        var candidates = Svc.Objects
            .Where(obj =>
                obj.ObjectKind is DalamudObjectKind.EventObj or DalamudObjectKind.Aetheryte
                && (
                    obj.BaseId == route.BaseId
                    || string.Equals(
                        obj.Name.TextValue,
                        "简易魔路",
                        StringComparison.Ordinal
                    )
                )
                && Vector3.Distance(obj.Position, expected) <= 20f
            )
            .Select(obj =>
                $"{obj.Name.TextValue}/Base={obj.BaseId}/"
                + $"Kind={obj.ObjectKind}/Targetable={obj.IsTargetable}/"
                + $"PlayerDistance={Player.DistanceTo(obj):F1}/"
                + $"PositionDistance={Vector3.Distance(obj.Position, expected):F1}/"
                + $"Targeted={Svc.Targets.Target?.Address == obj.Address}"
            )
            .ToList();
        return candidates.Count == 0
            ? $"附近未发现对象; Player={Player.Position}; Expected={expected}"
            : string.Join(" | ", candidates);
    }

    private static unsafe bool TryGetTelepotTown(out AtkUnitBase* addon)
    {
        addon = Svc.GameGui.GetAddonByName<AtkUnitBase>("TelepotTown", 1);
        return addon != null
               && addon->IsVisible
               && addon->AtkValues != null
               && addon->AtkValuesCount > 262;
    }

    private static unsafe bool TrySelectAethernetMenu(out string selectedEntry)
    {
        selectedEntry = "";
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>("SelectString", 1);
        if (addon == null || !addon->IsVisible)
        {
            return false;
        }

        try
        {
            var master = new AddonMaster.SelectString((nint)addon);
            if (!master.IsAddonReady)
            {
                return false;
            }

            foreach (var entry in master.Entries)
            {
                if (!entry.Text.Contains("传送网", StringComparison.Ordinal)
                    && !entry.Text.Contains("魔路", StringComparison.Ordinal))
                {
                    continue;
                }

                selectedEntry = entry.Text;
                entry.Select();
                return true;
            }
        }
        catch (Exception ex)
        {
            DebugLog.Debug(
                $"Northern magic route intermediate menu read failed: {ex.Message}"
            );
        }

        return false;
    }

    private static unsafe DestinationSelectionState TryFindDestination(
        string destinationName,
        out AtkUnitBase* addon,
        out uint callback,
        out bool callbackRequired
    )
    {
        callback = 0;
        callbackRequired = false;
        if (!TryGetTelepotTown(out addon))
        {
            return DestinationSelectionState.NotFound;
        }

        try
        {
            var visibleSelection = TrySelectVisibleDestination(
                addon,
                destinationName,
                out callback,
                out callbackRequired
            );
            if (visibleSelection != DestinationSelectionState.NotFound)
            {
                return visibleSelection;
            }

            var reader = new TelepotTownReader(addon);
            var count = (int)Math.Min(reader.NumEntries, 20);
            for (var index = 0; index < count; index++)
            {
                if (!string.Equals(
                        reader.GetDestinationName(index).Trim(),
                        destinationName.Trim(),
                        StringComparison.Ordinal
                    ))
                {
                    continue;
                }

                callback = reader.GetDestinationCallback(index);
                callbackRequired = true;
                return DestinationSelectionState.Activated;
            }
        }
        catch (Exception ex)
        {
            if (DateTime.UtcNow >= nextPanelReadErrorLogAt)
            {
                nextPanelReadErrorLogAt =
                    DateTime.UtcNow + TimeSpan.FromSeconds(2);
                DebugLog.Debug(
                    $"Northern magic route panel read failed: {ex.Message}"
                );
            }
        }

        return DestinationSelectionState.NotFound;
    }

    private static unsafe DestinationSelectionState TrySelectVisibleDestination(
        AtkUnitBase* addon,
        string destinationName,
        out uint callback,
        out bool callbackRequired
    )
    {
        callback = 0;
        callbackRequired = false;
        if (!TryGetDestinationList(addon, out var list))
        {
            return DestinationSelectionState.NotFound;
        }

        var visibleNodeCount = Math.Min(
            Math.Max(0, list->UldManager.NodeListCount - 1),
            52
        );
        for (var visualIndex = 0; visualIndex < visibleNodeCount; visualIndex++)
        {
            if (!TryGetVisibleDestination(
                    list,
                    visualIndex,
                    out var text,
                    out var logicalIndex
                ))
            {
                continue;
            }

            if (!string.Equals(
                    text,
                    destinationName.Trim(),
                    StringComparison.Ordinal
                ))
            {
                continue;
            }

            if (list->SelectedItemIndex != logicalIndex)
            {
                DebugLog.Debug(
                    $"Northern magic route: 切换面板选中项 "
                    + $"{list->SelectedItemIndex} -> {logicalIndex} "
                    + $"({text}, visual={visualIndex})"
                );
                list->SelectItem(logicalIndex, true);
                return DestinationSelectionState.Selecting;
            }

            if (!TryGetTreeDestinationCallback(
                    addon,
                    destinationName,
                    logicalIndex,
                    out callback
                ))
            {
                DebugLog.Debug(
                    $"Northern magic route: 无法读取目标回调 "
                    + $"{destinationName} (logical={logicalIndex})"
                );
                return DestinationSelectionState.NotFound;
            }

            callbackRequired = true;
            return DestinationSelectionState.Activated;
        }

        return DestinationSelectionState.NotFound;
    }

    private static unsafe bool TryGetTreeDestinationCallback(
        AtkUnitBase* addon,
        string destinationName,
        int preferredIndex,
        out uint callback
    )
    {
        callback = 0;
        var teleportTown = (AddonTeleportTown*)addon;
        var tree = teleportTown->List;
        if (tree == null)
        {
            return false;
        }

        if (TryReadTreeItemCallback(
                tree,
                preferredIndex,
                destinationName,
                out callback
            ))
        {
            return true;
        }

        var itemCount = Math.Min(tree->Items.Count, 64);
        for (var index = 0; index < itemCount; index++)
        {
            if (index == preferredIndex)
            {
                continue;
            }

            if (TryReadTreeItemCallback(
                    tree,
                    index,
                    destinationName,
                    out callback
                ))
            {
                return true;
            }
        }

        return false;
    }

    private static unsafe bool TryReadTreeItemCallback(
        AtkComponentTreeList* tree,
        int index,
        string destinationName,
        out uint callback
    )
    {
        callback = 0;
        if (index < 0 || index >= tree->Items.Count)
        {
            return false;
        }

        var item = tree->GetItem(index);
        if (item == null
            || item->StringValues.Count == 0
            || item->UIntValues.Count < 4)
        {
            return false;
        }

        var name = item->StringValues[0].ToString().Trim();
        if (!string.Equals(
                name,
                destinationName.Trim(),
                StringComparison.Ordinal
            ))
        {
            return false;
        }

        callback = item->UIntValues[3];
        DebugLog.Debug(
            $"Northern magic route: 目标回调 {destinationName} "
            + $"item={index}, callback={callback}"
        );
        return true;
    }

    private static unsafe string DescribeVisibleDestinations()
    {
        if (!TryGetTelepotTown(out var addon)
            || !TryGetDestinationList(addon, out var list))
        {
            return "列表不可用";
        }

        var visibleNodeCount = Math.Min(
            Math.Max(0, list->UldManager.NodeListCount - 1),
            52
        );
        var names = Enumerable.Range(0, visibleNodeCount)
            .Select(visualIndex =>
                TryGetVisibleDestination(
                    list,
                    visualIndex,
                    out var name,
                    out var logicalIndex
                )
                    ? $"[{logicalIndex}]{name}"
                    : ""
            )
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();
        return names.Length == 0 ? "（空）" : string.Join(" | ", names);
    }

    private static unsafe bool TryGetDestinationList(
        AtkUnitBase* addon,
        out AtkComponentList* list
    )
    {
        list = null;
        if (addon == null
            || addon->UldManager.NodeList == null
            || addon->UldManager.NodeListCount <= 16)
        {
            return false;
        }

        var node = addon->UldManager.NodeList[16];
        var componentNode = node == null ? null : node->GetAsAtkComponentNode();
        if (componentNode == null || componentNode->Component == null)
        {
            return false;
        }

        list = (AtkComponentList*)componentNode->Component;
        return list->UldManager.NodeList != null;
    }

    private static unsafe bool TryGetVisibleDestination(
        AtkComponentList* list,
        int visualIndex,
        out string name,
        out int logicalIndex
    )
    {
        name = "";
        logicalIndex = -1;
        var nodeIndex = visualIndex + 1;
        if (nodeIndex >= list->UldManager.NodeListCount)
        {
            return false;
        }

        var node = list->UldManager.NodeList[nodeIndex];
        var componentNode = node == null ? null : node->GetAsAtkComponentNode();
        if (componentNode == null
            || componentNode->Component == null
            || componentNode->Component->UldManager.NodeList == null
            || componentNode->Component->UldManager.NodeListCount <= 3)
        {
            return false;
        }

        var renderer =
            (AtkComponentListItemRenderer*)componentNode->Component;
        var textNodePointer =
            componentNode->Component->UldManager.NodeList[3];
        var textNode = textNodePointer == null
            ? null
            : textNodePointer->GetAsAtkTextNode();
        if (textNode == null)
        {
            return false;
        }

        name = GenericHelpers.ReadSeString(&textNode->NodeText).GetText().Trim();
        logicalIndex = renderer->ListItemIndex;
        return logicalIndex >= 0 && !string.IsNullOrWhiteSpace(name);
    }

    private static unsafe bool IsSameVisibleAddon(nint address)
    {
        if (address == 0)
        {
            return false;
        }

        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>("TelepotTown", 1);
        return addon != null && addon->IsVisible && (nint)addon == address;
    }

    private sealed unsafe class TelepotTownReader : AtkReader
    {
        private readonly AtkUnitBase* addon;

        public TelepotTownReader(AtkUnitBase* addon)
            : base(addon)
        {
            this.addon = addon;
        }

        private int LayoutOffset
        {
            get => addon->AtkValuesCount > 1
                   && addon->AtkValues[0].Type == AtkValueType.Bool
                ? 1
                : 0;
        }

        public uint NumEntries
        {
            get => ReadUInt(LayoutOffset) ?? 0;
        }

        public string GetDestinationName(int index)
        {
            return ReadSeString(262 + LayoutOffset + index)?.TextValue ?? "";
        }

        public uint GetDestinationCallback(int index)
        {
            return ReadUInt(6 + LayoutOffset + index * 4 + 3) ?? 0;
        }
    }

    private enum DestinationSelectionState
    {
        NotFound,
        Selecting,
        Activated,
    }
}
