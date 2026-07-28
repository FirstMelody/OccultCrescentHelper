using System.Collections.Generic;

namespace BOCCHI.Modules.DevMap;

public class ForkedTowerEventObjFile
{
    public int Version { get; set; } = 2;

    public List<ForkedTowerEventObjRecord> EventObjects { get; set; } = [];

    public List<ForkedTowerTrapGroupDefinition> TrapGroups { get; set; } = [];
}
