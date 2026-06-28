# ── Build stage ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY HorusAPI.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --system vpnapi && adduser --system --ingroup vpnapi vpnapi
USER vpnapi

COPY --from=build /app/publish .

EXPOSE 443
ENTRYPOINT ["dotnet", "HorusAPI.dll"]
