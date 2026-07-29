using System;
using System.Collections.Generic;
using System.Linq;
using ECommons.DalamudServices;
using Ocelot.Modules;

namespace BOCCHI.Modules.Fates;

public class FateTracker
{
    public readonly Dictionary<uint, Fate> Fates = [];

    public event Action<Fate>? OnFateSpawned;

    public event Action<Fate>? OnFateDespawned;


    public void Update(UpdateContext context)
    {
        Update();
    }

    public void Update()
    {
        var currentFates = Svc.Fates.ToDictionary(f => (uint)f.FateId, f => f);

        foreach (var (id, data) in currentFates)
        {
            if (Fates.TryGetValue(id, out var fate))
            {
                fate.Refresh(data);
                continue;
            }

            fate = new Fate(data);
            Fates[id] = fate;
            OnFateSpawned?.Invoke(fate);
        }

        var despawned = Fates.Keys.Except(currentFates.Keys).ToList();
        foreach (var id in despawned)
        {
            if (Fates.Remove(id, out var fate))
            {
                OnFateDespawned?.Invoke(fate);
            }
        }

        foreach (var fate in Fates.Values)
        {
            fate.Update();
        }
    }
}
