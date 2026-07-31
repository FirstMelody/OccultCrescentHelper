using System;
using System.Collections.Generic;

namespace BOCCHI.Modules.NorthernRoutes;

public sealed class NorthernRouteFile
{
    public int Version { get; set; } = 2;

    public List<NorthernAethernetRoute> Routes { get; set; } = [];

    public List<NorthernStandbyPoint> StandbyPoints { get; set; } = [];
}

public sealed class NorthernAethernetRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public uint TerritoryId { get; set; }

    public uint MapId { get; set; }

    public string Name { get; set; } = "";

    public int TeleportMenuOrder { get; set; }

    public uint LifestreamDestinationId { get; set; }

    public uint ActiveCustomAetheryteId { get; set; }

    public uint BaseId { get; set; }

    public float InteractionX { get; set; }

    public float InteractionY { get; set; }

    public float InteractionZ { get; set; }

    public float ArrivalX { get; set; }

    public float ArrivalY { get; set; }

    public float ArrivalZ { get; set; }

    public bool HasArrival { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}

public sealed class NorthernStandbyPoint
{
    public uint TerritoryId { get; set; }

    public uint MapId { get; set; }

    public string Name { get; set; } = "默认蹲守点";

    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }
}
