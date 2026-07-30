using System.Collections.Generic;
using BOCCHI.Data.Traps;

namespace BOCCHI.Modules.ForkedTower;

public class TrackedGroup(TrapGroup group)
{
    private readonly TrapGroup Group = group.Clone();

    private readonly HashSet<string> trapKeys = [];

    public void Observe(string trapKey)
    {
        trapKeys.Add(trapKey);
    }

    public bool HasDiscoveredAllTraps()
    {
        return trapKeys.Count >= Group.MaxInGroup;
    }
}
