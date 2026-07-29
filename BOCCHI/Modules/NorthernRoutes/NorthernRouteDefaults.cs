using System;
using System.Collections.Generic;

namespace BOCCHI.Modules.NorthernRoutes;

public static class NorthernRouteDefaults
{
    public const uint NorthTerritoryId = 1346;
    public const uint NorthMapId = 1135;

    public static IReadOnlyList<NorthernAethernetRoute> SourceOnlyRoutes { get; } =
    [
        new NorthernAethernetRoute
        {
            Id = Guid.Parse("13461135-0000-4000-8000-000000000000"),
            TerritoryId = NorthTerritoryId,
            MapId = NorthMapId,
            Name = "北部调查队营地（仅起点）",
            BaseId = 2015429,
            InteractionX = 880.0015f,
            InteractionY = 259.7396f,
            InteractionZ = 880.0587f,
            HasArrival = false,
            Enabled = true,
            RecordedAt = new DateTime(2026, 7, 29, 5, 35, 35, DateTimeKind.Utc),
        },
    ];

    public static IReadOnlyList<NorthernAethernetRoute> Routes { get; } =
    [
        new NorthernAethernetRoute
        {
            Id = Guid.Parse("13461135-0001-4000-8000-000000000001"),
            TerritoryId = NorthTerritoryId,
            MapId = NorthMapId,
            Name = "卡纳克城塞",
            BaseId = 2015434,
            InteractionX = 451.6841f,
            InteractionY = 70.9269f,
            InteractionZ = 528.8388f,
            ArrivalX = 454.19888f,
            ArrivalY = 69.99997f,
            ArrivalZ = 530.5268f,
            HasArrival = true,
            Enabled = true,
            RecordedAt = new DateTime(2026, 7, 29, 5, 15, 57, DateTimeKind.Utc),
        },
        new NorthernAethernetRoute
        {
            Id = Guid.Parse("13461135-0002-4000-8000-000000000002"),
            TerritoryId = NorthTerritoryId,
            MapId = NorthMapId,
            Name = "沉没圣堂前",
            BaseId = 2015430,
            InteractionX = 357.6689f,
            InteractionY = 45.76582f,
            InteractionZ = -554.3083f,
            ArrivalX = 358.4441f,
            ArrivalY = 45.188988f,
            ArrivalZ = -557.97723f,
            HasArrival = true,
            Enabled = true,
            RecordedAt = new DateTime(2026, 7, 29, 5, 16, 10, DateTimeKind.Utc),
        },
        new NorthernAethernetRoute
        {
            Id = Guid.Parse("13461135-0003-4000-8000-000000000003"),
            TerritoryId = NorthTerritoryId,
            MapId = NorthMapId,
            Name = "浮游遗迹",
            BaseId = 2015431,
            InteractionX = -547.2471f,
            InteractionY = 67.99808f,
            InteractionZ = 594.4042f,
            ArrivalX = -550.49835f,
            ArrivalY = 67.22933f,
            ArrivalZ = 597.12604f,
            HasArrival = true,
            Enabled = true,
            RecordedAt = new DateTime(2026, 7, 29, 5, 16, 15, DateTimeKind.Utc),
        },
        new NorthernAethernetRoute
        {
            Id = Guid.Parse("13461135-0004-4000-8000-000000000004"),
            TerritoryId = NorthTerritoryId,
            MapId = NorthMapId,
            Name = "腐坏的街道前",
            BaseId = 2015432,
            InteractionX = -388.5732f,
            InteractionY = 41.22126f,
            InteractionZ = -440.5197f,
            ArrivalX = -387.75488f,
            ArrivalY = 39.382263f,
            ArrivalZ = -437.82172f,
            HasArrival = true,
            Enabled = true,
            RecordedAt = new DateTime(2026, 7, 29, 5, 16, 19, DateTimeKind.Utc),
        },
        new NorthernAethernetRoute
        {
            Id = Guid.Parse("13461135-0005-4000-8000-000000000005"),
            TerritoryId = NorthTerritoryId,
            MapId = NorthMapId,
            Name = "妖火渔村",
            BaseId = 2015433,
            InteractionX = -13.3638f,
            InteractionY = 3.144829f,
            InteractionZ = -40.51193f,
            ArrivalX = -14.424f,
            ArrivalY = 2.0066032f,
            ArrivalZ = -44.52533f,
            HasArrival = true,
            Enabled = true,
            RecordedAt = new DateTime(2026, 7, 29, 5, 16, 24, DateTimeKind.Utc),
        },
    ];
}
