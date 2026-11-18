# syntax=docker/dockerfile:1

# One image for the app: the API, serving the browser app from wwwroot. Build from the
# repository root, so both server/ and client/ are in the context:
#   docker build -f server/Dockerfile -t fluentescritor-api .
#
# The image holds no secrets and no development settings. Every secret is supplied at
# run time as an environment variable (see "Local development (.NET)" in the README).

FROM node:22-alpine AS client
WORKDIR /client

# Install first, from the lock file alone, so this layer is reused until a dependency changes.
COPY client/package.json client/package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY client/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /source

# Restore first, from the project files alone, so this layer is reused until a dependency changes.
COPY server/global.json server/Directory.Build.props server/Directory.Packages.props ./
COPY server/src/FluentEscritor.Api/FluentEscritor.Api.csproj src/FluentEscritor.Api/
COPY server/src/FluentEscritor.ServiceDefaults/FluentEscritor.ServiceDefaults.csproj src/FluentEscritor.ServiceDefaults/
RUN dotnet restore src/FluentEscritor.Api/FluentEscritor.Api.csproj

COPY server/src/Shared/ src/Shared/
COPY server/src/FluentEscritor.Api/ src/FluentEscritor.Api/
COPY server/src/FluentEscritor.ServiceDefaults/ src/FluentEscritor.ServiceDefaults/
RUN dotnet publish src/FluentEscritor.Api/FluentEscritor.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app ./
# The API serves these files, with index.html for every page address.
COPY --from=client /client/dist ./wwwroot/

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The non-root user defined by the base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "FluentEscritor.Api.dll"]
