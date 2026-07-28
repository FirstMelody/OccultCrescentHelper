using System;
using System.Numerics;
using System.Text.Json.Serialization;

namespace BOCCHI.Modules.DevMap;

public class DevMapMarker
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DevMarkerType Type { get; set; }

    public uint EventId { get; set; }

    public uint BaseId { get; set; }

    public uint Level { get; set; }

    public int ObservationCount { get; set; } = 1;

    public string Name { get; set; } = "";

    public uint TerritoryId { get; set; }

    public uint MapId { get; set; }

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public Vector3 Position
    {
        get => new(X, Y, Z);
    }
}
