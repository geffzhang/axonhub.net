# Multi-stage build for the AxonHub .NET port.
#
# The image runs the API host with Kestrel + an in-process Orleans silo and
# expects PostgreSQL through ConnectionStrings__Default, exactly like the
# reference deployment (see porting/03-acceptance.md).
#
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore is cached on the project graph alone.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AxonHub.Llm/AxonHub.Llm.csproj src/AxonHub.Llm/
COPY src/AxonHub.Abstractions/AxonHub.Abstractions.csproj src/AxonHub.Abstractions/
COPY src/AxonHub.Grains.Abstractions/AxonHub.Grains.Abstractions.csproj src/AxonHub.Grains.Abstractions/
COPY src/AxonHub.Grains/AxonHub.Grains.csproj src/AxonHub.Grains/
COPY src/AxonHub.Persistence/AxonHub.Persistence.csproj src/AxonHub.Persistence/
COPY src/AxonHub.Api/AxonHub.Api.csproj src/AxonHub.Api/
RUN dotnet restore src/AxonHub.Api/AxonHub.Api.csproj

COPY src/ src/
RUN dotnet publish src/AxonHub.Api/AxonHub.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# ASP.NET Core 10 images listen on 8080 by default; keep it explicit so a
# deployment can override ASPNETCORE_URLS without surprises.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AxonHub.Api.dll"]
