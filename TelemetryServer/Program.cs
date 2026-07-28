using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(
        "uploads",
        context => RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 12,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            }
        )
    );
});

var app = builder.Build();
var dataDirectory = Environment.GetEnvironmentVariable("BOCCHI_DATA_DIR") ?? "/data";
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "telemetry.db");
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared,
}.ToString();

await InitializeDatabase(connectionString);

app.UseForwardedHeaders(
    new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    }
);
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost(
        "/api/v1/markers",
        async (TelemetryBatch batch) =>
        {
            if (batch.SchemaVersion != 1)
            {
                return Results.BadRequest(new { error = "unsupported schemaVersion" });
            }

            if (batch.Markers is not { Count: > 0 and <= 1000 })
            {
                return Results.BadRequest(new { error = "markers must contain 1-1000 items" });
            }

            var accepted = 0;
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            foreach (var marker in batch.Markers)
            {
                if (!TryNormalize(marker, out var normalized))
                {
                    continue;
                }

                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    """
                    INSERT INTO markers (
                        source, kind, territory_id, map_id, base_id, event_id, name,
                        x, y, z, hitbox_radius, mechanic_radius,
                        first_seen_utc, last_seen_utc, report_count
                    )
                    VALUES (
                        $source, $kind, $territory, $map, $base, $event, $name,
                        $x, $y, $z, $hitbox, $mechanic,
                        unixepoch(), unixepoch(), 1
                    )
                    ON CONFLICT (
                        source, kind, territory_id, map_id, base_id_key, event_id_key, x, y, z
                    )
                    DO UPDATE SET
                        name = COALESCE(excluded.name, markers.name),
                        hitbox_radius = COALESCE(excluded.hitbox_radius, markers.hitbox_radius),
                        mechanic_radius = COALESCE(excluded.mechanic_radius, markers.mechanic_radius),
                        last_seen_utc = unixepoch(),
                        report_count = markers.report_count + 1;
                    """;
                command.Parameters.AddWithValue("$source", normalized.Source);
                command.Parameters.AddWithValue("$kind", normalized.Kind);
                command.Parameters.AddWithValue("$territory", normalized.TerritoryId);
                command.Parameters.AddWithValue("$map", normalized.MapId);
                command.Parameters.AddWithValue(
                    "$base",
                    normalized.BaseId is { } baseId ? baseId : DBNull.Value
                );
                command.Parameters.AddWithValue(
                    "$event",
                    normalized.EventId is { } eventId ? eventId : DBNull.Value
                );
                command.Parameters.AddWithValue(
                    "$name",
                    normalized.Name is { } name ? name : DBNull.Value
                );
                command.Parameters.AddWithValue("$x", normalized.X);
                command.Parameters.AddWithValue("$y", normalized.Y);
                command.Parameters.AddWithValue("$z", normalized.Z);
                command.Parameters.AddWithValue(
                    "$hitbox",
                    normalized.HitboxRadius is { } hitbox ? hitbox : DBNull.Value
                );
                command.Parameters.AddWithValue(
                    "$mechanic",
                    normalized.MechanicRadius is { } mechanic ? mechanic : DBNull.Value
                );
                await command.ExecuteNonQueryAsync();
                accepted++;
            }

            await transaction.CommitAsync();
            return Results.Ok(new { accepted });
        }
    )
    .RequireRateLimiting("uploads");

