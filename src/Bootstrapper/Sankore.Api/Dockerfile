# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────
# Sankore.Api — image de l'hôte HTTP.
#
# Construite en RELEASE, et c'est le seul chemin du dépôt qui l'est : le gate de
# `Directory.Build.props` promeut en erreurs les avis de sécurité NuGet et quelques règles
# d'analyse uniquement en Release. Une image qui se construit est donc aussi la preuve que ce
# gate passe — ne pas le désactiver ici pour « débloquer » une construction : le gate existe
# précisément parce qu'un avis haute sévérité sur la chaîne de Data Protection était resté
# invisible derrière une configuration Release que rien ne construisait.
# ─────────────────────────────────────────────────────────────────────────────

# SDK ÉPINGLÉ à la version exacte de la bande utilisée par l'équipe.
#
# Le tag flottant `sdk:10.0` servait 10.0.402 pendant que les machines sont en 10.0.302, et
# `global.json` l'autorise (`rollForward: latestMinor` accepte toute bande supérieure) : l'image
# se construisait donc avec un SDK que personne n'utilise. Ce n'est pas la cause de l'échec de
# construction qui a motivé cette reprise — vérifié, 10.0.302 échouait pareil — mais une image
# dont le compilateur change sans que rien ne le dise n'est pas reproductible, et c'est ce qui a
# rendu ce diagnostic beaucoup plus long qu'il devait l'être.
#
# Relever cette version est un acte délibéré : la faire suivre le dépôt, pas l'inverse.
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS restore
WORKDIR /src

# Les fichiers partagés d'abord : ils changent rarement et conditionnent tout le reste.
# `global.json` épingle la bande du SDK, `Directory.Packages.props` toutes les versions de
# paquets, `Directory.Build.props` le gate ci-dessus.
COPY ["global.json", "Directory.Packages.props", "Directory.Build.props", "./"]

# Puis UNIQUEMENT les .csproj, pour que la couche de restore se recache indépendamment du
# code source : modifier un fichier .cs ne doit pas refaire télécharger les paquets.
#
# ATTENTION — cette liste doit contenir TOUT le graphe transitif de Sankore.Api, et une
# omission ne casse rien de visible. `dotnet restore` IGNORE un projet absent avec un simple
# « Skipping project ... because it was not found » : le restore réussit, partiellement, la
# couche se cache, et le vrai restore se refait dans `dotnet publish` à chaque modification de
# source — c'est-à-dire que tout le bénéfice de cette section disparaît sans un seul message
# d'erreur. Elle était tombée à 8 projets sur 28.
#
# Pour la régénérer après l'ajout d'un projet :
#   python3 - <<'EOF'
#   import re, os
#   from pathlib import Path
#   seen=set()
#   def walk(p):
#       p=Path(os.path.normpath(p))
#       if p in seen: return
#       seen.add(p)
#       for m in re.finditer(r'ProjectReference\s+Include="([^"]+)"',
#                            p.read_text(encoding='utf-8-sig')):
#           walk(p.parent / m.group(1).replace('\\','/'))
#   walk(Path('src/Bootstrapper/Sankore.Api/Sankore.Api.csproj'))
#   for p in sorted(str(x) for x in seen):
#       print(f'COPY ["{p}", "{Path(p).parent}/"]')
#   EOF

