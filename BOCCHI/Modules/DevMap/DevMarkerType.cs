namespace BOCCHI.Modules.DevMap;

public enum DevMarkerType
{
    SilverChest,
    BronzeChest,
    FortuneCarrot,
    // Kept for backwards-compatible JSON migration. New markers use FortuneCarrot.
    FortuneCarrotChest,
    PotChest,
    Fate,
    CriticalEncounter,
    InvestigationLocation,
    UnknownChest,
}
