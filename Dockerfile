# Multi-stage build for the AxonHub .NET port.
#
# The image runs the API host with Kestrel + an in-process Orleans silo and
# expects PostgreSQL through ConnectionStrings__Default, exactly like the
# reference deployment (see porting/03-acceptance.md). The admin frontend is
# built in the frontend stage and served from /app/frontend/dist.
#
# syntax=docker/dockerfile:1
FROM node:24-alpine AS frontend
WORKDIR /src/frontend

# Match frontend/package.json's packageManager pin.
RUN npm install --global pnpm@10.34.5

# Restore is cached on the lockfile alone; the source copy below leaves
# node_modules from this layer in place.
COPY frontend/package.json frontend/pnpm-lock.yaml frontend/pnpm-workspace.yaml ./
RUN pnpm install --frozen-lockfile

COPY frontend/ ./
RUN pnpm build

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

# Program.cs serves Server:Static:Root (default frontend/dist) relative to the
# content root, which is /app in this image.
COPY --from=frontend /src/frontend/dist ./frontend/dist

# ASP.NET Core 10 images listen on 8080 by default; keep it explicit so a
# deployment can override ASPNETCORE_URLS without surprises.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AxonHub.Api.dll"]
