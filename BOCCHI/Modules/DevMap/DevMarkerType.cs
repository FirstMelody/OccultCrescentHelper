namespace BOCCHI.Modules.DevMap;

public enum DevMarkerType
{
    SilverChest,
    BronzeChest,
    FortuneCarrot,
    // Kept for backwards-compatible JSON migration. New markers use FortuneCarrot.
    FortuneCarrotChest,
    PotChest,
    RerollChest,
    Fate,
    CriticalEncounter,
    // Kept only so v3 JSON can be read and purged during migration.
    InvestigationLocation,
    UnknownChest,
    Monster,
}
