# syntax=docker/dockerfile:1

# One image for the app: the API, serving the browser app from wwwroot. Build from the
# repository root, so both server/ and client/ are in the context:
#   docker build -f server/Dockerfile -t escribot-api .
#
# The image holds no secrets and no development settings. Every secret is supplied at
# run time as an environment variable (see "Local development (.NET)" in the README).

FROM node:26-alpine AS client
WORKDIR /client

# Install first, from the lock file alone, so this layer is reused until a dependency changes.
COPY client/package.json client/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
# Set by BuildKit to the architecture of the image being built (amd64 or arm64).
ARG TARGETARCH
WORKDIR /source

# Restore first, from the project files alone, so this layer is reused until a dependency changes.
COPY server/global.json server/Directory.Build.props server/Directory.Packages.props ./
COPY server/src/Escribot.Api/Escribot.Api.csproj src/Escribot.Api/
COPY server/src/Escribot.ServiceDefaults/Escribot.ServiceDefaults.csproj src/Escribot.ServiceDefaults/
RUN dotnet restore src/Escribot.Api/Escribot.Api.csproj --arch "$TARGETARCH" -p:PublishReadyToRun=true

COPY server/src/Shared/ src/Shared/
COPY server/src/Escribot.Api/ src/Escribot.Api/
COPY server/src/Escribot.ServiceDefaults/ src/Escribot.ServiceDefaults/
# ReadyToRun compiles the app and its packages ahead of time, so a replica that starts
# from zero spends less of its half vCPU on JIT compilation before it can answer.
RUN dotnet publish src/Escribot.Api/Escribot.Api.csproj \
    --configuration Release --no-restore --arch "$TARGETARCH" --self-contained false \
    -p:PublishReadyToRun=true --output /app

# Chiseled: only the files .NET needs, with no shell or package manager, so a cold start
# pulls a smaller image. "extra" keeps ICU and time zone data, as in the full image.
# "composite" ships the runtime and ASP.NET Core precompiled together, which starts faster.
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled-composite-extra AS final
WORKDIR /app
COPY --from=build /app ./
# The API serves these files, with index.html for every page address.
COPY --from=client /client/dist ./wwwroot/

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The non-root user defined by the base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "Escribot.Api.dll"]
