# ---------------------------------------------------------------------------
# Stage 1: build
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /repo

# Copy solution + project files first so NuGet restore is cached unless they change.
COPY global.json .
COPY YieldDataLogger.sln .
COPY src/YieldDataLogger.Core/YieldDataLogger.Core.csproj                 src/YieldDataLogger.Core/
COPY src/YieldDataLogger.Collector/YieldDataLogger.Collector.csproj       src/YieldDataLogger.Collector/
COPY src/YieldDataLogger.Api/YieldDataLogger.Api.csproj                   src/YieldDataLogger.Api/

RUN dotnet restore src/YieldDataLogger.Api/YieldDataLogger.Api.csproj

# Now copy the rest and publish.
COPY src/ src/
RUN dotnet publish src/YieldDataLogger.Api/YieldDataLogger.Api.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

# ---------------------------------------------------------------------------
# Stage 2: install Playwright browsers
# Microsoft.Playwright publishes a playwright.sh launcher into the app output dir.
# We just run that to download the Chromium binary. OS-level dependencies are
# installed separately in the runtime stage via apt-get, so --with-deps is not
# needed here and we avoid the ambiguous system 'install' command.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS playwright-install
WORKDIR /app
COPY --from=build /app/publish .

RUN chmod +x ./playwright.sh && \
    PLAYWRIGHT_BROWSERS_PATH=/root/.cache/ms-playwright \
    ./playwright.sh install chromium

# ---------------------------------------------------------------------------
# Stage 3: runtime image
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Playwright Chromium needs these system libs on Debian-slim.
# Installing them once here keeps the final image lean while still functional.
RUN apt-get update && apt-get install -y --no-install-recommends \
        libglib2.0-0 libnss3 libnspr4 libdbus-1-3 \
        libatk1.0-0 libatk-bridge2.0-0 libatspi2.0-0 \
        libexpat1 libxcomposite1 libxdamage1 libxfixes3 \
        libxrandr2 libgbm1 libxkbcommon0 libasound2 \
        libx11-6 libxcb1 libxext6 fonts-liberation \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

# Copy the installed Chromium + supporting binaries from the playwright-install stage.
# Playwright stores browsers in ~/.cache/ms-playwright by default.
COPY --from=playwright-install /root/.cache/ms-playwright /root/.cache/ms-playwright

# Non-root service account (good practice for containers).
# The data volume (SQLite, status.json) is mapped by the Container Apps revision.
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser \
    && chown -R appuser:appgroup /app \
    && chown -R appuser:appgroup /root/.cache/ms-playwright || true
USER appuser

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "YieldDataLogger.Api.dll"]
