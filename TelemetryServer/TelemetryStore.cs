using System.Globalization;
using Microsoft.Data.Sqlite;

public sealed class TelemetryStore
{
    public const string LegacyUploaderHash = "LEGACY-TRUSTED-IMPORT";
    private const int MaxPluginVersionLength = 40;
    private const int MaxNameLength = 160;
    private const double MaxCoordinateMagnitude = 2048;
    private const double MaxHitboxRadius = 100;
    private const double MonsterRegionRadius = 250;

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedKinds =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["dev-map"] =
            [
                "SilverChest",
                "BronzeChest",
                "FortuneCarrot",
                "PotChest",
                "Fate",
                "CriticalEncounter",
                "InvestigationLocation",
                "UnknownChest",
            ],
            ["tower-eventobj"] = ["Unknown", "SmallTrap", "BigTrap"],
            ["monster"] = ["Monster"],
        };

    private static readonly HashSet<uint> SouthTowerMaps =
    [
        968,
        969,
        970,
        971,
        976,
        982,
        984,
        985,
        986,
        988,
        989,
        990,
    ];

    private static readonly HashSet<uint> NorthTowerMaps =
    [
        1135,
        1136,
        1178,
        1179,
        1180,
        1181,
        1182,
        1183,
        1184,
        1185,
        1186,
        1187,
        1188,
        1189,
        1190,
        1191,
    ];

    private readonly string connectionString;
    private readonly string databasePath;
    private readonly int minimumReporters;
    private readonly int dailyUniqueMarkerLimit;
    private readonly string authoritativeUploaderHash;

    public TelemetryStore(
        string databasePath,
        int minimumReporters,
        int dailyUniqueMarkerLimit,
        string authoritativeUploaderHash = ""
    )
    {
        this.databasePath = databasePath;
        this.minimumReporters = minimumReporters;
        this.dailyUniqueMarkerLimit = dailyUniqueMarkerLimit;
        this.authoritativeUploaderHash = authoritativeUploaderHash;
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText =
                """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;
                """;
            await pragmaCommand.ExecuteNonQueryAsync();
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS markers (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL,
                kind TEXT NOT NULL,
                territory_id INTEGER NOT NULL,
                map_id INTEGER NOT NULL,
                base_id INTEGER,
                event_id INTEGER,
                level INTEGER,
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

            CREATE TABLE IF NOT EXISTS server_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS upload_batches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                uploader_hash TEXT NOT NULL,
                received_utc INTEGER NOT NULL,
                plugin_version TEXT,
                submitted_count INTEGER NOT NULL,
                accepted_count INTEGER NOT NULL,
                rejected_count INTEGER NOT NULL,
                status TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_upload_batches_uploader
                ON upload_batches (uploader_hash, received_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_upload_batches_recent
                ON upload_batches (received_utc DESC);

            CREATE TABLE IF NOT EXISTS marker_reports (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                uploader_hash TEXT NOT NULL,
                source TEXT NOT NULL,
                kind TEXT NOT NULL,
                territory_id INTEGER NOT NULL,
                map_id INTEGER NOT NULL,
                base_id INTEGER,
                event_id INTEGER,
                level INTEGER,
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
                    uploader_hash, source, kind, territory_id, map_id,
                    base_id_key, event_id_key, x, y, z
                )
            );
            CREATE INDEX IF NOT EXISTS idx_marker_reports_map
                ON marker_reports (territory_id, map_id, kind);
            CREATE INDEX IF NOT EXISTS idx_marker_reports_uploader
                ON marker_reports (uploader_hash, last_seen_utc DESC);
            CREATE INDEX IF NOT EXISTS idx_marker_reports_recent
                ON marker_reports (last_seen_utc DESC);
            """;
        await command.ExecuteNonQueryAsync();
        await EnsureColumnAsync(connection, transaction, "markers", "level", "INTEGER");
        await EnsureColumnAsync(connection, transaction, "marker_reports", "level", "INTEGER");

        command.CommandText =
            """
            INSERT INTO marker_reports (
                uploader_hash, source, kind, territory_id, map_id, base_id, event_id, level, name,
                x, y, z, hitbox_radius, mechanic_radius,
                first_seen_utc, last_seen_utc, report_count
            )
            SELECT
                $legacy, source, kind, territory_id, map_id, base_id, event_id, level, name,
                x, y, z, hitbox_radius, mechanic_radius,
                first_seen_utc, last_seen_utc, report_count
            FROM markers
            WHERE NOT EXISTS (
                SELECT 1 FROM server_meta WHERE key = 'legacy_marker_import_v1'
            );

            INSERT OR IGNORE INTO server_meta (key, value)
            VALUES ('legacy_marker_import_v1', unixepoch());
            """;
        command.Parameters.AddWithValue("$legacy", LegacyUploaderHash);
        await command.ExecuteNonQueryAsync();

        // These IDs have the same mechanic in both South and North. Early North
        // clients uploaded them as Unknown, so normalize both historical rows and
        // future uploads on the server instead of waiting for every client to
        // revisit the exact object.
        command.Parameters.Clear();
        command.CommandText =
            """
            DELETE FROM marker_reports AS stale
            WHERE stale.source = 'tower-eventobj'
              AND stale.base_id IN (2014584, 2014585)
              AND stale.kind <> CASE stale.base_id
                  WHEN 2014584 THEN 'SmallTrap'
                  ELSE 'BigTrap'
              END
              AND EXISTS (
                  SELECT 1
                  FROM marker_reports AS canonical
                  WHERE canonical.uploader_hash = stale.uploader_hash
                    AND canonical.source = stale.source
                    AND canonical.kind = CASE stale.base_id
                        WHEN 2014584 THEN 'SmallTrap'
                        ELSE 'BigTrap'
                    END
                    AND canonical.territory_id = stale.territory_id
                    AND canonical.map_id = stale.map_id
                    AND canonical.base_id_key = stale.base_id_key
                    AND canonical.event_id_key = stale.event_id_key
                    AND canonical.x = stale.x
                    AND canonical.y = stale.y
                    AND canonical.z = stale.z
              );

            UPDATE marker_reports
            SET kind = CASE base_id
                    WHEN 2014584 THEN 'SmallTrap'
                    ELSE 'BigTrap'
                END,
                mechanic_radius = CASE base_id
                    WHEN 2014584 THEN 7
                    ELSE 30
                END
            WHERE source = 'tower-eventobj'
              AND base_id IN (2014584, 2014585);

            DELETE FROM markers AS stale
            WHERE stale.source = 'tower-eventobj'
              AND stale.base_id IN (2014584, 2014585)
              AND stale.kind <> CASE stale.base_id
                  WHEN 2014584 THEN 'SmallTrap'
                  ELSE 'BigTrap'
              END
              AND EXISTS (
                  SELECT 1
                  FROM markers AS canonical
                  WHERE canonical.source = stale.source
                    AND canonical.kind = CASE stale.base_id
                        WHEN 2014584 THEN 'SmallTrap'
                        ELSE 'BigTrap'
                    END
                    AND canonical.territory_id = stale.territory_id
                    AND canonical.map_id = stale.map_id
                    AND canonical.base_id_key = stale.base_id_key
                    AND canonical.event_id_key = stale.event_id_key
                    AND canonical.x = stale.x
                    AND canonical.y = stale.y
                    AND canonical.z = stale.z
              );

            UPDATE markers
            SET kind = CASE base_id
                    WHEN 2014584 THEN 'SmallTrap'
                    ELSE 'BigTrap'
                END,
                mechanic_radius = CASE base_id
                    WHEN 2014584 THEN 7
                    ELSE 30
                END
            WHERE source = 'tower-eventobj'
              AND base_id IN (2014584, 2014585);
            """;
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    public async Task<UploadResult> AcceptBatchAsync(string uploaderHash, TelemetryBatch batch)
    {
        var submitted = batch.Markers?.Count ?? 0;
        if (batch.SchemaVersion != 1)
        {
            await RecordBatchAsync(
                uploaderHash,
                batch.PluginVersion,
                submitted,
                0,
                submitted,
                "rejected-schema"
            );
            return new UploadResult(
                StatusCodes.Status400BadRequest,
                new { error = "unsupported schemaVersion" }
            );
        }

        if (batch.Markers is not { Count: > 0 and <= 5000 })
        {
            await RecordBatchAsync(
                uploaderHash,
                batch.PluginVersion,
                submitted,
                0,
                submitted,
                "rejected-count"
            );
            return new UploadResult(
                StatusCodes.Status400BadRequest,
                new { error = "markers must contain 1-5000 items" }
            );
        }

        var accepted = 0;
        var rejected = 0;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var newToday = await CountNewReportsTodayAsync(connection, transaction, uploaderHash);

        foreach (var marker in batch.Markers)
        {
            if (!TryNormalize(marker, out var normalized))
            {
                rejected++;
                continue;
            }

            if (normalized.Source == "monster")
            {
                normalized = await ResolveMonsterRegionAsync(
                    connection,
                    transaction,
                    normalized
                );
            }

            var exists = await ReportExistsAsync(connection, transaction, uploaderHash, normalized);
            if (!exists && newToday >= dailyUniqueMarkerLimit)
            {
                rejected++;
                continue;
            }

            await UpsertReportAsync(connection, transaction, uploaderHash, normalized);
            if (!exists)
            {
                newToday++;
            }

            accepted++;
        }

        await InsertBatchAsync(
            connection,
            transaction,
            uploaderHash,
            batch.PluginVersion,
            submitted,
            accepted,
            rejected,
            accepted > 0
                ? rejected > 0 ? "partial" : "accepted"
                : "rejected-validation"
        );
        await transaction.CommitAsync();

        return new UploadResult(
            accepted > 0
                ? StatusCodes.Status200OK
                : StatusCodes.Status422UnprocessableEntity,
            new
            {
                accepted,
                rejected,
                pendingConfirmation = minimumReporters,
            }
        );
    }

    public async Task<IReadOnlyList<PublicTelemetryMarker>> GetMarkersAsync(
        uint? territoryId,
        uint? mapId,
        int limit
    )
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH grouped AS (
                SELECT
                    source, kind, territory_id, map_id,
                    base_id_key, event_id_key, x, y, z,
                    MIN(id) AS first_report_id,
                    MIN(
                        CASE
                            WHEN uploader_hash = $legacy
                              OR uploader_hash = $authoritative
                            THEN id
                        END
                    ) AS trusted_report_id,
                    MAX(last_seen_utc) AS last_seen_utc,
                    COUNT(*) AS reporter_count
                FROM marker_reports
                GROUP BY
                    source, kind, territory_id, map_id,
                    base_id_key, event_id_key, x, y, z
                HAVING
                    source = 'monster'
                    OR MAX(
                        CASE
                            WHEN uploader_hash = $legacy
                              OR uploader_hash = $authoritative
                            THEN 1
                            ELSE 0
                        END
                    ) = 1
                    OR COUNT(*) >= $minimumReporters
            ),
            eligible AS (
                SELECT
                    COALESCE(trusted_report_id, first_report_id) AS representative_id,
                    last_seen_utc,
                    reporter_count,
                    source,
                    kind,
                    territory_id,
                    map_id,
                    base_id_key,
                    event_id_key,
                    x,
                    y,
                    z
                FROM grouped
            )
            SELECT
                report.source, report.kind, report.territory_id, report.map_id,
                report.base_id, report.event_id, report.level, report.name,
                report.x, report.y, report.z,
                report.hitbox_radius, report.mechanic_radius,
                eligible.last_seen_utc, eligible.reporter_count
            FROM eligible
            JOIN marker_reports AS report ON report.id = eligible.representative_id
            WHERE ($territory IS NULL OR eligible.territory_id = $territory)
              AND ($map IS NULL OR eligible.map_id = $map)
              AND NOT (
                  report.kind IN ('BronzeChest', 'SilverChest')
                  AND EXISTS (
                      SELECT 1
                      FROM eligible AS pot
                      WHERE pot.source = eligible.source
                        AND pot.kind = 'PotChest'
                        AND pot.territory_id = eligible.territory_id
                        AND pot.map_id = eligible.map_id
                        AND pot.base_id_key = eligible.base_id_key
                        AND pot.event_id_key = eligible.event_id_key
                        AND pot.x = eligible.x
                        AND pot.y = eligible.y
                        AND pot.z = eligible.z
                  )
              )
            ORDER BY eligible.last_seen_utc DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$legacy", LegacyUploaderHash);
        command.Parameters.AddWithValue("$authoritative", authoritativeUploaderHash);
        command.Parameters.AddWithValue("$minimumReporters", minimumReporters);
        command.Parameters.AddWithValue(
            "$territory",
            territoryId is { } territory ? territory : DBNull.Value
        );
        command.Parameters.AddWithValue("$map", mapId is { } map ? map : DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        var markers = new List<PublicTelemetryMarker>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            markers.Add(
                new PublicTelemetryMarker(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetDouble(8),
                    reader.GetDouble(9),
                    reader.GetDouble(10),
                    reader.IsDBNull(11) ? null : reader.GetDouble(11),
                    reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(13)),
                    reader.GetInt64(14)
                )
            );
        }

        return markers;
    }

    public async Task<TelemetryStats> GetStatsAsync(uint? territoryId, uint? mapId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        const string eligibleCte =
            """
            WITH eligible AS (
                SELECT kind, COUNT(*) AS reporter_count
                FROM marker_reports
                WHERE ($territory IS NULL OR territory_id = $territory)
                  AND ($map IS NULL OR map_id = $map)
                GROUP BY
                    source, kind, territory_id, map_id,
                    base_id_key, event_id_key, x, y, z
                HAVING
                    source = 'monster'
                    OR MAX(
                        CASE
                            WHEN uploader_hash = $legacy
                              OR uploader_hash = $authoritative
                            THEN 1
                            ELSE 0
                        END
                    ) = 1
                    OR COUNT(*) >= $minimumReporters
            )
            """;

        await using var totalsCommand = connection.CreateCommand();
        totalsCommand.CommandText =
            eligibleCte
            + """
            SELECT COUNT(*), COALESCE(SUM(reporter_count), 0)
            FROM eligible;
            """;
        totalsCommand.Parameters.AddWithValue("$legacy", LegacyUploaderHash);
        totalsCommand.Parameters.AddWithValue("$authoritative", authoritativeUploaderHash);
        totalsCommand.Parameters.AddWithValue("$minimumReporters", minimumReporters);
        totalsCommand.Parameters.AddWithValue(
            "$territory",
            territoryId is { } territoryForTotals ? territoryForTotals : DBNull.Value
        );
        totalsCommand.Parameters.AddWithValue(
            "$map",
            mapId is { } mapForTotals ? mapForTotals : DBNull.Value
        );
        long uniqueMarkers;
        long totalReports;
        await using (var reader = await totalsCommand.ExecuteReaderAsync())
        {
            await reader.ReadAsync();
            uniqueMarkers = reader.GetInt64(0);
            totalReports = reader.GetInt64(1);
        }

        await using var kindsCommand = connection.CreateCommand();
        kindsCommand.CommandText =
            eligibleCte
            + """
            SELECT kind, COUNT(*) AS count
            FROM eligible
            GROUP BY kind
            ORDER BY count DESC, kind
            LIMIT 30;
            """;
        kindsCommand.Parameters.AddWithValue("$legacy", LegacyUploaderHash);
        kindsCommand.Parameters.AddWithValue("$authoritative", authoritativeUploaderHash);
        kindsCommand.Parameters.AddWithValue("$minimumReporters", minimumReporters);
        kindsCommand.Parameters.AddWithValue(
            "$territory",
            territoryId is { } territoryForKinds ? territoryForKinds : DBNull.Value
        );
        kindsCommand.Parameters.AddWithValue(
            "$map",
            mapId is { } mapForKinds ? mapForKinds : DBNull.Value
        );
        var kinds = new List<TelemetryKindCount>();
        await using var kindsReader = await kindsCommand.ExecuteReaderAsync();
        while (await kindsReader.ReadAsync())
        {
            kinds.Add(new TelemetryKindCount(kindsReader.GetString(0), kindsReader.GetInt64(1)));
        }

        return new TelemetryStats(uniqueMarkers, totalReports, kinds);
    }

    public async Task<IReadOnlyList<UploaderSummary>> ListUploadersAsync(int limit)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                batches.uploader_hash,
                MIN(batches.received_utc),
                MAX(batches.received_utc),
                COUNT(*),
                SUM(batches.accepted_count),
                SUM(batches.rejected_count),
                COALESCE(reports.unique_reports, 0)
            FROM upload_batches AS batches
            LEFT JOIN (
                SELECT uploader_hash, COUNT(*) AS unique_reports
                FROM marker_reports
                GROUP BY uploader_hash
            ) AS reports ON reports.uploader_hash = batches.uploader_hash
            GROUP BY batches.uploader_hash
            ORDER BY MAX(batches.received_utc) DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        var summaries = new List<UploaderSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            summaries.Add(
                new UploaderSummary(
                    reader.GetString(0),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1)),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6)
                )
            );
        }

        return summaries;
    }

    public async Task<IReadOnlyList<UploaderMarkerSummary>> InspectUploaderAsync(
        string uploaderHash
    )
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                source, kind, territory_id, map_id, COUNT(*),
                MIN(x), MAX(x), MIN(y), MAX(y), MIN(z), MAX(z)
            FROM marker_reports
            WHERE uploader_hash = $hash
            GROUP BY source, kind, territory_id, map_id
            ORDER BY territory_id, map_id, source, kind;
            """;
        command.Parameters.AddWithValue("$hash", uploaderHash);
        var summaries = new List<UploaderMarkerSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            summaries.Add(
                new UploaderMarkerSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.GetInt64(4),
                    reader.GetDouble(5),
                    reader.GetDouble(6),
                    reader.GetDouble(7),
                    reader.GetDouble(8),
                    reader.GetDouble(9),
                    reader.GetDouble(10)
                )
            );
        }

        return summaries;
    }

    public async Task<IReadOnlyList<MarkerContributor>> FindMarkerContributorsAsync(
        long territoryId,
        long mapId,
        double x,
        double y,
        double z,
        double radius
    )
    {
        radius = Math.Clamp(radius, 0.01, 50);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                uploader_hash, source, kind, x, y, z,
                first_seen_utc, last_seen_utc, report_count
            FROM marker_reports
            WHERE territory_id = $territory
              AND map_id = $map
              AND ((x - $x) * (x - $x)
                   + (y - $y) * (y - $y)
                   + (z - $z) * (z - $z)) <= ($radius * $radius)
            ORDER BY last_seen_utc DESC, uploader_hash;
            """;
        command.Parameters.AddWithValue("$territory", territoryId);
        command.Parameters.AddWithValue("$map", mapId);
        command.Parameters.AddWithValue("$x", x);
        command.Parameters.AddWithValue("$y", y);
        command.Parameters.AddWithValue("$z", z);
        command.Parameters.AddWithValue("$radius", radius);
        var contributors = new List<MarkerContributor>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            contributors.Add(
                new MarkerContributor(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(6)),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(7)),
                    reader.GetInt64(8)
                )
            );
        }

        return contributors;
    }

    public async Task<DeleteUploaderResult> DeleteUploaderAsync(string uploaderHash)
    {
        if (string.Equals(uploaderHash, LegacyUploaderHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The trusted legacy import cannot be deleted.");
        }

        var backupPath =
            databasePath
            + ".before-delete-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + ".bak";
        await using (var source = new SqliteConnection(connectionString))
        await using (var destination = new SqliteConnection($"Data Source={backupPath}"))
        {
            await source.OpenAsync();
            await destination.OpenAsync();
            source.BackupDatabase(destination);
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var reports = await DeleteByHashAsync(
            connection,
            transaction,
            "marker_reports",
            uploaderHash
        );
        var batches = await DeleteByHashAsync(
            connection,
            transaction,
            "upload_batches",
            uploaderHash
        );
        await transaction.CommitAsync();
        return new DeleteUploaderResult(uploaderHash, reports, batches, backupPath);
    }

    private async Task RecordBatchAsync(
        string uploaderHash,
        string? pluginVersion,
        int submitted,
        int accepted,
        int rejected,
        string status
    )
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertBatchAsync(
            connection,
            transaction,
            uploaderHash,
            pluginVersion,
            submitted,
            accepted,
            rejected,
            status
        );
        await transaction.CommitAsync();
    }

    private static async Task InsertBatchAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string uploaderHash,
        string? pluginVersion,
        int submitted,
        int accepted,
        int rejected,
        string status
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO upload_batches (
                uploader_hash, received_utc, plugin_version,
                submitted_count, accepted_count, rejected_count, status
            )
            VALUES (
                $hash, unixepoch(), $version,
                $submitted, $accepted, $rejected, $status
            );
            """;
        command.Parameters.AddWithValue("$hash", uploaderHash);
        command.Parameters.AddWithValue(
            "$version",
            NormalizeOptionalString(pluginVersion, MaxPluginVersionLength) is { } version
                ? version
                : DBNull.Value
        );
        command.Parameters.AddWithValue("$submitted", submitted);
        command.Parameters.AddWithValue("$accepted", accepted);
        command.Parameters.AddWithValue("$rejected", rejected);
        command.Parameters.AddWithValue("$status", status);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountNewReportsTodayAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string uploaderHash
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM marker_reports
            WHERE uploader_hash = $hash
              AND first_seen_utc >= unixepoch('now', 'start of day');
            """;
        command.Parameters.AddWithValue("$hash", uploaderHash);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> ReportExistsAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string uploaderHash,
        TelemetryMarker marker
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT EXISTS (
                SELECT 1
                FROM marker_reports
                WHERE uploader_hash = $hash
                  AND source = $source
                  AND kind = $kind
                  AND territory_id = $territory
                  AND map_id = $map
                  AND base_id_key = COALESCE($base, -1)
                  AND event_id_key = COALESCE($event, -1)
                  AND x = $x AND y = $y AND z = $z
            );
            """;
        AddMarkerIdentityParameters(command, uploaderHash, marker);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<TelemetryMarker> ResolveMonsterRegionAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        TelemetryMarker marker
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            SELECT x, y, z
            FROM marker_reports
            WHERE source = 'monster'
              AND kind = 'Monster'
              AND territory_id = $territory
              AND map_id = $map
              AND base_id_key = COALESCE($base, -1)
              AND ((x - $x) * (x - $x) + (z - $z) * (z - $z))
                  <= ($radius * $radius)
            ORDER BY ((x - $x) * (x - $x) + (z - $z) * (z - $z)), id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$territory", marker.TerritoryId);
        command.Parameters.AddWithValue("$map", marker.MapId);
        command.Parameters.AddWithValue(
            "$base",
            marker.BaseId is { } baseId ? baseId : DBNull.Value
        );
        command.Parameters.AddWithValue("$x", marker.X);
        command.Parameters.AddWithValue("$z", marker.Z);
        command.Parameters.AddWithValue("$radius", MonsterRegionRadius);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return marker;
        }

        return marker with
        {
            X = reader.GetDouble(0),
            Y = reader.GetDouble(1),
            Z = reader.GetDouble(2),
        };
    }

    private static async Task UpsertReportAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string uploaderHash,
        TelemetryMarker marker
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText =
            """
            INSERT INTO marker_reports (
                uploader_hash, source, kind, territory_id, map_id,
                base_id, event_id, level, name,
                x, y, z, hitbox_radius, mechanic_radius,
                first_seen_utc, last_seen_utc, report_count
            )
            VALUES (
                $hash, $source, $kind, $territory, $map, $base, $event, $level, $name,
                $x, $y, $z, $hitbox, $mechanic,
                unixepoch(), unixepoch(), 1
            )
            ON CONFLICT (
                uploader_hash, source, kind, territory_id, map_id,
                base_id_key, event_id_key, x, y, z
            )
            DO UPDATE SET
                name = COALESCE(marker_reports.name, excluded.name),
                level = COALESCE(marker_reports.level, excluded.level),
                hitbox_radius = COALESCE(marker_reports.hitbox_radius, excluded.hitbox_radius),
                mechanic_radius = COALESCE(
                    marker_reports.mechanic_radius,
                    excluded.mechanic_radius
                ),
                last_seen_utc = unixepoch(),
                report_count = marker_reports.report_count + 1;
            """;
        AddMarkerIdentityParameters(command, uploaderHash, marker);
        command.Parameters.AddWithValue(
            "$name",
            marker.Name is { } name ? name : DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$level",
            marker.Level is { } level ? level : DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$hitbox",
            marker.HitboxRadius is { } hitbox ? hitbox : DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$mechanic",
            marker.MechanicRadius is { } mechanic ? mechanic : DBNull.Value
        );
        await command.ExecuteNonQueryAsync();
    }

    private static void AddMarkerIdentityParameters(
        SqliteCommand command,
        string uploaderHash,
        TelemetryMarker marker
    )
    {
        command.Parameters.AddWithValue("$hash", uploaderHash);
        command.Parameters.AddWithValue("$source", marker.Source);
        command.Parameters.AddWithValue("$kind", marker.Kind);
        command.Parameters.AddWithValue("$territory", marker.TerritoryId);
        command.Parameters.AddWithValue("$map", marker.MapId);
        command.Parameters.AddWithValue(
            "$base",
            marker.BaseId is { } baseId ? baseId : DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$event",
            marker.EventId is { } eventId ? eventId : DBNull.Value
        );
        command.Parameters.AddWithValue("$x", marker.X);
        command.Parameters.AddWithValue("$y", marker.Y);
        command.Parameters.AddWithValue("$z", marker.Z);
    }

    private static async Task<int> DeleteByHashAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string table,
        string uploaderHash
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = $"DELETE FROM {table} WHERE uploader_hash = $hash;";
        command.Parameters.AddWithValue("$hash", uploaderHash);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string table,
        string column,
        string definition
    )
    {
        await using var inspect = connection.CreateCommand();
        inspect.Transaction = (SqliteTransaction)transaction;
        inspect.CommandText = $"PRAGMA table_info({table});";
        await using var reader = await inspect.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using var alter = connection.CreateCommand();
        alter.Transaction = (SqliteTransaction)transaction;
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync();
    }

    private static bool TryNormalize(TelemetryMarker marker, out TelemetryMarker normalized)
    {
        normalized = marker;
        var source = marker.Source?.Trim();
        var kind = marker.Kind?.Trim();
        if (kind == "FortuneCarrotChest")
        {
            kind = "FortuneCarrot";
        }

        if (source == "tower-eventobj")
        {
            kind = marker.BaseId switch
            {
                2014584 => "SmallTrap",
                2014585 => "BigTrap",
                _ => kind,
            };
        }

        if (string.IsNullOrWhiteSpace(source)
            || string.IsNullOrWhiteSpace(kind)
            || !AllowedKinds.TryGetValue(source, out var allowedKinds)
            || !allowedKinds.Contains(kind)
            || !IsAllowedMap(source, marker.TerritoryId, marker.MapId)
            || !double.IsFinite(marker.X)
            || !double.IsFinite(marker.Y)
            || !double.IsFinite(marker.Z)
            || Math.Abs(marker.X) > MaxCoordinateMagnitude
            || Math.Abs(marker.Y) > MaxCoordinateMagnitude
            || Math.Abs(marker.Z) > MaxCoordinateMagnitude)
        {
            return false;
        }

        if (source == "tower-eventobj" && marker.BaseId is not > 0)
        {
            return false;
        }

        if (source == "monster"
            && (marker.BaseId is not > 0
                || marker.Level is not > 0 or > 100
                || string.IsNullOrWhiteSpace(marker.Name)))
        {
            return false;
        }

        if (source == "dev-map"
            && kind is "Fate" or "CriticalEncounter"
            && marker.EventId is not > 0)
        {
            return false;
        }

        normalized = marker with
        {
            Source = source,
            Kind = kind,
            Name = NormalizeOptionalString(marker.Name, MaxNameLength),
            Level = source == "monster" ? marker.Level : null,
            X = Math.Round(marker.X, 3),
            Y = Math.Round(marker.Y, 3),
            Z = Math.Round(marker.Z, 3),
            HitboxRadius = source == "tower-eventobj"
                ? NormalizeRadius(marker.HitboxRadius, MaxHitboxRadius)
                : null,
            MechanicRadius = source == "tower-eventobj"
                ? kind switch
                {
                    "SmallTrap" => 7,
                    "BigTrap" => 30,
                    _ => null,
                }
                : null,
        };
        return true;
    }

    private static bool IsAllowedMap(string source, uint territoryId, uint mapId)
    {
        return source switch
        {
            "dev-map" => (territoryId, mapId) is (1252, 967) or (1346, 1135),
            "monster" => (territoryId, mapId) is (1252, 967) or (1346, 1135),
            "tower-eventobj" => territoryId switch
            {
                1252 => SouthTowerMaps.Contains(mapId),
                1346 => NorthTowerMaps.Contains(mapId),
                _ => false,
            },
            _ => false,
        };
    }

    private static double? NormalizeRadius(double? value, double maximum)
    {
        return value is { } radius
               && double.IsFinite(radius)
               && radius >= 0
               && radius <= maximum
            ? Math.Round(radius, 3)
            : null;
    }

    private static string? NormalizeOptionalString(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed[..Math.Min(trimmed.Length, maximumLength)];
    }
}

public sealed record UploaderSummary(
    string UploaderHash,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    long BatchCount,
    long AcceptedCount,
    long RejectedCount,
    long UniqueReports
);

public sealed record UploaderMarkerSummary(
    string Source,
    string Kind,
    long TerritoryId,
    long MapId,
    long Count,
    double MinX,
    double MaxX,
    double MinY,
    double MaxY,
    double MinZ,
    double MaxZ
);

public sealed record DeleteUploaderResult(
    string UploaderHash,
    int DeletedReports,
    int DeletedBatches,
    string BackupPath
);

public sealed record MarkerContributor(
    string UploaderHash,
    string Source,
    string Kind,
    double X,
    double Y,
    double Z,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    long ReportCount
);
