# Agent relais SANKORE — guide d'installation

Destinataire : l'équipe informatique de l'IMF. Ce document ne suppose aucune connaissance de
SANKORE ni de son code.

L'agent relais est un petit service que **vous** installez sur **votre** matériel, dans **votre**
réseau. Il permet à SANKORE d'atteindre vos systèmes internes — core banking, serveur de fichiers,
base de reporting — sans que vous ouvriez quoi que ce soit vers l'extérieur.

> **État de cette version.** Le composant agent est livré et testé en isolation. Le point d'entrée
> côté plateforme (le serveur qui accepte la session de l'agent) **n'est pas encore déployé** :
> l'enrôlement d'un agent et la mise en service effective se feront à la livraison d'INT-27. Vous
> pouvez dès maintenant préparer le serveur, le pare-feu et le fichier de configuration ; l'étape
> §6 (« vérifier la connexion ») ne pourra aboutir qu'une fois cette partie en place.

---

## 1. Le principe, en une page

```
        VOTRE RÉSEAU                                      INTERNET
 ┌───────────────────────────────────────┐
 │                                       │
 │   ┌───────────────────────────────┐   │   session sortante,
 │   │     Agent relais SANKORE      │───┼──── TLS mutuel  ────────►  SANKORE
 │   │  (conteneur ou service Win.)  │   │   (443 sortant)            api.sankore...
 │   └───────┬───────┬───────┬───────┘   │
 │           │       │       │           │   ◄──── ordres ────────────
 │           ▼       ▼       ▼           │   ────► résultats ────────►
 │      ┌───────┐ ┌─────┐ ┌────────┐     │   ────► battements ───────►
 │      │  CBS  │ │SFTP │ │Reporting│    │
 │      │ HTTP  │ │     │ │PostgreSQL│   │
 │      └───────┘ └─────┘ └────────┘     │
 │                                       │
 └───────────────────────────────────────┘
          AUCUN flux entrant
```

Trois propriétés à retenir, parce qu'elles déterminent ce que vous avez à autoriser :

1. **Connexion sortante uniquement.** C'est l'agent qui appelle SANKORE, jamais l'inverse. SANKORE
   n'ouvre aucun port chez vous. **Votre pare-feu n'a besoin d'aucune règle entrante**, d'aucune
   redirection de port, d'aucune adresse fixe, d'aucun hôte en DMZ. Une règle sortante vers un nom
   d'hôte en 443 suffit.
2. **Tout ce que l'agent peut atteindre est écrit dans votre fichier de configuration.** SANKORE
   ne transmet jamais une URL, un nom d'hôte, un répertoire ni une requête SQL : il nomme une
   *cible déclarée* dans votre fichier. Ce fichier est donc la limite exacte de ce que l'agent peut
   faire chez vous, et vous seuls l'écrivez.
3. **Le SQL est en lecture seule sur une vue que vous déclarez.** L'agent n'exécute jamais une
   requête reçue sur le réseau. Il construit lui-même un `SELECT` sur la vue nommée dans votre
   fichier, avec les seules colonnes de filtre que vous y avez autorisées, dans une transaction
   `READ ONLY`.

L'agent ne conserve rien : aucune donnée n'est écrite sur disque, le temps d'un traitement
seulement en mémoire. Ses journaux ne contiennent aucune donnée personnelle — on y trouve
l'identifiant technique de l'ordre, son type, son résultat et sa durée, jamais un nom, un numéro de
compte, un contenu de fichier ni une ligne de base.

---

## 2. Prérequis

### Serveur

| | Minimum | Confortable |
|---|---|---|
| CPU | 1 vCPU | 2 vCPU |
| RAM | 512 Mo | 1 Go |
| Disque | 500 Mo (binaire + journaux) | 2 Go |

Un seul agent suffit pour un site. Deux agents sur deux machines sont possibles (chacun avec son
propre certificat) si vous voulez de la redondance.

### Système

- **Docker** : Docker Engine 24+ ou Docker Desktop. Rien d'autre à installer.
- **Service Windows** : Windows Server 2019 ou plus récent, avec le
  [Runtime .NET 10 (Desktop/Console, x64)](https://dotnet.microsoft.com/download/dotnet/10.0).
  Le binaire est publié sans runtime embarqué.

### Réseau

| Flux | Source | Destination | Port | Sens |
|------|--------|-------------|------|------|
| Session SANKORE | serveur de l'agent | nom d'hôte fourni par SANKORE | 443/tcp | **sortant** |
| Core banking | serveur de l'agent | votre serveur CBS | selon votre installation | interne |
| SFTP | serveur de l'agent | votre serveur de fichiers | 22/tcp | interne |
| Base de reporting | serveur de l'agent | votre PostgreSQL | 5432/tcp | interne |
| DNS | serveur de l'agent | vos résolveurs | 53 | interne |
| NTP | serveur de l'agent | vos serveurs de temps | 123/udp | interne |

NTP n'est pas décoratif : le TLS mutuel refuse un certificat si l'horloge du serveur dérive de
plusieurs minutes, et le symptôme est un certificat « refusé » sans autre explication.

**Aucune ligne entrante.** Si quelqu'un vous demande d'ouvrir un port vers l'agent, c'est une
erreur de conception : l'agent n'écoute sur aucun port et n'expose aucun service.

### Ce que SANKORE doit vous fournir

1. L'**URL de la session** (`wss://…`).
2. Un **jeton d'enrôlement à usage unique**, valable quelques minutes, qui sert à obtenir le
   certificat client. *(Disponible avec INT-27.)*
3. Le **certificat client** et sa clé privée, au format PKCS#12 (`.pfx`), plus son mot de passe,
   transmis séparément.

---

## 3. Le fichier de configuration

### Où le placer

| Déploiement | Chemin attendu par défaut |
|---|---|
| Docker | `/etc/sankore/relay-agent.json` (dans le conteneur, via un volume monté) |
| Service Windows | `C:\ProgramData\Sankore\RelayAgent\relay-agent.json` |

Deux moyens de changer ce chemin : l'argument `--config <chemin>` ou la variable d'environnement
`SANKORE_RELAY_CONFIG`. L'argument gagne sur la variable, la variable sur le défaut.

Un modèle complet est livré avec les sources :
`src/Modules/Integration/Sankore.Integration.RelayAgent/relay-agent.sample.json`.

### `Relay:Sankore` — où appeler

| Champ | Obligatoire | Défaut | Explication |
|---|---|---|---|
| `ChannelUri` | oui | — | L'URL fournie par SANKORE. **Doit** commencer par `wss://`. `ws://` est refusé au démarrage : il transporterait vos données en clair et ne peut pas porter de certificat client. |
| `ServerCertificateThumbprint` | non | `null` | Empreinte SHA-256 (64 caractères hexadécimaux, sans deux-points) du certificat que SANKORE doit présenter. Laissé vide, c'est le magasin de certificats du système qui valide — le bon choix dans le cas général. À renseigner si votre proxy déchiffre le TLS et que vous voulez être certain qu'il ne le fait pas sur ce flux. |
| `HandshakeTimeoutSeconds` | non | `20` | Budget pour l'appel et l'échange d'ouverture. |
| `MaxFrameBytes` | non | `8388608` (8 Mio) | Taille maximale d'un message reçu. Garde-fou mémoire. |
| `MaxConcurrentOrders` | non | `4` | Nombre d'ordres traités en parallèle. Au-delà, un ordre est refusé immédiatement (`RELAY_AGENT_BUSY`) plutôt que mis en file. Augmentez-le si vos systèmes internes supportent plus de parallélisme ; `4` évite qu'une rafale ouvre quarante sessions SSH d'un coup sur votre serveur de fichiers. |

### `Relay:Certificate` — l'identité de l'agent

Renseignez **exactement un** des deux modes. Les deux à la fois, ou aucun, font échouer le
démarrage avec un message explicite.

| Champ | Explication |
|---|---|
| `Pkcs12Path` | Chemin du fichier `.pfx` (certificat **et** clé privée). Mode recommandé sous Docker. |
| `Password` | Mot de passe du `.pfx`. À éviter dans le fichier : préférez `PasswordFile` ou la variable d'environnement `Relay__Certificate__Password`. |
| `PasswordFile` | Chemin d'un fichier dont tout le contenu est le mot de passe (les espaces et retours à la ligne sont retirés). C'est la forme d'un secret Docker ou Swarm. |
| `StoreThumbprint` | Alternative Windows : empreinte **SHA-256** d'un certificat déjà importé dans le magasin de la machine. La clé privée reste gérée par Windows et n'est jamais un fichier lisible. |
| `StoreName` | Magasin à parcourir. Défaut `My` (« Personnel »). |
| `StoreLocation` | `LocalMachine` (défaut, requis pour un service) ou `CurrentUser`. |

> **Attention à l'empreinte.** L'empreinte que Windows affiche dans ses fenêtres est un **SHA-1**.
> L'agent, comme SANKORE, utilise le **SHA-256**. Un SHA-1 collé ici donne « aucun certificat
> trouvé » alors que le certificat est bien installé. Pour obtenir la bonne valeur :
> ```powershell
> certutil -hashfile C:\chemin\agent.cer SHA256
> ```

### `Relay:Reconnect` — la reconnexion automatique

| Champ | Défaut | Explication |
|---|---|---|
| `InitialDelaySeconds` | `1` | Délai avant la première nouvelle tentative. |
| `MaxDelaySeconds` | `60` | **Plafond.** Le délai double à chaque échec jusqu'à cette valeur, puis s'y arrête : pendant une panne de SANKORE l'agent réessaie environ une fois par minute, indéfiniment. Maximum accepté : 600. |
| `JitterRatio` | `0.25` | Dispersion aléatoire (±25 %) appliquée au délai, pour que tous les agents du parc ne réessaient pas à la même seconde. |

Vous n'avez normalement rien à changer ici. La reconnexion est automatique et permanente : un
redémarrage de SANKORE, une coupure de votre lien internet ou une maintenance de votre proxy se
rattrapent seuls, sans intervention.

### `Relay:Heartbeat` — le battement de cœur

| Champ | Défaut | Explication |
|---|---|---|
| `IntervalSeconds` | `30` | Période du battement. C'est aussi le signal de vie vu par SANKORE. |
| `ProbeTimeoutSeconds` | `5` | Budget du test de latence vers chaque cible. Doit rester inférieur à `IntervalSeconds`. |

### `Relay:HttpTargets` — vos API internes

Une entrée par système. SANKORE nommera `Name` et fournira un chemin **relatif** ; l'adresse vient
d'ici.

| Champ | Obligatoire | Défaut | Explication |
|---|---|---|---|
| `Name` | oui | — | Le nom que SANKORE utilise. Lettres, chiffres, `.`, `_`, `-`. **Unique toutes cibles confondues.** |
| `BaseUrl` | oui | — | URL absolue. **Doit se terminer par `/`** — sinon un chemin relatif se résoudrait un segment trop haut, et l'agent refuse de démarrer plutôt que de corriger silencieusement votre fichier. |
| `AllowedMethods` | non | `["GET","POST"]` | Méthodes autorisées, parmi `GET POST PUT PATCH DELETE HEAD`. **Déclarez `["GET"]` seul pour une intégration en lecture** : aucun ordre ne pourra alors écrire sur ce système, quoi que SANKORE envoie. |
| `Headers` | non | `{}` | En-têtes ajoutés à chaque appel, typiquement la clé d'API de votre système. Ils viennent du fichier, et jamais du réseau. |
| `TimeoutSeconds` | non | `30` | Budget par appel (max 300). |
| `MaxResponseBytes` | non | `4194304` (4 Mio) | Taille maximale d'une réponse. Au-delà, l'ordre échoue au lieu de saturer la mémoire. |

L'agent ne suit **aucune redirection** : une API locale qui répond un 302 est signalée en erreur,
car la redirection mènerait à une adresse que rien n'a vérifiée.

### `Relay:SftpTargets` — vos répertoires d'échange

Une entrée par couple (serveur, répertoire). SANKORE fournira un **nom de fichier nu** ; le
répertoire vient d'ici et aucun nom reçu ne peut en sortir.

| Champ | Obligatoire | Défaut | Explication |
|---|---|---|---|
| `Name` | oui | — | Comme ci-dessus, unique toutes cibles confondues. |
| `Host` / `Port` | oui / non | — / `22` | Serveur SFTP, résolu dans votre réseau. |
| `Username` | oui | — | Compte SSH. |
| `PrivateKeyPath` | l'un des deux | — | Chemin d'une clé privée OpenSSH. Recommandé. |
| `Password` | l'un des deux | — | Mot de passe du compte, ou passphrase de la clé ci-dessus. |
| `HostKeyFingerprintSha256` | **oui** | — | Empreinte SHA-256 en base64 de la clé d'hôte du serveur. Voir l'encadré. |
| `RemotePath` | oui | — | Répertoire, **chemin absolu**. Le seul que cette cible peut toucher. |
| `AllowPut` | — | `false` | Autorise le dépôt de fichiers. |
| `AllowRead` | — | `false` | Autorise la lecture de fichiers. Au moins l'un des deux doit être vrai. |
| `TimeoutSeconds` | non | `60` | Budget par opération (max 300). |
| `MaxFileBytes` | non | `16777216` (16 Mio) | Taille maximale d'un fichier, dans les deux sens. |

> **`HostKeyFingerprintSha256` est obligatoire et l'agent refuse de démarrer sans.** C'est la
> vérification que l'on saute d'habitude « puisque le serveur est le nôtre », et c'est précisément
> celle qui compte ici : ce flux transporte des fichiers clients, et sans elle n'importe quoi
> capable de répondre à cette adresse pourrait les lire et vous en fournir d'autres. Pour obtenir
> la valeur, depuis le serveur de l'agent :
> ```bash
> ssh-keyscan -t rsa,ed25519 sftp.interne.example.lan 2>/dev/null | ssh-keygen -lf -
> # 256 SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU  sftp... (ED25519)
> #              └──────────── copiez cette partie ────────┘
> ```
> Ne copiez **pas** le préfixe `SHA256:`. Le `=` final éventuel est facultatif.
>
> **Déclarez deux cibles pour deux répertoires**, l'une en dépôt seul, l'autre en lecture seule :
> c'est ainsi que SANKORE ne peut pas relire les fichiers que vos autres systèmes laissent dans le
> répertoire sortant.

### `Relay:SqlViewTargets` — vos vues de reporting

Une entrée par vue. **PostgreSQL uniquement** dans cette version : c'est le seul pilote embarqué.
Une base Oracle ou SQL Server demanderait l'ajout d'un pilote et donc une nouvelle version de
l'agent — dit ici plutôt que laissé à découvrir.

| Champ | Obligatoire | Défaut | Explication |
|---|---|---|---|
| `Name` | oui | — | Unique toutes cibles confondues. |
| `ConnectionString` | oui | — | Chaîne Npgsql. **Utilisez un rôle n'ayant que le `SELECT` sur cette vue** — voir l'encadré. |
| `Schema` | non | `public` | Schéma de la vue. Identifiant SQL simple. |
| `View` | oui | — | Nom de la vue. Identifiant SQL simple, **sans le schéma**. |
| `Parameters` | non | `[]` | Colonnes sur lesquelles un ordre peut filtrer. Toute autre colonne nommée par un ordre est refusée (`RELAY_PARAMETER_NOT_DECLARED`). Liste vide = vue sans filtre. |
| `MaxRows` | non | `200` | Plafond de lignes, appliqué en `LIMIT` (max 10000). |
| `TimeoutSeconds` | non | `15` | Budget de la requête (max 300). |

> **Trois verrous, et le premier est le vôtre.**
> 1. **Le rôle de base.** Créez un rôle dédié et n'accordez rien d'autre :
>    ```sql
>    CREATE ROLE sankore_ro LOGIN PASSWORD '…';
>    GRANT CONNECT ON DATABASE cbs_reporting TO sankore_ro;
>    GRANT USAGE  ON SCHEMA reporting        TO sankore_ro;
>    GRANT SELECT ON reporting.v_soldes_clients TO sankore_ro;
>    ```
>    C'est le seul verrou qui tient même si l'agent est mal configuré, et vous seuls pouvez le
>    poser.
> 2. **La transaction.** L'agent exécute `SET TRANSACTION READ ONLY` avant la requête : la session
>    est alors incapable d'écrire, même à travers une vue munie d'un déclencheur `INSTEAD OF`.
> 3. **Le `LIMIT`.** Votre `MaxRows` évite qu'un filtre oublié ne renvoie toute une table.
>
> L'agent ne reçoit **jamais** de requête SQL. Il assemble `SELECT * FROM "schéma"."vue" WHERE
> "colonne" = @p0 … LIMIT n` à partir de votre seul fichier ; les valeurs reçues sont liées en
> paramètres. Une valeur contenant `'; DROP TABLE …` reste une valeur.

---

## 4. Obtenir et installer le certificat client

Le certificat est l'identité de l'agent : il est propre à **cet** agent, pour **cette** IMF. Il
n'est jamais inclus dans l'image, jamais partagé entre deux installations.

1. SANKORE vous transmet un **jeton d'enrôlement à usage unique**, de courte durée de vie
   (quelques minutes). *(Flux livré avec INT-27.)*
2. Vous l'échangez contre un certificat client et sa clé privée. Le jeton est consommé à cet
   instant : il ne fonctionne plus et personne, chez SANKORE, ne peut le réafficher.
3. SANKORE conserve de votre certificat **uniquement son empreinte SHA-256**. Le certificat
   lui-même n'est pas stocké de leur côté, et sa clé privée ne quitte jamais votre machine.
4. Installez-le :

**Docker** — déposez le `.pfx` et son mot de passe dans le répertoire monté :
```bash
sudo mkdir -p /etc/sankore
sudo cp agent.pfx /etc/sankore/relay-agent.pfx
printf '%s' 'le-mot-de-passe' | sudo tee /etc/sankore/cert-password >/dev/null
sudo chown -R 1654:1654 /etc/sankore      # l'UID non-root de l'image
sudo chmod 600 /etc/sankore/relay-agent.pfx /etc/sankore/cert-password
```
puis dans le fichier de configuration :
```json
"Certificate": {
  "Pkcs12Path": "/etc/sankore/relay-agent.pfx",
  "PasswordFile": "/etc/sankore/cert-password"
}
```

**Service Windows** — deux options.

*Option A, le fichier* (la plus simple) :
```powershell
New-Item -ItemType Directory -Force C:\ProgramData\Sankore\RelayAgent | Out-Null
Copy-Item agent.pfx C:\ProgramData\Sankore\RelayAgent\relay-agent.pfx
# Restreindre l'accès au compte de service et aux administrateurs :
icacls C:\ProgramData\Sankore\RelayAgent\relay-agent.pfx /inheritance:r `
  /grant:r "NT SERVICE\SankoreRelayAgent:(R)" /grant:r "BUILTIN\Administrators:(F)"
```

*Option B, le magasin* (la clé privée n'est jamais un fichier lisible) :
```powershell
$pw = Read-Host -AsSecureString "Mot de passe du .pfx"
Import-PfxCertificate -FilePath agent.pfx -CertStoreLocation Cert:\LocalMachine\My -Password $pw
certutil -hashfile agent.cer SHA256     # l'empreinte SHA-256 à mettre dans StoreThumbprint
```
puis `"StoreThumbprint": "…", "StoreLocation": "LocalMachine", "StoreName": "My"` et **pas** de
`Pkcs12Path`.

**Renouvellement.** Le certificat est lu une seule fois, au démarrage. Remplacez le fichier (ou
réimportez-le), puis redémarrez l'agent — c'est volontaire : relire le fichier à chaque connexion
ferait d'un `.pfx` à moitié écrit un agent qui cesse silencieusement de se reconnecter.

---

## 5. Installation

### 5.1 Docker

```bash
# 1. Construire l'image (depuis la racine du dépôt SANKORE)
docker build \
  -f src/Modules/Integration/Sankore.Integration.RelayAgent/Dockerfile \
  -t sankore-relay-agent:1.0.0 .

# 2. Préparer le répertoire de configuration (cf. §3 et §4)
sudo mkdir -p /etc/sankore
sudo cp relay-agent.json /etc/sankore/relay-agent.json

# 3. Lancer
docker run -d \
  --name sankore-relay \
  --restart unless-stopped \
  -v /etc/sankore:/etc/sankore:ro \
  --log-opt max-size=10m --log-opt max-file=5 \
  sankore-relay-agent:1.0.0
```

Remarques :

- **Aucun `-p`.** L'agent n'écoute sur rien. Si vous publiez un port, vous publiez du vide.
- `--restart unless-stopped` : l'agent gère lui-même la reconnexion, mais pas un `OOM kill` ni un
  redémarrage de l'hôte.
- Le volume est monté en lecture seule (`:ro`) : l'agent n'écrit jamais dans son répertoire de
  configuration.
- Le conteneur tourne sous un compte non-root (`$APP_UID`, 1654), d'où le `chown` du §4.

Version `docker compose` équivalente :
```yaml
services:
  relay-agent:
    image: sankore-relay-agent:1.0.0
    restart: unless-stopped
    volumes:
      - /etc/sankore:/etc/sankore:ro
    logging:
      driver: json-file
      options: { max-size: "10m", max-file: "5" }
```

### 5.2 Service Windows

```powershell
# 1. Publier (sur une machine de build disposant du SDK .NET 10)
dotnet publish src\Modules\Integration\Sankore.Integration.RelayAgent `
  -c Release -o C:\Publish\SankoreRelayAgent

# 2. Déployer
New-Item -ItemType Directory -Force "C:\Program Files\Sankore\RelayAgent" | Out-Null
Copy-Item C:\Publish\SankoreRelayAgent\* "C:\Program Files\Sankore\RelayAgent" -Recurse -Force

New-Item -ItemType Directory -Force C:\ProgramData\Sankore\RelayAgent | Out-Null
Copy-Item relay-agent.json C:\ProgramData\Sankore\RelayAgent\relay-agent.json

# 3. Créer le service
sc.exe create SankoreRelayAgent `
  binPath= "\"C:\Program Files\Sankore\RelayAgent\Sankore.Integration.RelayAgent.exe\"" `
  DisplayName= "Agent relais SANKORE" `
  start= auto
sc.exe description SankoreRelayAgent "Relaie les ordres SANKORE vers les systemes internes de l'IMF. Connexion sortante uniquement."

# 4. Redémarrage automatique en cas de plantage du processus
sc.exe failure SankoreRelayAgent reset= 86400 actions= restart/5000/restart/20000/restart/60000

# 5. Démarrer
sc.exe start SankoreRelayAgent
```

Le service tourne par défaut sous `LocalSystem`. Pour le restreindre, utilisez son identité
virtuelle `NT SERVICE\SankoreRelayAgent` (c'est elle qui figure dans la commande `icacls` du §4) :

```powershell
sc.exe config SankoreRelayAgent obj= "NT SERVICE\SankoreRelayAgent"
```

Ce compte doit pouvoir lire `C:\ProgramData\Sankore\RelayAgent\` et, en option B, accéder à la clé
privée du certificat (`Gérer les clés privées` dans la console `certlm.msc`).

Le chemin de configuration peut être changé en ajoutant `--config C:\autre\chemin.json` à la fin du
`binPath`.

---

## 6. Vérifier que ça fonctionne

### 6.1 Au démarrage

**Docker** :
```bash
docker logs -f sankore-relay
```
**Windows** : Observateur d'événements → Journaux Windows → Application, source
**Sankore Relay Agent**. Ou en PowerShell :
```powershell
Get-EventLog -LogName Application -Source "Sankore Relay Agent" -Newest 30 | Format-List
```

Un démarrage sain ressemble à ceci :

```
Relay agent configuration file: /etc/sankore/relay-agent.json
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Sankore.Integration.RelayAgent.Channel.RelayChannelWorker[0]
      Relay session opened to SANKORE. uri=wss://api.sankore.example.com/relay/v1/channel agentVersion=1.0.0
```

La première ligne confirme **quel fichier a été lu** — à vérifier en premier si une modification
semble sans effet.

Si la configuration est invalide, l'agent **refuse de démarrer** et liste tout ce qu'il faut
corriger, clé par clé :

```
The relay agent cannot start: the configuration file has 2 problem(s).
  File: /etc/sankore/relay-agent.json
  - Relay:Sankore:ChannelUri must use wss:// (mutual TLS), not 'ws'.
  - Relay:SftpTargets:0:HostKeyFingerprintSha256 is required. Obtain it with ...
```

C'est volontaire : une cible silencieusement inutilisable se manifesterait des semaines plus tard
par « SANKORE ne voit pas nos soldes », avec la cause enfouie dans un journal que personne ne
regarde.

### 6.2 Le battement de cœur quand tout va bien

Toutes les 30 secondes, l'agent envoie à SANKORE un message de ce genre (visible côté SANKORE, dans
la fiche de l'agent relais) :

```json
{
  "agentVersion": "1.0.0",
  "state": "Healthy",
  "at": "2026-10-09T08:41:12.4+00:00",
  "ordersInFlight": 0,
  "targets": [
    { "name": "cbs-api",    "kind": "HttpCall", "state": "Reachable", "latencyMs": 7,  "checkedAt": "…" },
    { "name": "cbs-depot",  "kind": "SftpPut",  "state": "Reachable", "latencyMs": 3,  "checkedAt": "…" },
    { "name": "cbs-retour", "kind": "SftpRead", "state": "Reachable", "latencyMs": 3,  "checkedAt": "…" },
    { "name": "soldes",     "kind": "SqlView",  "state": "Reachable", "latencyMs": 2,  "checkedAt": "…" }
  ]
}
```

Comment le lire :

- `state: "Healthy"` — la session est ouverte et **toutes** les cibles déclarées répondent.
- `state: "Degraded"` — la session est ouverte, mais **au moins une** cible ne répond pas. C'est le
  cas le plus souvent mal interprété : l'intégration n'est pas « tombée », c'est un de vos systèmes
  internes qui ne répond pas. La ligne `Unreachable` dit lequel.
- `state: "Unknown"` sur une cible — elle n'a encore été ni testée ni utilisée depuis le démarrage.
  Normal pendant les premières secondes. Durable, cela signifie que l'agent vient de redémarrer en
  boucle.
- `latencyMs` — temps d'établissement d'une connexion TCP vers la cible. Mesuré avant chaque
  battement, et remplacé par la mesure d'un ordre réel dès qu'il y en a un (plus représentatif).
  Un test TCP ne prouve pas que les identifiants sont bons ni que la vue existe : seul un ordre réel
  le dit.
- `ordersInFlight` — ordres en cours. Durablement égal à `MaxConcurrentOrders`, c'est le signe
  qu'un de vos systèmes répond trop lentement.
- Aucun champ ne contient de donnée personnelle : des noms de cibles issus de votre fichier, des
  états et des durées.

### 6.3 Un test de bout en bout

Sur demande de SANKORE, un ordre de test est envoyé vers chaque cible déclarée. Dans vos journaux
vous verrez, par ordre :

```
info: Relay order received.  correlationId=7f3c… kind=SqlView target=soldes
info: Relay order succeeded. correlationId=7f3c… kind=SqlView target=soldes durationMs=14 resultBytes=382
```

C'est tout ce que les journaux contiennent sur un ordre : son identifiant technique, son type, la
cible, le résultat, la durée et la **taille** de la réponse. Jamais le contenu.

---

## 7. Lire les trois pannes

### 7.1 Certificat refusé

**Journal :**
```
warn: Relay session lost. exceptionType=AuthenticationException attempt=1 retryInMs=1210
warn: Relay session lost. exceptionType=AuthenticationException attempt=2 retryInMs=2080
```
(ou `WebSocketException` si votre proxy intercepte le refus)

L'agent réessaie indéfiniment et ne démarrera jamais de session. À vérifier dans cet ordre :

| Vérification | Comment |
|---|---|
| L'heure du serveur | `date -u` / `w32tm /query /status`. Une dérive de quelques minutes suffit à faire rejeter un certificat valide. C'est la première cause, et la plus déroutante. |
| Le `.pfx` est lisible et le mot de passe correct | Ces deux fautes **empêchent le démarrage** et ne produisent pas de boucle de reconnexion : le service s'arrête en nommant le chemin (fichier absent), ou avec une `CryptographicException` (mot de passe faux, fichier tronqué), ou avec une erreur de droits si le compte de service ne peut pas lire le fichier. Si vous voyez une boucle de reconnexion, le certificat a bien été chargé et le problème est ailleurs dans ce tableau. |
| Le certificat n'est pas expiré | `openssl pkcs12 -in relay-agent.pfx -nokeys \| openssl x509 -noout -dates` |
| L'agent n'a pas été **révoqué** côté SANKORE | Une révocation coupe la session immédiatement et refuse toute reconnexion. Demandez à SANKORE l'état de l'agent. Un certificat révoqué ne se « répare » pas : il faut un nouvel enrôlement. |
| Mode magasin : la bonne empreinte | `StoreThumbprint` attend un **SHA-256**, pas le SHA-1 affiché par Windows (§3). |
| Mode magasin : la clé privée est présente | `Get-ChildItem Cert:\LocalMachine\My \| Where-Object HasPrivateKey`. Importer un `.cer` au lieu d'un `.pfx` donne un certificat sans clé privée, inutilisable en TLS mutuel. |

### 7.2 SANKORE injoignable

**Journal :**
```
warn: Relay session lost. exceptionType=WebSocketException attempt=1 retryInMs=1233
warn: Relay session lost. exceptionType=WebSocketException attempt=2 retryInMs=1982
warn: Relay session lost. exceptionType=WebSocketException attempt=3 retryInMs=3955
warn: Relay session lost. exceptionType=WebSocketException attempt=4 retryInMs=7500
warn: Relay session lost. exceptionType=WebSocketException attempt=5 retryInMs=58000
```

Le délai qui **double puis se stabilise autour de 60 secondes** est le comportement normal : c'est
le plafond `MaxDelaySeconds`. L'agent reprendra seul dans la minute suivant le retour de SANKORE —
il n'y a rien à redémarrer, et redémarrer le service ne fait que remettre le délai à 1 seconde.

À vérifier :

| Vérification | Comment |
|---|---|
| Résolution DNS | `getent hosts api.sankore.example.com` / `Resolve-DnsName` |
| Le 443 sortant est bien autorisé | `curl -v https://api.sankore.example.com/` depuis le serveur de l'agent. Un blocage pare-feu donne un `timeout`, un proxy obligatoire un `407`. |
| Un proxy d'entreprise est nécessaire | Renseignez `HTTPS_PROXY` dans l'environnement du service ou du conteneur. L'agent utilise les variables standard. |
| L'URL | Relisez `ChannelUri`. Une erreur de chemin donne un `WebSocketException` identique à une coupure réseau. |
| SANKORE est effectivement en service | Côté plateforme. Dans l'état actuel du projet, c'est l'explication la plus probable : **le point d'entrée côté SANKORE n'est pas encore déployé** (voir l'encadré en tête de ce document). |

Si le message est `exceptionType=RelayProtocolException`, ce n'est pas une panne réseau mais un
désaccord de version, et il est accompagné d'une ligne explicite :
```
fail: Relay handshake refused: Protocol version mismatch: this agent speaks 1, SANKORE answered 2. Upgrade the agent.
```
Réessayer n'y changera rien : il faut la version d'agent correspondante.

### 7.3 Un système relayé injoignable

Ici **la session SANKORE va bien** ; c'est un de vos systèmes internes qui ne répond pas. Le
battement passe en `Degraded` et les journaux nomment la cible :

```
warn: Relay order failed. correlationId=91ab… kind=HttpCall target=cbs-api outcome=Unavailable errorCode=RELAY_TARGET_UNREACHABLE durationMs=21 exceptionType=SocketException
```

Les codes et ce qu'ils veulent dire :

| Code | Signification | À faire |
|---|---|---|
| `RELAY_TARGET_UNREACHABLE` | Connexion impossible ou refusée. | Le système est arrêté, ou le flux interne est bloqué. Testez depuis le serveur de l'agent. |
| `RELAY_TARGET_TIMEOUT` | Pas de réponse dans le budget. | Système surchargé, ou `TimeoutSeconds` trop court pour une opération lourde. |
| `RELAY_TARGET_ERROR` | Le système a répondu et a refusé. | Identifiants SFTP, droits sur le répertoire, vue supprimée, `GRANT` révoqué. Le détail est dans les journaux **de ce système**, chez vous — l'agent n'en recopie rien, par conception. |
| `RELAY_HOST_KEY_REFUSED` | La clé d'hôte SSH ne correspond pas à `HostKeyFingerprintSha256`. | **Ne corrigez pas l'empreinte par réflexe.** Soit le serveur a été réinstallé ou sa clé a tourné (légitime : relevez la nouvelle empreinte comme au §3), soit quelque chose s'est interposé. Tranchez cette question avant de modifier le fichier. |
| `RELAY_PAYLOAD_TOO_LARGE` | Réponse ou fichier au-delà du plafond. | Relevez `MaxResponseBytes` / `MaxFileBytes`, ou vérifiez que le système ne renvoie pas une page d'erreur HTML à la place des données. |
| `RELAY_TARGET_NOT_DECLARED` | SANKORE a nommé une cible absente de votre fichier. | Normal si vous venez d'en retirer une. Sinon, signalez-le : SANKORE et votre fichier ne sont plus d'accord. |
| `RELAY_PARAMETER_NOT_DECLARED` | Un ordre a filtré sur une colonne non autorisée. | Ajoutez la colonne à `Parameters` **si c'est voulu**. Le refus est délibéré : l'ignorer élargirait silencieusement le résultat. |
| `RELAY_METHOD_NOT_ALLOWED` / `RELAY_DIRECTION_NOT_ALLOWED` | Un ordre a demandé une écriture sur une cible déclarée en lecture, ou l'inverse. | Idem : élargissez la déclaration si c'est voulu. |
| `RELAY_AGENT_BUSY` | `MaxConcurrentOrders` atteint. | Ponctuellement, normal. Durablement, un de vos systèmes répond trop lentement. |
| `RELAY_UNEXPECTED_ERROR` | Cas non prévu. | Relevez `correlationId`, `kind`, `target`, `exceptionType` et transmettez-les au support SANKORE. **Ne transmettez pas de contenu de fichier ni de ligne de base** : ils ne sont pas nécessaires au diagnostic. |

`correlationId` est la clé : c'est le même identifiant des deux côtés, donc la seule chose à citer
au support pour qu'il retrouve l'ordre côté SANKORE.

---

## 8. Exploitation courante

| Opération | Docker | Service Windows |
|---|---|---|
| État | `docker ps --filter name=sankore-relay` | `Get-Service SankoreRelayAgent` |
| Journaux | `docker logs --tail 100 -f sankore-relay` | Observateur d'événements, source `Sankore Relay Agent` |
| Appliquer un changement de configuration | `docker restart sankore-relay` | `Restart-Service SankoreRelayAgent` |
| Arrêt propre | `docker stop sankore-relay` | `Stop-Service SankoreRelayAgent` |
| Mise à jour | nouvelle image, `docker stop`/`rm`/`run` | arrêter, remplacer le contenu de `C:\Program Files\Sankore\RelayAgent`, redémarrer |

Le fichier de configuration **n'est pas rechargé à chaud** : toute modification demande un
redémarrage. C'est délibéré — un fichier à moitié enregistré ne doit pas pouvoir reconfigurer un
agent en production.

**Journaux.** Il n'y a pas de rotation intégrée : l'agent écrit sur la sortie standard (Docker) ou
dans le journal d'événements Windows, et c'est à la plateforme d'hébergement de les faire tourner —
d'où les `--log-opt` du §5.1.

**Ce que l'agent ne conserve pas.** Aucun fichier de données, aucune file d'attente, aucun cache.
Un ordre dont la réponse n'a pas pu être renvoyée (session coupée au mauvais moment) est perdu, et
c'est SANKORE qui le rejouera : la plateforme tient déjà le cycle de vie des commandes avec clé
d'idempotence. Bufferiser ici reviendrait à garder vos données sur ce serveur pour refaire, moins
bien, un travail déjà fait ailleurs.

---

## 9. Désinstallation

**Docker :**
```bash
docker stop sankore-relay && docker rm sankore-relay
docker rmi sankore-relay-agent:1.0.0
sudo rm -rf /etc/sankore          # contient le certificat et les mots de passe
```

**Service Windows :**
```powershell
Stop-Service SankoreRelayAgent
sc.exe delete SankoreRelayAgent
Remove-Item -Recurse "C:\Program Files\Sankore\RelayAgent"
Remove-Item -Recurse C:\ProgramData\Sankore\RelayAgent
# Mode magasin : retirer aussi le certificat
Get-ChildItem Cert:\LocalMachine\My | Where-Object Subject -like "*relay*" | Remove-Item
```

**Demandez à SANKORE de révoquer l'agent.** La suppression du service n'invalide pas le
certificat : sans révocation, quelqu'un qui aurait récupéré le `.pfx` pourrait encore ouvrir une
session au nom de votre IMF. La révocation est immédiate et définitive côté plateforme.

---

## 10. Résumé pour votre équipe sécurité

| Question | Réponse |
|---|---|
| Flux entrants à ouvrir ? | **Aucun.** L'agent n'écoute sur aucun port. |
| Flux sortants ? | Un seul : 443/tcp vers le nom d'hôte SANKORE. |
| Authentification ? | TLS mutuel. Certificat client propre à cet agent, clé privée jamais sortie de votre machine, empreinte SHA-256 seule conservée par SANKORE. |
| SANKORE peut-il atteindre une adresse non prévue ? | Non. Chaque adresse, répertoire et vue est déclarée dans votre fichier ; un ordre nomme une cible déclarée, et un chemin qui sortirait de l'URL de base ou un nom de fichier contenant un chemin sont refusés. |
| SANKORE peut-il exécuter du SQL arbitraire ? | Non. Le protocole ne comporte aucun champ pour une requête. L'agent assemble un `SELECT` sur la vue que vous déclarez, filtré sur les seules colonnes que vous autorisez, en transaction `READ ONLY`. |
| SANKORE peut-il écrire dans votre base ? | Non — et vous pouvez le garantir vous-même avec un rôle en `SELECT` seul (§3). |
| Données stockées sur ce serveur ? | Aucune. Traitement en mémoire, rien sur disque, aucune file d'attente. |
| Données personnelles dans les journaux ? | Aucune. Identifiant technique de l'ordre, type, cible, résultat, durée, taille. Les messages d'exception des bibliothèques (qui nomment chemins, colonnes et valeurs) ne sont jamais journalisés : seul leur *type* l'est. |
| Secrets présents sur ce serveur | Le `.pfx` et son mot de passe, les identifiants de vos propres systèmes (clé SSH, clé d'API, chaîne de connexion). Tous dans le fichier de configuration et le répertoire du certificat : protégez ces deux emplacements comme vous protégez le reste. |
| Composants tiers embarqués | SSH.NET (client SFTP), Npgsql (client PostgreSQL), le runtime .NET 10. Rien d'autre. |

---

*Voir aussi : `docs/deployment-dokploy.md` pour le déploiement de la plateforme elle-même, et
`docs/integration-module-plan.md` §5bis(a) et §5ter pour le modèle de confiance de l'enrôlement.*
