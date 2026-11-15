# syntax=docker/dockerfile:1

# One image for the API. Build from the server/ directory:
#   docker build -t fluentescritor-api .
#
# The image holds no secrets and no development settings. Every secret is supplied at
# run time as an environment variable (see "Local development (.NET)" in the README).

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /source

# Restore first, from the project files alone, so this layer is reused until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/FluentEscritor.Api/FluentEscritor.Api.csproj src/FluentEscritor.Api/
COPY src/FluentEscritor.ServiceDefaults/FluentEscritor.ServiceDefaults.csproj src/FluentEscritor.ServiceDefaults/
RUN dotnet restore src/FluentEscritor.Api/FluentEscritor.Api.csproj

COPY src/Shared/ src/Shared/
COPY src/FluentEscritor.Api/ src/FluentEscritor.Api/
COPY src/FluentEscritor.ServiceDefaults/ src/FluentEscritor.ServiceDefaults/
RUN dotnet publish src/FluentEscritor.Api/FluentEscritor.Api.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app
COPY --from=build /app ./

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

# The non-root user defined by the base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "FluentEscritor.Api.dll"]
