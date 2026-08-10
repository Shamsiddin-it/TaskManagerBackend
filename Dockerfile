# MentorTaskFlow — single image, three roles: mtf-api, mtf-worker, mtf-migrator (DEPLOY-013).
# Linux base only: the deadline and scheduler calculations of TZ 14.2 and 20.1 depend on the IANA
# tzdata that ships with the official Linux images (DEPLOY-011).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, with only the project graph copied, so a source-only change reuses the layer.
COPY Directory.Build.props Directory.Packages.props global.json MentorTaskFlow.sln ./
COPY src/MentorTaskFlow.Domain/*.csproj              src/MentorTaskFlow.Domain/
COPY src/MentorTaskFlow.Contracts/*.csproj           src/MentorTaskFlow.Contracts/
COPY src/MentorTaskFlow.Application/*.csproj         src/MentorTaskFlow.Application/
COPY src/MentorTaskFlow.Infrastructure/*.csproj      src/MentorTaskFlow.Infrastructure/
COPY src/MentorTaskFlow.Api/*.csproj                 src/MentorTaskFlow.Api/
COPY tests/MentorTaskFlow.UnitTests/*.csproj         tests/MentorTaskFlow.UnitTests/
COPY tests/MentorTaskFlow.IntegrationTests/*.csproj  tests/MentorTaskFlow.IntegrationTests/
COPY tests/MentorTaskFlow.ArchitectureTests/*.csproj tests/MentorTaskFlow.ArchitectureTests/
RUN dotnet restore src/MentorTaskFlow.Api/MentorTaskFlow.Api.csproj

COPY src/ src/
RUN dotnet publish src/MentorTaskFlow.Api/MentorTaskFlow.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# DEPLOY-011 requires IANA tzdata for zone ids such as Asia/Dushanbe (BRN-010, CAT-023). The official
# aspnet image already ships it, so this verifies rather than installs: an apt-get here added a
# network dependency that fails whenever a mirror is mid-sync, for a package that was never missing.
# If a future base image drops tzdata, the build stops here instead of shipping an image whose
# deadline arithmetic silently falls back to UTC.
RUN test -f /usr/share/zoneinfo/Asia/Dushanbe \
 || (echo "IANA tzdata is missing from the base image (DEPLOY-011)." && exit 1)

# Never run as root.
RUN useradd --uid 10001 --create-home --shell /usr/sbin/nologin mentortaskflow
USER 10001

COPY --from=build --chown=10001:10001 /app/publish ./

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    TZ=UTC

EXPOSE 8080

ENTRYPOINT ["dotnet", "MentorTaskFlow.Api.dll"]