app.MapGet(
    "/api/v1/stats",
    async () =>
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        var total = await ScalarLong(
            connection,
            "SELECT COUNT(*) FROM markers;"
        );
        var reports = await ScalarLong(
            connection,
            "SELECT COALESCE(SUM(report_count), 0) FROM markers;"
        );

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT kind, COUNT(*) AS count
            FROM markers
            GROUP BY kind
            ORDER BY count DESC, kind
            LIMIT 30;
            """;
        var kinds = new List<object>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            kinds.Add(new { kind = reader.GetString(0), count = reader.GetInt64(1) });
        }

        return Results.Ok(new { uniqueMarkers = total, totalReports = reports, kinds });
    }
);

app.MapGet(
    "/api/v1/markers",
    async (uint? territoryId, uint? mapId, int? limit) =>
    {
        var take = Math.Clamp(limit ?? 500, 1, 1000);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source, kind, territory_id, map_id, base_id, event_id, name,
                   x, y, z, hitbox_radius, mechanic_radius, last_seen_utc, report_count
            FROM markers
            WHERE ($territory IS NULL OR territory_id = $territory)
              AND ($map IS NULL OR map_id = $map)
            ORDER BY last_seen_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue(
            "$territory",
            territoryId is { } territory ? territory : DBNull.Value
        );
        command.Parameters.AddWithValue("$map", mapId is { } map ? map : DBNull.Value);
        command.Parameters.AddWithValue("$limit", take);

        var markers = new List<object>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            markers.Add(
                new
                {
                    source = reader.GetString(0),
                    kind = reader.GetString(1),
                    territoryId = reader.GetInt64(2),
                    mapId = reader.GetInt64(3),
                    baseId = reader.IsDBNull(4) ? null : (long?)reader.GetInt64(4),
                    eventId = reader.IsDBNull(5) ? null : (long?)reader.GetInt64(5),
                    name = reader.IsDBNull(6) ? null : reader.GetString(6),
                    x = reader.GetDouble(7),
                    y = reader.GetDouble(8),
                    z = reader.GetDouble(9),
                    hitboxRadius = reader.IsDBNull(10) ? null : (double?)reader.GetDouble(10),
                    mechanicRadius = reader.IsDBNull(11)
                        ? null
                        : (double?)reader.GetDouble(11),
                    lastSeenUtc = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)),
                    reportCount = reader.GetInt64(13),
                }
            );
        }

        return Results.Ok(new { markers });
    }
);

app.Run();

static async Task InitializeDatabase(string connectionString)
{
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = 5000;
        CREATE TABLE IF NOT EXISTS markers (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            source TEXT NOT NULL,
            kind TEXT NOT NULL,
            territory_id INTEGER NOT NULL,
            map_id INTEGER NOT NULL,
            base_id INTEGER,
            event_id INTEGER,
            base_id_key INTEGER GENERATED ALWAYS AS (COALESCE(base_id, -1)) STORED,
            event_id_key INTEGER GENERATED ALWAYS AS (COALESCE(event_id, -1)) STORED,
            name TEXT,
            x REAL NOT NULL,
            y REAL NOT NULL,
            z REAL NOT NULL,
            hitbox_radius REAL,
            mechanic_radius REAL,
            first_seen_utc INTEGER NOT NULL,
            last_seen_utc INTEGER NOT NULL,
            report_count INTEGER NOT NULL DEFAULT 1,
            UNIQUE (
                source, kind, territory_id, map_id, base_id_key, event_id_key, x, y, z
            )
        );
        CREATE INDEX IF NOT EXISTS idx_markers_map
            ON markers (territory_id, map_id, kind);
        CREATE INDEX IF NOT EXISTS idx_markers_recent
            ON markers (last_seen_utc DESC);
        """;
    await command.ExecuteNonQueryAsync();
}

static async Task<long> ScalarLong(SqliteConnection connection, string sql)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
}

static bool TryNormalize(TelemetryMarker marker, out TelemetryMarker normalized)
{
    normalized = marker;
    if (marker.TerritoryId == 0
        || marker.MapId == 0
        || string.IsNullOrWhiteSpace(marker.Source)
        || string.IsNullOrWhiteSpace(marker.Kind)
        || !double.IsFinite(marker.X)
        || !double.IsFinite(marker.Y)
        || !double.IsFinite(marker.Z)
        || Math.Abs(marker.X) > 100_000
        || Math.Abs(marker.Y) > 100_000
        || Math.Abs(marker.Z) > 100_000)
    {
        return false;
    }

    normalized = marker with
    {
        Source = marker.Source.Trim()[..Math.Min(marker.Source.Trim().Length, 40)],
        Kind = marker.Kind.Trim()[..Math.Min(marker.Kind.Trim().Length, 60)],
        Name = string.IsNullOrWhiteSpace(marker.Name)
            ? null
            : marker.Name.Trim()[..Math.Min(marker.Name.Trim().Length, 160)],
        X = Math.Round(marker.X, 3),
        Y = Math.Round(marker.Y, 3),
        Z = Math.Round(marker.Z, 3),
        HitboxRadius = NormalizeRadius(marker.HitboxRadius),
        MechanicRadius = NormalizeRadius(marker.MechanicRadius),
    };
    return true;
}

static double? NormalizeRadius(double? value)
{
    return value is { } radius && double.IsFinite(radius) && radius >= 0 && radius <= 1000
        ? Math.Round(radius, 3)
        : null;
}

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
    string? Name,
    double X,
    double Y,
    double Z,
    double? HitboxRadius,
    double? MechanicRadius
);
