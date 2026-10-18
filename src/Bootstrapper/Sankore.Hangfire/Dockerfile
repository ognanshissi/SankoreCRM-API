# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Sankore.Hangfire — l'ordonnanceur des travaux de fond.
#
# Aligné sur `src/Bootstrapper/Sankore.Api/Dockerfile`, dont les commentaires détaillent
# chaque décision. Ce qui est propre à cette image est seul commenté ici.
#
# Construite en RELEASE : le gate de `Directory.Build.props` ne promeut les avis de sécurité
# NuGet et quelques règles d'analyse en erreurs que dans cette configuration. Ne pas le
# désactiver pour débloquer une construction.
# ─────────────────────────────────────────────────────────────────────────────

# SDK ÉPINGLÉ : le tag flottant `sdk:10.0` servait 10.0.402 quand les machines de l'équipe
# sont en 10.0.302, et `global.json` l'autorise. Une image dont le compilateur change sans
# que rien ne le dise n'est pas reproductible.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS restore
WORKDIR /src

# Gestion centralisée des versions : chaque PackageReference du dépôt omet sa version, donc
# le restore échoue en NU1015 sans `Directory.Packages.props`. `global.json` épingle la bande
# du SDK, `Directory.Build.props` porte le gate.
COPY ["global.json", "Directory.Packages.props", "Directory.Build.props", "./"]

# TOUT le graphe transitif, et pas seulement les références directes. La liste était tombée à
# 4 projets sur 16 — une omission ne casse rien de visible, parce que `dotnet restore` IGNORE
# un projet absent (« Skipping project ... because it was not found ») : le restore réussit
# partiellement, la couche se cache, et le vrai restore se refait dans `publish` à chaque
# modification de source. Tout le bénéfice de cette section disparaît sans un message.
#
# Régénération après l'ajout d'un projet : voir le script en commentaire dans le Dockerfile
# de Sankore.Api, en changeant le projet de départ.

COPY ["SankoreCRM.ServiceDefaults/SankoreCRM.ServiceDefaults.csproj", "SankoreCRM.ServiceDefaults/"]
COPY ["src/Bootstrapper/Sankore.Hangfire/Sankore.Hangfire.csproj", "src/Bootstrapper/Sankore.Hangfire/"]
COPY ["src/Modules/Administration/Sankore.Modules.Administration.PublicApi/Sankore.Modules.Administration.PublicApi.csproj", "src/Modules/Administration/Sankore.Modules.Administration.PublicApi/"]
COPY ["src/Modules/Administration/Sankore.Modules.Administration/Sankore.Modules.Administration.csproj", "src/Modules/Administration/Sankore.Modules.Administration/"]
COPY ["src/Modules/Customers/Sankore.Modules.Customers.PublicApi/Sankore.Modules.Customers.PublicApi.csproj", "src/Modules/Customers/Sankore.Modules.Customers.PublicApi/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.PublicApi/Sankore.Modules.Integration.PublicApi.csproj", "src/Modules/Integration/Sankore.Modules.Integration.PublicApi/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration/Sankore.Modules.Integration.csproj", "src/Modules/Integration/Sankore.Modules.Integration/"]
COPY ["src/Modules/Kyc/Sankore.Modules.Kyc.PublicApi/Sankore.Modules.Kyc.PublicApi.csproj", "src/Modules/Kyc/Sankore.Modules.Kyc.PublicApi/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads.PublicApi/Sankore.Modules.Leads.PublicApi.csproj", "src/Modules/Leads/Sankore.Modules.Leads.PublicApi/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads/Sankore.Modules.Leads.csproj", "src/Modules/Leads/Sankore.Modules.Leads/"]
COPY ["src/Modules/Notifications/Sankore.Modules.Notifications.PublicApi/Sankore.Modules.Notifications.PublicApi.csproj", "src/Modules/Notifications/Sankore.Modules.Notifications.PublicApi/"]
COPY ["src/Modules/Workflow/Sankore.Modules.Workflow.PublicApi/Sankore.Modules.Workflow.PublicApi.csproj", "src/Modules/Workflow/Sankore.Modules.Workflow.PublicApi/"]
COPY ["src/Shared/Sankore.Shared.Infrastructure/Sankore.Shared.Infrastructure.csproj", "src/Shared/Sankore.Shared.Infrastructure/"]
COPY ["src/Shared/Sankore.Shared.Kernel/Sankore.Shared.Kernel.csproj", "src/Shared/Sankore.Shared.Kernel/"]
COPY ["src/Shared/Sankore.Shared.ObjectStorage/Sankore.Shared.ObjectStorage.csproj", "src/Shared/Sankore.Shared.ObjectStorage/"]
COPY ["src/Shared/Sankore.Shared.Spreadsheets/Sankore.Shared.Spreadsheets.csproj", "src/Shared/Sankore.Shared.Spreadsheets/"]

