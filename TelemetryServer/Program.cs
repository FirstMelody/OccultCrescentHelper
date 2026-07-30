using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

const int maxRequestBodyBytes = 2 * 1024 * 1024;
const int defaultMinimumReporters = 2;
const int defaultDailyUniqueMarkerLimit = 1500;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodyBytes);
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

var dataDirectory = Environment.GetEnvironmentVariable("BOCCHI_DATA_DIR") ?? "/data";
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "telemetry.db");
var minimumReporters = ReadPositiveInt(
    "BOCCHI_MIN_REPORTERS",
    defaultMinimumReporters,
    maximum: 10
);
var dailyUniqueMarkerLimit = ReadPositiveInt(
    "BOCCHI_DAILY_UNIQUE_MARKER_LIMIT",
    defaultDailyUniqueMarkerLimit,
    maximum: 100_000
);
var authoritativeUploaderHash = ReadOptionalUploaderHash(
    "BOCCHI_AUTHORITATIVE_UPLOADER_HASH"
);
var store = new TelemetryStore(
    databasePath,
    minimumReporters,
    dailyUniqueMarkerLimit,
    authoritativeUploaderHash
);
await store.InitializeAsync();
var linkerCatalogPath = Path.Combine(
    AppContext.BaseDirectory,
    "wwwroot",
    "maps",
    "linker-markers.json"
);
await store.SyncTrustedCatalogAsync(LinkerMarkerCatalog.Load(linkerCatalogPath));

if (args.Length > 0 && string.Equals(args[0], "admin", StringComparison.OrdinalIgnoreCase))
{
    Environment.ExitCode = await TelemetryAdmin.RunAsync(store, args.Skip(1).ToArray());
    return;
}

var ipHashSecret = ReadIpHashSecret();
var app = builder.Build();

var forwardedHeaderOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
// Caddy connects to the loopback-published port through Docker's private gateway.
// Trust only that private address family in addition to ASP.NET's loopback defaults.
forwardedHeaderOptions.KnownNetworks.Add(
    new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12)
);
app.UseForwardedHeaders(forwardedHeaderOptions);
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost(
        "/api/v1/markers",
        async (HttpContext context, TelemetryBatch batch) =>
        {
            var uploaderHash = HashRemoteIp(context.Connection.RemoteIpAddress, ipHashSecret);
            var result = await store.AcceptBatchAsync(uploaderHash, batch);
            return Results.Json(
                result.Body,
                statusCode: result.StatusCode
            );
        }
    )
    .RequireRateLimiting("uploads");

app.MapGet(
    "/api/v1/stats",
    async (uint? territoryId, uint? mapId) =>
        Results.Ok(await store.GetStatsAsync(territoryId, mapId))
);

app.MapGet(
    "/api/v1/markers",
    async (uint? territoryId, uint? mapId, int? limit) =>
    {
        var take = Math.Clamp(limit ?? 500, 1, 10000);
        return Results.Ok(
            new
            {
                markers = await store.GetMarkersAsync(territoryId, mapId, take),
            }
        );
    }
);

app.Run();

static byte[] ReadIpHashSecret()
{
    var encoded = Environment.GetEnvironmentVariable("BOCCHI_IP_HASH_SECRET");
    if (string.IsNullOrWhiteSpace(encoded))
    {
        throw new InvalidOperationException(
            "BOCCHI_IP_HASH_SECRET must be a Base64-encoded random secret of at least 32 bytes."
        );
    }

    byte[] secret;
    try
    {
        secret = Convert.FromBase64String(encoded);
    }
    catch (FormatException exception)
    {
        throw new InvalidOperationException(
            "BOCCHI_IP_HASH_SECRET is not valid Base64.",
            exception
        );
    }

    if (secret.Length < 32)
    {
        throw new InvalidOperationException(
            "BOCCHI_IP_HASH_SECRET must decode to at least 32 bytes."
        );
    }

    return secret;
}

static string HashRemoteIp(IPAddress? address, byte[] secret)
{
    var normalized = address?.MapToIPv6().GetAddressBytes() ?? [0];
    return Convert.ToHexString(HMACSHA256.HashData(secret, normalized));
}

static int ReadPositiveInt(string name, int fallback, int maximum)
{
    var value = Environment.GetEnvironmentVariable(name);
    return int.TryParse(value, out var parsed) && parsed > 0
        ? Math.Min(parsed, maximum)
        : fallback;
}

static string ReadOptionalUploaderHash(string name)
{
    var value = Environment.GetEnvironmentVariable(name)?.Trim().ToUpperInvariant();
    if (string.IsNullOrEmpty(value))
    {
        return string.Empty;
    }

    if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
    {
        throw new InvalidOperationException(
            $"{name} must be an uppercase 64-character hexadecimal uploader hash."
        );
    }

    return value;
}
