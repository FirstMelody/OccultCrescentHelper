using System;
using System.Numerics;
using BOCCHI.Data;
using BOCCHI.Enums;
using Dalamud.Game.ClientState.Fates;

namespace BOCCHI.Modules.Fates;

public class Fate
{
    private readonly IFate fate;

    public readonly EventData Data;

    public Fate(IFate fate)
    {
        this.fate = fate;

        var id = GetFateId(fate);
        if (EventData.Fates.TryGetValue(id, out var knownData))
        {
            Data = knownData;
            return;
        }

        Data = new EventData
        {
            Id = id,
            Type = EventType.Fate,
            InternalName = GetFateName(fate),
            StartPosition = GetFatePosition(fate),
            Radius = GetFateRadius(fate),
        };
    }

    public uint Id
    {
        get
        {
            try
            {
                return fate.FateId;
            }
            catch (AccessViolationException)
            {
                return 0;
            }
        }
    }

    public string Name
    {
        get
        {
            try
            {
                return fate.Name.ToString();
            }
            catch (AccessViolationException)
            {
                return "Unknown Fate";
            }
        }
    }

    public float Radius
    {
        get
        {
            try
            {
                return Data.Radius ?? fate.Radius;
            }
            catch (AccessViolationException)
            {
                return 0f;
            }
        }
    }

    public Vector3 StartPosition
    {
        get
        {
            try
            {
                return Data.StartPosition ?? fate.Position;
            }
            catch (AccessViolationException)
            {
                return Vector3.Zero;
            }
        }
    }

    public readonly EventProgress Progress = new();

    public byte CurrentProgress
    {
        get
        {
            try
            {
                return fate.Progress;
            }
            catch (AccessViolationException)
            {
                return 100;
            }
        }
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

    private static uint GetFateId(IFate fate)
    {
        try
        {
            return fate.FateId;
        }
        catch (AccessViolationException)
        {
            return 0;
        }
    }

    private static string GetFateName(IFate fate)
    {
        try
        {
            return fate.Name.ToString();
        }
        catch (AccessViolationException)
        {
            return "Unknown Fate";
        }
    }

    private static Vector3 GetFatePosition(IFate fate)
    {
        try
        {
            return fate.Position;
        }
        catch (AccessViolationException)
        {
            return Vector3.Zero;
        }
    }

    private static float GetFateRadius(IFate fate)
    {
        try
        {
            return fate.Radius;
        }
        catch (AccessViolationException)
        {
            return 0f;
        }
    }
}
