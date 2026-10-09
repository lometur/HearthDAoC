# syntax=docker/dockerfile:1
# HearthDAoC server (unofficial fork of shadowofze/OfflineDAoC). Build from the repository root:
#   docker build -f deploy/Dockerfile -t hearthdaoc:dev .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
RUN apt-get update \
 && apt-get install -y --no-install-recommends cmake g++ make binutils python3 \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY source/server ./source/server
COPY source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs ./source/tools/OfflineDaoc.Launcher/BotCharacterGenerator.cs
COPY source/tools/OfflineDaoc.ProgressImport ./source/tools/OfflineDaoc.ProgressImport
COPY tools/linux ./tools/linux
COPY deploy ./deploy
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# Server (the build only needs some serverconfig.xml; never the example file, which listens on all interfaces with UPnP on)
RUN cp deploy/serverconfig.build.xml source/server/CoreServer/config/serverconfig.xml \
 && dotnet build source/server/DOLLinux.sln -c Release
# Native pathfinding from the server's own Detour copy; it must export all 14 functions the server imports
RUN cmake -S source/server/Pathing/Detour -B /tmp/detour -DCMAKE_BUILD_TYPE=Release -DSOVERSION=1 -DLIB_VERSION=1.0.0 \
 && cmake --build /tmp/detour -j"$(nproc)" \
 && install -D -m 755 /tmp/detour/libDetour.so.1.0.0 source/server/Release/lib/Detour.so \
 && test "$(nm -D --defined-only source/server/Release/lib/Detour.so | grep -cE ' T (LoadNavMesh|FreeNavMesh|CreateNavMeshQuery|FreeNavMeshQuery|PathStraight|MoveAlongSurface|MoveAlongSurfaceGrounded|FindRandomPointAroundCircle|FindClosestPoint|FindClosestPointInBox|HasLineOfSight|UpdateFlags|GetPolyAt|GetPolysInBox)$')" = 14
# Upstream release config files the source tree lacks (verified against the pinned manifest). The release's
# server data files (the lock's server_files) are not baked in: init_world.py downloads them into /data.
RUN for f in logconfig.xml invalidnames.txt MailConfig.xml; do \
      python3 tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract "runtime/server/config/$f" "source/server/Release/config/$f"; \
    done
# Admin CLIs
RUN tools/linux/build.sh /out/tools

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
 && apt-get install -y --no-install-recommends python3 sqlite3 tzdata \
 && rm -rf /var/lib/apt/lists/*
# The .NET 10 images are Ubuntu 24.04 and already have uid/gid 1000 ("ubuntu"); the server runs by number.
COPY --from=build /src/source/server/Release /app/server
COPY --from=build /out/tools /app/tools
COPY tools/linux/accounts /app/tools/accounts
COPY tools/linux/odaoc_fetch.py /app/bin/odaoc_fetch.py
COPY deploy/bin /app/bin
COPY deploy/entrypoint.sh /app/entrypoint.sh
COPY deploy/upstream.lock /app/upstream.lock
# /app/server must be writable by whichever HEARTHDAOC_UID runs it (script compilation, dashboards, links).
# /data is world-writable so a fresh volume works for any HEARTHDAOC_UID (Docker copies this mode into it).
RUN mkdir -p /data \
 && chown 1000:1000 /data && chmod 0777 /data \
 && chmod -R a+rwX /app/server \
 && chmod +x /app/entrypoint.sh /app/bin/*.sh /app/bin/*.py /app/tools/accounts/accounts.py
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=0 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_CLI_HOME=/tmp \
    XDG_CONFIG_HOME=/tmp/xdg \
    PYTHONUNBUFFERED=1 \
    HEARTHDAOC_EDITION=classic
USER 1000:1000
VOLUME /data
HEALTHCHECK --interval=30s --timeout=5s --start-period=10m --retries=3 CMD ["/app/bin/healthcheck.sh"]
ENTRYPOINT ["/app/entrypoint.sh"]
