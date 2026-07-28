using System.Collections.Generic;

namespace BOCCHI.Modules.Telemetry;

public sealed class TelemetryBatch
{
    public int SchemaVersion { get; set; } = 1;

    public string PluginVersion { get; set; } = "";

    public List<TelemetryMarker> Markers { get; set; } = [];
}

public sealed class TelemetryMarker
{
    public string Source { get; set; } = "";

    public string Kind { get; set; } = "";

    public uint TerritoryId { get; set; }

    public uint MapId { get; set; }

    public uint? BaseId { get; set; }

    public uint? EventId { get; set; }

    public string? Name { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float? HitboxRadius { get; set; }

    public float? MechanicRadius { get; set; }
}
