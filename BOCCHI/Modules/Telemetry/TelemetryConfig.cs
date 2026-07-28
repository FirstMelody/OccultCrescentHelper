using Ocelot.Modules;

namespace BOCCHI.Modules.Telemetry;

public class TelemetryConfig : ModuleConfig
{
    public bool Enabled { get; set; }

    public int ConsentVersion { get; set; }

    public bool IncludeTowerObjects { get; set; } = true;

    public bool ShowSharedMarkers { get; set; } = true;
}
