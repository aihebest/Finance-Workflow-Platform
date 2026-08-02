# syntax=docker/dockerfile:1.7

# ── Build ──────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0-jammy AS build
WORKDIR /src

# Restore as a separate layer so a code-only change does not re-download the
# whole package graph on every build.
COPY Directory.Build.props ./
COPY src/Desicon.Workflow.Core/*.csproj            src/Desicon.Workflow.Core/
COPY src/Desicon.Workflow.Domain/*.csproj          src/Desicon.Workflow.Domain/
COPY src/Desicon.Workflow.Infrastructure/*.csproj  src/Desicon.Workflow.Infrastructure/
COPY src/Desicon.Workflow.Api/*.csproj             src/Desicon.Workflow.Api/
RUN dotnet restore src/Desicon.Workflow.Api/Desicon.Workflow.Api.csproj

COPY src/ src/
RUN dotnet publish src/Desicon.Workflow.Api/Desicon.Workflow.Api.csproj \
        -c Release -o /app/publish \
        --no-restore \
        /p:UseAppHost=false

# ── Runtime ────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0-jammy-chiseled AS runtime

# Chiselled image ships no shell and no package manager, which removes most of
# what a container escape would want to use. It also keeps the Trivy image scan
# quiet, because there is almost no OS surface left to have CVEs.

WORKDIR /app
COPY --from=build /app/publish ./

# Workflow definitions are part of the artefact, not external configuration.
# Program.cs resolves Workflow:DefinitionsPath against ContentRootPath, and
# appsettings.json's "../../modules" is a source-tree path -- inside the
# image that is /modules, which does not exist. Baking the definitions in
# and overriding the path below keeps a deployed image self-describing: the
# workflow rules that were tested are the ones that run, and a definition
# change is a new image rather than a live edit to a mounted directory.
COPY modules/ ./modules/

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    Workflow__DefinitionsPath=modules

EXPOSE 8080

# Non-root by default in the chiselled image (UID 64198).
USER $APP_UID

ENTRYPOINT ["dotnet", "Desicon.Workflow.Api.dll"]
