using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;

namespace BOCCHI.Modules.DevMap;

public class ForkedTowerEventObjRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public uint TerritoryId { get; set; }

    public uint MapId { get; set; }

    public uint BaseId { get; set; }

    public string Name { get; set; } = "";

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }

    public float HitboxRadius { get; set; }

    public float? MechanicRadius { get; set; }

    public ForkedTowerEventObjType Type { get; set; } = ForkedTowerEventObjType.Unknown;

    public bool WasTargetable { get; set; }

    public string TowerRunId { get; set; } = "";

    public List<string> ObservedRunIds { get; set; } = [];

    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.Now;

    [JsonIgnore]
    public Vector3 Position
    {
        get => new(X, Y, Z);
    }
}
