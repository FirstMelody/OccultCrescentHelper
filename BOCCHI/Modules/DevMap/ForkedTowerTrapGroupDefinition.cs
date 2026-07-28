using System;
using System.Collections.Generic;

namespace BOCCHI.Modules.DevMap;

public class ForkedTowerTrapGroupDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public uint TerritoryId { get; set; }

    public uint MapId { get; set; }

    public int MaxActive { get; set; } = 1;

    public List<Guid> CandidateIds { get; set; } = [];
}
