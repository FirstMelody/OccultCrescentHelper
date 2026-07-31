using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Fates;
using ECommons;

namespace BOCCHI.Modules.Fates;

public class Fate
{
    public readonly EventData Data;

    public uint Id { get; }

    public string Name { get; private set; } = "未知临危受命";

    public float Radius { get; private set; }

    public Vector3 StartPosition { get; private set; }

    public readonly EventProgress Progress = new();

    public byte CurrentProgress { get; private set; }

    public Fate(IFate fate)
    {
        Id = fate.FateId;
        if (!EventData.Fates.TryGetValue(Id, out var data))
        {
            data = new EventData
            {
                Id = Id,
                Type = EventType.Fate,
                InternalName = fate.Name.GetText(),
            };
        }

        Data = data;
        Refresh(fate);
    }

    internal void Refresh(IFate fate)
    {
        // IFate is backed by game memory and becomes invalid as soon as the FATE
        // despawns. Keep only managed snapshots outside the current Svc.Fates scan.
        Name = fate.Name.GetText();
        Radius = Data.Radius ?? fate.Radius;
        StartPosition = Data.StartPosition ?? fate.Position;
        CurrentProgress = fate.Progress;
    }

    public void Update()
    {
        if (CurrentProgress <= 0)
        {
            return;
        }

        if (Progress.Count == 0 || Progress.Latest != CurrentProgress)
        {
            Progress.Add(CurrentProgress);
        }
    }

    public bool IsPotFate()
    {
        return Data.Note == MonsterNote.PersistentPots;
    }

    public Aethernet GetAethernet()
    {
        return Data.Aethernet ?? ZoneData.GetClosestAethernetShard(StartPosition);
    }
}
