using System;
using BOCCHI.Data;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace BOCCHI.Modules.Automator;

internal sealed class AutoResurrectionController
{
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
        if (reviveAgent == null || !reviveAgent->IsAgentActive())
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
        if (reviveAgent == null
            || !reviveAgent->IsAgentActive()
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

    private void Reset()
    {
        acceptAt = DateTime.MinValue;
        promptAddress = 0;
        promptHandled = false;
    }
}
