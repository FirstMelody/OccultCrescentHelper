public sealed record TelemetryBatch(
    int SchemaVersion,
    string? PluginVersion,
    List<TelemetryMarker>? Markers
);

public sealed record TelemetryMarker(
    string Source,
    string Kind,
    uint TerritoryId,
    uint MapId,
    uint? BaseId,
    uint? EventId,
    uint? Level,
    string? Name,
    double X,
    double Y,
    double Z,
    double? HitboxRadius,
    double? MechanicRadius
);

public sealed record PublicTelemetryMarker(
    string Source,
    string Kind,
    long TerritoryId,
    long MapId,
    long? BaseId,
    long? EventId,
    long? Level,
    string? Name,
    double X,
    double Y,
    double Z,
    double? HitboxRadius,
    double? MechanicRadius,
    DateTimeOffset LastSeenUtc,
    long ReportCount
);

public sealed record TelemetryKindCount(string Kind, long Count);

public sealed record TelemetryStats(
    long UniqueMarkers,
    long TotalReports,
    IReadOnlyList<TelemetryKindCount> Kinds
);

public sealed record UploadResult(int StatusCode, object Body);
