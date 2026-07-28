FROM mcr.microsoft.com/dotnet/sdk:8.0-bookworm-slim AS build
WORKDIR /src
COPY BOCCHI.TelemetryServer.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-bookworm-slim
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
ENV BOCCHI_DATA_DIR=/data
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "BOCCHI.TelemetryServer.dll"]
