using System.Numerics;

namespace BOCCHI.Modules.DevMap;

internal sealed class TowerMapTrapCandidate
{
    public required Vector3 Position { get; init; }

    public required uint BaseId { get; init; }

    public required ForkedTowerEventObjType Type { get; init; }

    public required float MechanicRadius { get; init; }

    public required string GroupKey { get; set; }

    public required string GroupName { get; set; }

    public required int MaxActive { get; set; }

    public bool IsBuiltInGroup { get; init; }

    public bool IsObservedInCurrentRun { get; set; }

    public bool IsExcluded { get; set; }

    public bool IsExcludedByObservedVariant { get; set; }

    public int ObservedInGroup { get; set; }

    public ForkedTowerEventObjRecord? SourceRecord { get; set; }
}
