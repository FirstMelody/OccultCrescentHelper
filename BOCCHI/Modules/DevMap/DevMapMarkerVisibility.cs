using System;

namespace BOCCHI.Modules.DevMap;

[Flags]
public enum DevMapMarkerVisibility : ushort
{
    None = 0,
    SilverChest = 1 << 0,
    BronzeChest = 1 << 1,
    FortuneCarrot = 1 << 2,
    PotChest = 1 << 3,
    Fate = 1 << 4,
    CriticalEncounter = 1 << 5,
    InvestigationLocation = 1 << 6,
    UnknownChest = 1 << 7,
    Monster = 1 << 8,
    All = SilverChest
          | BronzeChest
          | FortuneCarrot
          | PotChest
          | Fate
          | CriticalEncounter
          | InvestigationLocation
          | UnknownChest
          | Monster,
}
