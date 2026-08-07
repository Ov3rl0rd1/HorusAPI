# ── Build stage ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

COPY HorusAPI.csproj .
RUN dotnet restore HorusAPI.csproj

COPY . .
# Build only the API project — the test project (and its extra packages) is not
# part of the image, and OpenAPI file generation is skipped in-container.
RUN dotnet publish HorusAPI.csproj -c Release -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --system vpnapi && adduser --system --ingroup vpnapi vpnapi
USER vpnapi

COPY --from=build /app/publish .

EXPOSE 443
ENTRYPOINT ["dotnet", "HorusAPI.dll"]
