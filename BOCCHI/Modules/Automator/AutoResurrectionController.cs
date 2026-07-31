using System;
using BOCCHI.Data;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace BOCCHI.Modules.Automator;

internal sealed class AutoResurrectionController
{
    private const uint InvalidEntityId = 0xE0000000;

    private DateTime acceptAt = DateTime.MinValue;
    private nint promptAddress;
    private bool promptHandled;

    public unsafe void Update(AutomatorConfig config)
    {
        if (!config.ShouldAutoAcceptResurrection
            || !ZoneData.IsInPluginTerritory()
            || !Player.IsDead)
        {
            Reset();
            return;
        }

        var reviveAgent = AgentRevive.Instance();
        if (!HasIncomingResurrection(reviveAgent))
        {
            Reset();
            return;
        }

        var addon = Svc.GameGui.GetAddonByName<AddonSelectYesno>(
            "SelectYesno",
            1
        );
        if (addon == null || !addon->AtkUnitBase.IsVisible)
        {
            acceptAt = DateTime.MinValue;
            promptAddress = 0;
            return;
        }

        if (promptHandled)
        {
            return;
        }

        var currentAddress = (nint)addon;
        if (promptAddress != currentAddress || acceptAt == DateTime.MinValue)
        {
            promptAddress = currentAddress;
            acceptAt = DateTime.UtcNow + TimeSpan.FromSeconds(
                Math.Clamp(config.AutoAcceptResurrectionDelay, 0f, 10f)
            );
            DebugLog.Debug(
                "Auto resurrection: detected revive prompt; "
                + $"source={reviveAgent->ResurrectingPlayerId}, "
                + $"remaining={reviveAgent->ResurrectionTimeLeft}, "
                + $"state={reviveAgent->ReviveState}, "
                + $"accepting after {config.AutoAcceptResurrectionDelay:F1}s"
            );
            return;
        }

        if (DateTime.UtcNow < acceptAt)
        {
            return;
        }

        // Re-check both the dedicated revive agent and the current addon
        // immediately before firing the generic SelectYesno "yes" callback.
        // This prevents accepting unrelated confirmations while the player is dead.
        reviveAgent = AgentRevive.Instance();
        addon = Svc.GameGui.GetAddonByName<AddonSelectYesno>(
            "SelectYesno",
            1
        );
        if (!HasIncomingResurrection(reviveAgent)
            || addon == null
            || !addon->AtkUnitBase.IsVisible
            || (nint)addon != promptAddress
            || !Player.IsDead)
        {
            Reset();
            return;
        }

        promptHandled = true;
        acceptAt = DateTime.MinValue;
        addon->AtkUnitBase.FireCallbackInt(0);
        DebugLog.Debug("Auto resurrection: accepted revive prompt");
    }

    private static unsafe bool HasIncomingResurrection(AgentRevive* reviveAgent)
    {
        if (reviveAgent == null
            || !reviveAgent->IsAgentActive()
            || reviveAgent->Revive == null)
        {
            return false;
        }

        var sourceId = reviveAgent->ResurrectingPlayerId;
        return sourceId is not 0 and not InvalidEntityId
               && reviveAgent->ResurrectionTimeLeft > 0
               && reviveAgent->Revive->Timer > 0f;
    }

    private void Reset()
    {
        acceptAt = DateTime.MinValue;
        promptAddress = 0;
        promptHandled = false;
    }
}
