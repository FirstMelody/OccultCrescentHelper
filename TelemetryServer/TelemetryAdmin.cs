using System.Text.RegularExpressions;

public static partial class TelemetryAdmin
{
    public static async Task<int> RunAsync(TelemetryStore store, string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "list-uploaders":
            {
                var limit = args.Length > 1 && int.TryParse(args[1], out var parsed)
                    ? parsed
                    : 50;
                foreach (var item in await store.ListUploadersAsync(limit))
                {
                    Console.WriteLine(
                        $"{item.UploaderHash} first={item.FirstSeenUtc:O} "
                        + $"last={item.LastSeenUtc:O} batches={item.BatchCount} "
                        + $"accepted={item.AcceptedCount} rejected={item.RejectedCount} "
                        + $"unique={item.UniqueReports}"
                    );
                }

                return 0;
            }
            case "inspect-uploader" when args.Length == 2:
            {
                if (!IsUploaderHash(args[1]))
                {
                    Console.Error.WriteLine("Invalid uploader hash.");
                    return 2;
                }

                foreach (var item in await store.InspectUploaderAsync(args[1]))
                {
                    Console.WriteLine(
                        $"{item.Source}/{item.Kind} territory={item.TerritoryId} "
                        + $"map={item.MapId} count={item.Count} "
                        + $"x={item.MinX:F3}..{item.MaxX:F3} "
                        + $"y={item.MinY:F3}..{item.MaxY:F3} "
                        + $"z={item.MinZ:F3}..{item.MaxZ:F3}"
                    );
                }

                return 0;
            }
            case "find-marker" when args.Length is 6 or 7:
            {
                if (!long.TryParse(args[1], out var territoryId)
                    || !long.TryParse(args[2], out var mapId)
                    || !double.TryParse(
                        args[3],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var x
                    )
                    || !double.TryParse(
                        args[4],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var y
                    )
                    || !double.TryParse(
                        args[5],
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var z
                    ))
                {
                    Console.Error.WriteLine("Invalid marker coordinates.");
                    return 2;
                }

                var radius = args.Length == 7
                             && double.TryParse(
                                 args[6],
                                 System.Globalization.CultureInfo.InvariantCulture,
                                 out var parsedRadius
                             )
                    ? parsedRadius
                    : 1;
                foreach (var item in await store.FindMarkerContributorsAsync(
                             territoryId,
                             mapId,
                             x,
                             y,
                             z,
                             radius
                         ))
                {
                    Console.WriteLine(
                        $"{item.UploaderHash} {item.Source}/{item.Kind} "
                        + $"xyz={item.X:F3},{item.Y:F3},{item.Z:F3} "
                        + $"first={item.FirstSeenUtc:O} last={item.LastSeenUtc:O} "
                        + $"reports={item.ReportCount}"
                    );
                }

                return 0;
            }
            case "delete-uploader" when args.Length == 3:
            {
                var uploaderHash = args[1];
                if (!IsUploaderHash(uploaderHash)
                    || !string.Equals(uploaderHash, args[2], StringComparison.Ordinal))
                {
                    Console.Error.WriteLine(
                        "Refusing deletion: provide the same valid hash twice."
                    );
                    return 2;
                }

                var result = await store.DeleteUploaderAsync(uploaderHash);
                Console.WriteLine(
                    $"deleted hash={result.UploaderHash} reports={result.DeletedReports} "
                    + $"batches={result.DeletedBatches} backup={result.BackupPath}"
                );
                return 0;
            }
            default:
                PrintUsage();
                return 2;
        }
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Usage:
              admin list-uploaders [limit]
              admin inspect-uploader HASH
              admin find-marker TERRITORY MAP X Y Z [RADIUS]
              admin delete-uploader HASH HASH
            """
        );
    }

    private static bool IsUploaderHash(string value)
    {
        return UploaderHashPattern().IsMatch(value);
    }

    [GeneratedRegex("^[0-9A-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex UploaderHashPattern();
}
