# Telemetry upload security

The public marker API derives a stable HMAC-SHA256 uploader hash from the real
network address. The HMAC key is stored only in `TelemetryServer/.env` on the
server and must never be committed. Raw addresses are not stored in the marker
database and uploader hashes are never returned by a public endpoint.

New reports are stored separately per uploader hash. A point becomes public
after two distinct uploader hashes report the same normalized identity. The
pre-migration dataset is imported once as the protected
`LEGACY-TRUSTED-IMPORT` baseline.

One server-configured authoritative uploader may bypass the two-reporter
threshold. Its anonymous HMAC uploader hash is stored only in the server
environment, and its reports are preferred as the representative public data
for matching points. Other uploaders continue to require independent
confirmation.

## Administration

Run administrative commands inside the private container:

```sh
docker exec bocchi-telemetry \
  dotnet /app/BOCCHI.TelemetryServer.dll admin list-uploaders 50

docker exec bocchi-telemetry \
  dotnet /app/BOCCHI.TelemetryServer.dll admin inspect-uploader HASH

docker exec bocchi-telemetry \
  dotnet /app/BOCCHI.TelemetryServer.dll \
  admin find-marker TERRITORY MAP X Y Z 1

docker exec bocchi-telemetry \
  dotnet /app/BOCCHI.TelemetryServer.dll admin delete-uploader HASH HASH
```

Deletion requires the same 64-character uppercase hash twice. Before deleting
anything, the server creates a consistent SQLite backup next to
`telemetry.db`.

The delete operation removes every upload batch and marker contribution for
that uploader. Public results are derived from remaining reports, so affected
points disappear immediately or fall back to another contributor.

## Required environment

Generate the secret once:

```sh
openssl rand -base64 48
```

Store it in the server-only `.env` file:

```dotenv
BOCCHI_IP_HASH_SECRET=...
BOCCHI_AUTHORITATIVE_UPLOADER_HASH=...
```

Changing or losing this key changes future hashes and prevents correlating new
uploads with earlier ones. The authoritative hash is optional, must be the
64-character uppercase hash shown by `admin list-uploaders`, and must be
updated if the authoritative client's public network address changes.
