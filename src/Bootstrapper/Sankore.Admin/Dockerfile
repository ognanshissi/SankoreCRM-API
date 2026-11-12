# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Sankore.Admin — image du registre de tenants.
#
# Même construction que src/Bootstrapper/Sankore.Api/Dockerfile, dont les commentaires
# détaillent chaque choix : SDK épinglé, restauration en couche séparée, `publish` seul, image
# d'exécution non-root. Publiée sur GHCR par .github/workflows/docker.yml sous le nom
# `sankore-registry` — PAS `sankore-admin`, déjà pris par l'application Angular d'administration
# publiée par le dépôt du front.
#
# Construite en RELEASE avec le gate de `Directory.Build.props` (avis de sécurité NuGet et
# quelques règles d'analyse en erreur) : ne pas le désactiver ici pour débloquer une construction.
# ─────────────────────────────────────────────────────────────────────────────

# Même version épinglée que l'image de l'API : les deux images se construisent avec le même
# compilateur. La relever dans les deux fichiers ensemble.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS restore
WORKDIR /src

COPY ["global.json", "Directory.Packages.props", "Directory.Build.props", "./"]

# TOUT le graphe transitif de Sankore.Admin, et seulement lui. Un projet oublié ne casse rien de
# visible au restore (« Skipping project … because it was not found »), mais casse la publication
# `--no-restore` plus bas (« Assets file … project.assets.json not found »). Pour régénérer la
# liste, le script en tête du Dockerfile de l'API, avec le chemin de Sankore.Admin.csproj.
COPY ["SankoreCRM.ServiceDefaults/SankoreCRM.ServiceDefaults.csproj", "SankoreCRM.ServiceDefaults/"]
COPY ["src/Bootstrapper/Sankore.Admin/Sankore.Admin.csproj", "src/Bootstrapper/Sankore.Admin/"]
COPY ["src/Shared/Sankore.Shared.Infrastructure/Sankore.Shared.Infrastructure.csproj", "src/Shared/Sankore.Shared.Infrastructure/"]
COPY ["src/Shared/Sankore.Shared.Kernel/Sankore.Shared.Kernel.csproj", "src/Shared/Sankore.Shared.Kernel/"]
COPY ["src/Shared/Sankore.Shared.ObjectStorage/Sankore.Shared.ObjectStorage.csproj", "src/Shared/Sankore.Shared.ObjectStorage/"]

RUN dotnet restore "src/Bootstrapper/Sankore.Admin/Sankore.Admin.csproj"

FROM restore AS publish
ARG BUILD_CONFIGURATION=Release
COPY . .

# Contournement partagé avec l'image de l'API (voir son commentaire) : sans répertoires `bin/`,
# MSBuild n'étend pas ses globs par défaut et échoue sur « **/*.resx » ou « **/*.cs ».
RUN find . -name '*.csproj' -printf '%h\n' | xargs -I{} mkdir -p '{}/bin/Debug'

RUN dotnet publish "src/Bootstrapper/Sankore.Admin/Sankore.Admin.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Exécution sur le tag mineur : on reçoit les correctifs de sécurité du runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Non-root, déclaré avant le COPY : l'utilisateur applicatif lit les binaires sans les posséder.
USER $APP_UID

# 8080 en clair seulement : aucun certificat dans l'image, TLS terminé en amont (Traefik).
# La sonde de santé de docker-compose.admin.yml interroge `/health` sur ce port.
EXPOSE 8080

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Admin.dll"]
