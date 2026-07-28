using BOCCHI.Data;
using BOCCHI.Modules.StateManager;
using ECommons.DalamudServices;
using Ocelot.Modules;

namespace BOCCHI.Modules.WindowManager;

[OcelotModule(5)]
public class WindowManagerModule(Plugin _plugin, Config _config) : Module(_plugin, _config)
{
    public override WindowManagerConfig Config
    {
        get => PluginConfig.WindowManagerConfig;
    }

    public override bool ShouldInitialize
    {
        get => true;
    }


    private bool mainClosed = false;

    private bool configClosed = false;


    public override void PostInitialize()
    {
        if (ZoneData.IsNorthernExpeditionTerritory(Svc.ClientState.TerritoryType)
            || Config.OpenMainOnStartUp)
        {
            Plugin.Windows.OpenMainUI();
        }


        if (Config.OpenConfigOnStartUp)
        {
            Plugin.Windows.OpenConfigUI();
        }

        GetModule<StateManagerModule>().OnEnterInCombat += EnterCombat;
        GetModule<StateManagerModule>().OnEnterInCriticalEncounter += EnterCombat;
        GetModule<StateManagerModule>().OnEnterInFate += EnterCombat;
        GetModule<StateManagerModule>().OnEnterIdle += ExitCombat;
    }

    public override void OnTerritoryChanged(uint id)
    {
        if (ZoneData.IsNorthernExpeditionTerritory(id))
        {
            Plugin.Windows.OpenMainUI();
        }
        else if (ZoneData.IsPluginTerritory(id))
        {
            if (Config.OpenMainOnEnter)
            {
                Plugin.Windows.OpenMainUI();
            }


            if (Config.OpenConfigOnEnter)
            {
                Plugin.Windows.OpenConfigUI();
            }
        }
        else
        {
            if (Config.CloseMainOnExit)
            {
                Plugin.Windows.CloseMainUI();
            }


            if (Config.CloseConfigOnExit)
            {
                Plugin.Windows.CloseConfigUI();
            }
        }
    }

    private void EnterCombat(StateManagerModule module)
    {
        if (Config.HideMainInCombat && Plugin.Windows.IsMainUIOpen())
        {
            Plugin.Windows.CloseMainUI();
            mainClosed = true;
        }

        if (Config.HideConfigInCombat && Plugin.Windows.IsConfigUIOpen())
        {
            Plugin.Windows.CloseConfigUI();
            configClosed = true;
        }
    }

    private void ExitCombat(StateManagerModule module)
    {
        if (Config.HideMainInCombat && mainClosed)
        {
            Plugin.Windows.OpenMainUI();
            mainClosed = false;
        }

        if (Config.HideConfigInCombat && configClosed)
        {
            Plugin.Windows.OpenConfigUI();
            configClosed = false;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        GetModule<StateManagerModule>().OnEnterInCombat -= EnterCombat;
        GetModule<StateManagerModule>().OnEnterInCriticalEncounter -= EnterCombat;
        GetModule<StateManagerModule>().OnEnterInFate -= EnterCombat;
        GetModule<StateManagerModule>().OnEnterIdle -= ExitCombat;
    }
}