RUN dotnet restore "src/Bootstrapper/Sankore.Hangfire/Sankore.Hangfire.csproj"

FROM restore AS publish
ARG BUILD_CONFIGURATION=Release
COPY . .

# CONTOURNEMENT, et non un correctif — le défaut est au niveau du dépôt.
#
# Sur une arborescence sans `bin/` — un clone neuf, la CI, ou ce conteneur puisque
# `.dockerignore` exclut `**/bin` — MSBuild n'arrive pas à étendre ses propres globs par
# défaut : un patron d'exclusion désigne `bin/<Configuration>`, le répertoire n'existe pas,
# l'expansion lève, et MSBuild retombe sur le glob pris pour un nom de fichier littéral. D'où
# `MSB3552: Resource file "**/*.resx" cannot be found` — ce qui faisait échouer CETTE image,
# et non les règles d'analyse que l'ancien commentaire incriminait.
#
# Vérifié : créer les répertoires fait passer la construction, et rien d'autre n'a d'effet.
# Conséquence à ne pas perdre de vue : le dépôt ne se construit pas depuis un clone propre, et
# cette ligne le masque POUR L'IMAGE SEULEMENT.
RUN find . -name '*.csproj' -printf '%h\n' | xargs -I{} mkdir -p '{}/bin/Debug'

# `publish` directement, sans étape `build` intermédiaire : publish compile lui-même, et
# l'étape `build` du modèle Visual Studio produisait un /app/build que rien ne copiait.
#
# AUCUNE dérogation au gate ici, contrairement à la version précédente, qui passait
# `EnforceCodeStyleInBuild=false /p:TreatWarningsAsErrors=false /p:NoWarn=CS9113` en invoquant
# des relevés CA1000/CA1716 sur Sankore.Shared.Kernel. Deux raisons de les retirer : ces
# relevés n'existent plus — mesuré, zéro occurrence — et surtout `TreatWarningsAsErrors` est un
# booléen qui n'a jamais eu d'effet sur le gate, lequel repose sur `WarningsAsErrors`, une
# LISTE. La dérogation ne relâchait donc rien ; elle donnait seulement l'impression de le
# faire, et désignait la mauvaise cause de l'échec.
RUN dotnet publish "src/Bootstrapper/Sankore.Hangfire/Sankore.Hangfire.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# L'image d'EXÉCUTION reste sur le tag mineur, à l'inverse du SDK : un correctif d'exécution
# est un correctif de sécurité qu'on veut recevoir, un changement de bande du SDK réécrit la
# construction.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Déclaré AVANT le COPY pour que l'utilisateur applicatif ne soit pas propriétaire des
# binaires : il les lit, il ne les réécrit pas.
USER $APP_UID

# 8080 en clair seulement. Pas de 8081 : l'image ne porte aucun certificat, donc exposer un
# port HTTPS annonçait un point d'entrée qui n'écoute pas.
EXPOSE 8080

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Hangfire.dll"]
