using System;
using System.Linq;
using BOCCHI.Data;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI;

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

        var addon = Svc.GameGui.GetAddonByName<AddonSelectYesno>(
            "SelectYesno",
            1
        );
        if (addon == null
            || !addon->AtkUnitBase.IsVisible
            || !IsResurrectionPrompt(addon))
        {
            Reset();
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

        // Re-check the localized prompt text immediately before firing the
        // generic SelectYesno "yes" callback. Addon rows 112 and 113 are the
        // resurrection prompts; the death Return prompt is the distinct row 111.
        addon = Svc.GameGui.GetAddonByName<AddonSelectYesno>(
            "SelectYesno",
            1
        );
        if (addon == null
            || !addon->AtkUnitBase.IsVisible
            || (nint)addon != promptAddress
            || !IsResurrectionPrompt(addon)
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

    private static unsafe bool IsResurrectionPrompt(AddonSelectYesno* addon)
    {
        if (addon == null || addon->PromptText == null)
        {
            return false;
        }

        var prompt = NormalizePrompt(addon->PromptText->NodeText.ToString());
        var addonSheet = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        return MatchesAddonText(addonSheet, prompt, 112)
               || MatchesAddonText(addonSheet, prompt, 113);
    }

    private static bool MatchesAddonText(
        Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.Addon> addonSheet,
        string prompt,
        uint rowId
    )
    {
        return addonSheet.TryGetRow(rowId, out var row)
               && string.Equals(
                   prompt,
                   NormalizePrompt(row.Text.ToString()),
                   StringComparison.Ordinal
               );
    }

    private static string NormalizePrompt(string text)
    {
        return string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
    }

    private void Reset()
    {
        acceptAt = DateTime.MinValue;
        promptAddress = 0;
        promptHandled = false;
    }
}