COPY ["src/Bootstrapper/Sankore.Api/Sankore.Api.csproj", "src/Bootstrapper/Sankore.Api/"]
COPY ["src/Modules/Administration/Sankore.Modules.Administration.PublicApi/Sankore.Modules.Administration.PublicApi.csproj", "src/Modules/Administration/Sankore.Modules.Administration.PublicApi/"]
COPY ["src/Modules/Administration/Sankore.Modules.Administration/Sankore.Modules.Administration.csproj", "src/Modules/Administration/Sankore.Modules.Administration/"]
COPY ["src/Modules/Customers/Sankore.Modules.Customers.PublicApi/Sankore.Modules.Customers.PublicApi.csproj", "src/Modules/Customers/Sankore.Modules.Customers.PublicApi/"]
COPY ["src/Modules/Customers/Sankore.Modules.Customers/Sankore.Modules.Customers.csproj", "src/Modules/Customers/Sankore.Modules.Customers/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.Amplitude/Sankore.Modules.Integration.Adapters.Amplitude.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.Amplitude/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.Fake/Sankore.Modules.Integration.Adapters.Fake.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.Fake/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.Orass/Sankore.Modules.Integration.Adapters.Orass.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.Orass/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.PerfectVision/Sankore.Modules.Integration.Adapters.PerfectVision.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.PerfectVision/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.Ringover/Sankore.Modules.Integration.Adapters.Ringover.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.Ringover/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.Sab/Sankore.Modules.Integration.Adapters.Sab.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.Sab/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.Adapters.Temenos/Sankore.Modules.Integration.Adapters.Temenos.csproj", "src/Modules/Integration/Sankore.Modules.Integration.Adapters.Temenos/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration.PublicApi/Sankore.Modules.Integration.PublicApi.csproj", "src/Modules/Integration/Sankore.Modules.Integration.PublicApi/"]
COPY ["src/Modules/Integration/Sankore.Modules.Integration/Sankore.Modules.Integration.csproj", "src/Modules/Integration/Sankore.Modules.Integration/"]
COPY ["src/Modules/Kyc/Sankore.Modules.Kyc.PublicApi/Sankore.Modules.Kyc.PublicApi.csproj", "src/Modules/Kyc/Sankore.Modules.Kyc.PublicApi/"]
COPY ["src/Modules/Kyc/Sankore.Modules.Kyc/Sankore.Modules.Kyc.csproj", "src/Modules/Kyc/Sankore.Modules.Kyc/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads.PublicApi/Sankore.Modules.Leads.PublicApi.csproj", "src/Modules/Leads/Sankore.Modules.Leads.PublicApi/"]
COPY ["src/Modules/Leads/Sankore.Modules.Leads/Sankore.Modules.Leads.csproj", "src/Modules/Leads/Sankore.Modules.Leads/"]
COPY ["src/Modules/Notifications/Sankore.Modules.Notifications.PublicApi/Sankore.Modules.Notifications.PublicApi.csproj", "src/Modules/Notifications/Sankore.Modules.Notifications.PublicApi/"]
COPY ["src/Modules/Notifications/Sankore.Modules.Notifications/Sankore.Modules.Notifications.csproj", "src/Modules/Notifications/Sankore.Modules.Notifications/"]
COPY ["src/Modules/Workflow/Sankore.Modules.Workflow.PublicApi/Sankore.Modules.Workflow.PublicApi.csproj", "src/Modules/Workflow/Sankore.Modules.Workflow.PublicApi/"]
COPY ["src/Modules/Workflow/Sankore.Modules.Workflow/Sankore.Modules.Workflow.csproj", "src/Modules/Workflow/Sankore.Modules.Workflow/"]
COPY ["src/Shared/Sankore.Shared.Infrastructure/Sankore.Shared.Infrastructure.csproj", "src/Shared/Sankore.Shared.Infrastructure/"]
COPY ["src/Shared/Sankore.Shared.Kernel/Sankore.Shared.Kernel.csproj", "src/Shared/Sankore.Shared.Kernel/"]
COPY ["src/Shared/Sankore.Shared.ObjectStorage.S3/Sankore.Shared.ObjectStorage.S3.csproj", "src/Shared/Sankore.Shared.ObjectStorage.S3/"]
COPY ["src/Shared/Sankore.Shared.ObjectStorage/Sankore.Shared.ObjectStorage.csproj", "src/Shared/Sankore.Shared.ObjectStorage/"]
COPY ["src/Shared/Sankore.Shared.Realtime/Sankore.Shared.Realtime.csproj", "src/Shared/Sankore.Shared.Realtime/"]
COPY ["src/Shared/Sankore.Shared.Spreadsheets/Sankore.Shared.Spreadsheets.csproj", "src/Shared/Sankore.Shared.Spreadsheets/"]

RUN dotnet restore "src/Bootstrapper/Sankore.Api/Sankore.Api.csproj"

FROM restore AS publish
ARG BUILD_CONFIGURATION=Release
COPY . .

# `publish` directement, sans étape `build` intermédiaire : publish compile lui-même, et
# l'étape `build` que générait le modèle Visual Studio produisait un /app/build que rien ne
# copiait ensuite — une compilation complète jetée.
#
# UseAppHost=false : l'ENTRYPOINT invoque `dotnet Sankore.Api.dll`, donc l'exécutable natif
# serait du poids mort.
# CONTOURNEMENT, et non un correctif — le défaut est au niveau du dépôt, pas de cette image.
#
# Sur une arborescence SANS `bin/` — un clone neuf, la CI, ou ce conteneur, puisque
# `.dockerignore` exclut `**/bin` — MSBuild n'arrive pas à étendre ses propres globs par défaut :
#
#   An exception occurred while expanding a fileSpec with globs: fileSpec: "**/*.resx",
#   assuming it is a file name. Exception: ... Could not find a part of the path
#   '/src/src/Modules/Workflow/Sankore.Modules.Workflow/bin/Debug'.
#
# Un patron d'exclusion désigne `bin/<Configuration>`, le répertoire n'existe pas, l'expansion
# lève, et MSBuild retombe sur le glob pris pour un nom de fichier littéral — d'où
# `MSB3552: Resource file "**/*.resx" cannot be found` sur les sept projets principaux de
# modules. Cela touche `**/*.cs` et `**/*` de la même façon.
#
# Vérifié par le test minimal : créer les répertoires fait passer la construction, et rien
# d'autre n'a d'effet (ni la bande du SDK, ni les séparateurs de `BaseOutputPath`, ni le
# contournement `DefaultItemExcludes` déjà présent dans `Directory.Build.props`).
#
# Conséquence à ne pas perdre de vue : le dépôt ne se construit pas depuis un clone propre, et
# cette ligne le masque POUR L'IMAGE SEULEMENT. La CI et la première construction d'un nouveau
# développeur restent cassées jusqu'à ce que la cause soit traitée dans `Directory.Build.props`.
RUN find . -name '*.csproj' -printf '%h\n' | xargs -I{} mkdir -p '{}/bin/Debug'

RUN dotnet publish "src/Bootstrapper/Sankore.Api/Sankore.Api.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# L'image d'EXÉCUTION reste sur le tag mineur, volontairement, à l'inverse du SDK ci-dessus.
# Les deux risques ne sont pas les mêmes : un correctif de l'exécution est un correctif de
# sécurité qu'on veut recevoir, tandis qu'un changement de bande du SDK réécrit la
# construction. On épingle donc ce qui compile, et on laisse se corriger ce qui exécute.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app

# Non-root, fourni par l'image de base. Déclaré AVANT le COPY pour que l'utilisateur
# applicatif ne soit pas propriétaire des binaires : il les lit, il ne les réécrit pas.
USER $APP_UID

# 8080 en clair. Pas de 8081 : l'image ne porte aucun certificat, donc exposer un port HTTPS
# annonçait un point d'entrée qui n'écoute pas. La terminaison TLS est en amont.
EXPOSE 8080

COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Sankore.Api.dll"]
