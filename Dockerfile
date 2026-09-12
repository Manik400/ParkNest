# ParkNest API + admin site in one image. See docs/hosting-render-neon.md.
#
# Three stages: build the Angular admin site, publish the .NET API, then a small runtime image
# with the site placed in wwwroot so the API serves it same-origin (Program.cs picks it up when
# wwwroot/index.html exists). One free web service hosts everything; the database is external.

# --- 1. Admin site -------------------------------------------------------------------------
FROM node:20-alpine AS admin
WORKDIR /admin
COPY clients/admin/package.json clients/admin/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY clients/admin/ ./
# Production configuration: apiBaseUrl is '' (same origin), hashed filenames.
RUN npx ng build --configuration production

# --- 2. API --------------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/ParkNest.Domain/ParkNest.Domain.csproj src/ParkNest.Domain/
COPY src/ParkNest.Application/ParkNest.Application.csproj src/ParkNest.Application/
COPY src/ParkNest.Infrastructure/ParkNest.Infrastructure.csproj src/ParkNest.Infrastructure/
COPY src/ParkNest.Api/ParkNest.Api.csproj src/ParkNest.Api/
RUN dotnet restore src/ParkNest.Api/ParkNest.Api.csproj
COPY src/ src/
RUN dotnet publish src/ParkNest.Api/ParkNest.Api.csproj -c Release -o /app --no-restore

# --- 3. Runtime ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
COPY --from=admin /admin/dist/admin/browser ./wwwroot
# Listing photos with Storage:Provider=Local land here. On a host with an ephemeral disk they do
# not survive a deploy; switch to object storage before it matters.
RUN mkdir -p /app/media
# Kestrel listens on 8080 in the base image (ASPNETCORE_HTTP_PORTS). TLS is the host's job.
EXPOSE 8080
ENV ASPNETCORE_ENVIRONMENT=Staging \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
ENTRYPOINT ["dotnet", "ParkNest.Api.dll"]
