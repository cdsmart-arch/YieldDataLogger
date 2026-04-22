# ---------------------------------------------------------------------------
# Stage 1: build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy project files first so NuGet restore is cached unless they change.
COPY global.json .
COPY YieldDataLogger.sln .
COPY src/YieldDataLogger.Core/YieldDataLogger.Core.csproj                 src/YieldDataLogger.Core/
COPY src/YieldDataLogger.Collector/YieldDataLogger.Collector.csproj       src/YieldDataLogger.Collector/
COPY src/YieldDataLogger.Api/YieldDataLogger.Api.csproj                   src/YieldDataLogger.Api/

RUN dotnet restore src/YieldDataLogger.Api/YieldDataLogger.Api.csproj

COPY src/ src/
RUN dotnet publish src/YieldDataLogger.Api/YieldDataLogger.Api.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ---------------------------------------------------------------------------
# Stage 2: runtime
# Use the official Microsoft Playwright .NET image which ships with Chromium
# and all required system libs pre-installed for the exact package version.
# Tag format: v<playwright-version>-noble  (noble = Ubuntu 24.04 LTS)
# Keep this tag in sync with the Microsoft.Playwright NuGet version in
# src/YieldDataLogger.Collector/YieldDataLogger.Collector.csproj.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/playwright/dotnet:v1.59.0-noble AS runtime
WORKDIR /app

COPY --from=build /app/publish .

# Non-root user for good container hygiene.
RUN groupadd --system appgroup && useradd --system --gid appgroup appuser \
    && chown -R appuser:appgroup /app
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "YieldDataLogger.Api.dll"]
