using System.Collections.Generic;

namespace BOCCHI.Modules.DevMap;

public class DevMapMarkerFile
{
    public int Version { get; set; } = 3;

    public List<DevMapMarker> Markers { get; set; } = [];
}
