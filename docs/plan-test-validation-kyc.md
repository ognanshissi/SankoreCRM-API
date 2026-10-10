# Procédure de validation d'un dossier KYC & plan de test (M02)

Ce document décrit (1) la procédure opérationnelle pour amener un dossier KYC de sa
création jusqu'au statut validé, et (2) le plan de test qui couvre cette procédure.

Toutes les routes sont préfixées `api/v1`. Le groupe est `kyc-files`, y compris pour la
vérification et l'approbation : une vérification n'est pas une ressource, c'est quelque
chose qu'on fait à un dossier.

---

## 1. Rappel du modèle

### 1.1 Les dix statuts internes

| Statut | Sens |
|--------|------|
| `Collecting` | créé, données en cours de saisie |
| `Verifying` | appels biométriques en cours ou en file |
| `Validating` | dans le circuit d'approbation |
| `ComplementRequired` | renvoyé à l'agent avec des points à corriger |
| `Simplified` | KYC simplifié approuvé, plafonds en vigueur |
| `Full` | KYC complet approuvé |
| `UnderReview` | revue périodique ou événementielle en cours |
| `Expired` | revue non faite dans le délai de grâce |
| `Rejected` | fin de relation |
| `Suspended` | blocage conformité |

Un dossier « actif » est donc un dossier en `Simplified` ou `Full` — ce sont les deux
seules cibles de `KycFile.Approve`, la seule méthode qui pose le tier et `ValidatedAt`.

### 1.2 Table de transitions

Déclarative dans `KycFile.Allowed` (`src/Modules/Kyc/Sankore.Modules.Kyc/Domain/KycFile.cs`).
Les arêtes qui comptent pour cette procédure :

```
Collecting         → Verifying
Verifying          → Validating | ComplementRequired
Validating         → Simplified | Full | ComplementRequired | Rejected
ComplementRequired → Verifying | Validating   (Validating UNIQUEMENT via ManuallyValidate)
Rejected, Suspended → (terminal)
```

Aucun handler ne compare les statuts lui-même : il appelle une méthode de l'agrégat, et
la table répond `KYC_INVALID_TRANSITION`.

### 1.3 L'échelle d'approbation

Résolue une seule fois, à l'entrée en `Validating`, par `KycApprovalCircuit` :

| Condition | Échelons |
|-----------|----------|
| Vigilance `Low` | `Agent` |
| Vigilance `Standard` | `Agent`, `BranchManager` |
| Vigilance `High` **ou** doublon suspecté | + `ComplianceOfficer` |
| `FaceMatchAttempts >= face-match-max-attempts` (défaut 2) | + `BranchManager` |
| Dossier validé à la main (`ManuallyValidatedBy` non nul) | + `BranchManager` |

Les deux dernières clauses sont indépendantes du risque : deux comparaisons faciales
échouées disent quelque chose sur **la capture**, pas sur le client.

### 1.4 Quel tier

`DecideKycApprovalHandler.ResolveTierAsync` : `Full` exige **les deux** preuves — une
ligne `KycIdentityDocument` avec un `OcrFieldsJson` non nul **et** un
`KycFaceVerification` avec `IsMatch = true`. Tout le reste est `Simplified`. Le tier
n'est jamais déduit du score de confiance.

---

## 2. Prérequis d'environnement

### 2.1 Biométrie

```jsonc
// src/Bootstrapper/Sankore.Api/appsettings.Development.json
"Kyc": {
  "Biometry": {
    "UseFake": true,              // FakeBiometryClient, aucun Flask requis
    "BaseUrl": "http://localhost:8000"
  }
}
```

`UseFake: true` est le mode de test recommandé : `FakeBiometryClient` est **déterministe**
(aucune horloge, aucun aléa, aucun hash des octets de l'image) et répond toujours :

| Sortie | Valeur |
|--------|--------|
| OCR | CNI ivoirienne — `OUATTARA` / `AWA` / `CI0012345678` / `1987-04-02` |
| Similarité faciale | `0.93` → match (seuil `0.80`) |
| Score global | `82` → niveau `High` (seuils : <50 Low, <80 Medium, ≥80 High) |

Avec `UseFake: false`, il faut le service Flask joignable sur `BaseUrl` **et** un token par
tenant : `PUT /api/v1/kyc-settings/biometry-token`. Sans token, chaque vérification répond
`BIOMETRY_NOT_CONFIGURED` et le dossier reste en `Verifying` pour le replay Hangfire — le
client n'est jamais rejeté pour notre propre panne.

> Le changement d'`appsettings` n'est pas rechargé à chaud : **redémarrer l'API**.

### 2.2 Paramètres tenant (`kyc_settings`)

| Clé | Défaut | Effet sur le test |
|-----|--------|-------------------|
| `face-match-max-attempts` | `2` | au-delà, le chef d'agence s'ajoute au circuit |
| `verification-rejection-floor` | `40` | score en dessous ⇒ niveau `Rejected` ⇒ `ComplementRequired` |

Lecture/écriture : `GET|PUT /api/v1/kyc-settings/{key}` · permission `kyc:settings:manage`.

### 2.3 Deux comptes — c'est le prérequis le plus souvent oublié

| Acteur | Permissions | Rôle |
|--------|-------------|------|
| **A** — agent | `kyc:manage`, `kyc:verify` | ouvre, dépose, vérifie |
| **B** — valideur | `kyc:approve`, `kyc:read` | signe les échelons |

`B` peut signer **tous** les échelons : le handler barre le soumissionnaire, il n'exige pas
un signataire différent par échelon. Deux comptes suffisent, jamais trois.
`RoleSeeder` accorde toutes les permissions aux rôles `System` et `Administrator`.

### 2.4 RabbitMQ

L'activation du client (M01) passe par l'outbox et MassTransit. Sans broker joignable, le
dossier sera validé mais le client restera `PendingKyc` — et rien ne le dira dans la
réponse HTTP.

---

## 3. Procédure nominale (chemin biométrique)

### 3.1 Ouvrir le dossier
```
POST /api/v1/kyc-files
Permission: kyc:manage
Body: { "customerId": "<CLIENT_ID>", "channel": "Agency", "vigilanceLevel": "Standard" }
→ 201 { kycFileId, alreadyExisted: false }   statut: Collecting
```
`200` au lieu de `201` quand un dossier ouvert existait déjà (`alreadyExisted: true`) :
l'intention de l'appelant est satisfaite, mais rien n'a été créé. L'unicité est garantie
par l'index filtré `ux_kyc_files_open_per_customer`, pas par la lecture du handler.

Souvent inutile : `ClientCreatedEvent` (M01) et `KycRequestedIntegrationEvent` (M13,
conversion de lead) déclenchent la même commande.

### 3.2 Déposer les images
```
POST /api/v1/kyc-files/{id}/documents?kind=IdentityDocumentFront
POST /api/v1/kyc-files/{id}/documents?kind=Selfie
Permission: kyc:manage
Corps: multipart/form-data, champ "file"
→ 201 { documentId, storageRef, kind, ... }
```
`kind` ∈ `IdentityDocumentFront` | `IdentityDocumentBack` | `Selfie`. **Conserver les
`storageRef`** : c'est ce que `/verify` attend. En mode fake, n'importe quel JPEG non vide
convient — le double ne lit pas les octets.

### 3.3 Vérifier — c'est cet appel qui fixe `LastSubmittedBy`
```
POST /api/v1/kyc-files/{id}/verify          ← jeton de A
Permission: kyc:verify
Body: { "documentStorageRef": "...", "selfieStorageRef": "...", "documentType": "Cni" }
→ 200 { outcome: "Scored", confidenceScore: 82, confidenceLevel: "High", status: "Validating" }
```
`documentType` ∈ `Cni` | `Passport` | `Cedeao` | `Consulaire` (défaut `Cni`). Il ne peut pas
être déduit : le type est une partie de la *réponse* OCR, donc le service en a besoin en
*entrée* pour choisir son gabarit d'extraction.

Le circuit est créé automatiquement (`StartKycApprovalCommand`) — il n'existe aucune route
pour le démarrer à la main.

### 3.4 Lire le circuit
```
GET /api/v1/kyc-files/{id}/approval     Permission: kyc:read
```
À ne pas sauter : c'est la seule façon de connaître l'échelon en attente, et `level` doit
le désigner exactement.

### 3.5 Signer, dans l'ordre
```
POST /api/v1/kyc-files/{id}/approval/decisions     ← jeton de B
Permission: kyc:approve
Body: { "level": "Agent", "decision": "Approved", "comment": "pièces conformes" }
→ 200 { level: "Agent", decision: "Approved", fileStatus: "Validating", circuitCompleted: false }

Body: { "level": "BranchManager", "decision": "Approved" }
→ 200 { fileStatus: "Full", tier: "Full", circuitCompleted: true }
```
`decision` ∈ `Approved` | `Rejected` | `ComplementRequired`. Seul `Approved` sur le dernier
échelon valide ; `Rejected` et `ComplementRequired` ferment le circuit depuis n'importe quel
échelon.

### 3.6 Contrôler
```
GET /api/v1/kyc-files/{id}                          → status, tier, validatedAt
GET /api/v1/kyc-files/by-customer/{customerId}/caps → plafonds en vigueur
```

### 3.7 Script de bout en bout

```bash
API=http://localhost:5080/api/v1

A=$(curl -s $API/auth/login -H 'Content-Type: application/json' \
     -d '{"email":"agent@tenant.ci","password":"..."}' | jq -r .accessToken)
B=$(curl -s $API/auth/login -H 'Content-Type: application/json' \
     -d '{"email":"admin@tenant.ci","password":"..."}' | jq -r .accessToken)

FILE=$(curl -s $API/kyc-files -H "Authorization: Bearer $A" -H 'Content-Type: application/json' \
  -d '{"customerId":"<CLIENT_ID>","channel":"Agency","vigilanceLevel":"Standard"}' | jq -r .kycFileId)

DOC=$(curl -s "$API/kyc-files/$FILE/documents?kind=IdentityDocumentFront" \
  -H "Authorization: Bearer $A" -F "file=@cni.jpg" | jq -r .storageRef)
SELFIE=$(curl -s "$API/kyc-files/$FILE/documents?kind=Selfie" \
  -H "Authorization: Bearer $A" -F "file=@selfie.jpg" | jq -r .storageRef)

curl -s $API/kyc-files/$FILE/verify -H "Authorization: Bearer $A" -H 'Content-Type: application/json' \
  -d "{\"documentStorageRef\":\"$DOC\",\"selfieStorageRef\":\"$SELFIE\",\"documentType\":\"Cni\"}" | jq

curl -s $API/kyc-files/$FILE/approval -H "Authorization: Bearer $B" | jq

for L in Agent BranchManager; do
  curl -s $API/kyc-files/$FILE/approval/decisions -H "Authorization: Bearer $B" \
    -H 'Content-Type: application/json' \
    -d "{\"level\":\"$L\",\"decision\":\"Approved\"}" | jq -c
done

curl -s $API/kyc-files/$FILE -H "Authorization: Bearer $B" | jq '{status,tier,confidenceScore,validatedAt}'
```

---

## 4. Procédure dégradée — validation manuelle

Pour les deux états qui n'avaient aucune sortie : service biométrique injoignable (dossier
figé en `Verifying`), et capture que le service refuse indéfiniment (boucle
`ComplementRequired` → `Verifying` → `ComplementRequired`).

```
POST /api/v1/kyc-files/{id}/manual-validation
Permission: kyc:document:validate
Body: { "reason": "Document usé, pièces vérifiées au guichet" }
→ statut: Validating
```

Trois conséquences à tester explicitement :

1. `reason` est **obligatoire** → `KYC_MANUAL_VALIDATION_REASON_REQUIRED`. Elle est stockée
   sur l'agrégat (`ManualValidationReason`), pas seulement dans `audit.entries`.
2. Le circuit gagne le `BranchManager`, même sur un dossier `Low` — sinon le dossier serait
   **insignable** : son seul échelon serait `Agent` et la seule personne interdite de le
   signer serait justement celle qui vient de valider.
3. `LastSubmittedBy` n'est **pas** touché. Le valideur est enregistré dans
   `ManuallyValidatedBy`, second point d'ancrage des quatre yeux.
4. Le tier sera `Simplified` : sans score, il n'y a ni lecture OCR ni face-match.

---

## 5. Plan de test

### 5.1 Chemin nominal

| ID | Cas | Action | Attendu |
|----|-----|--------|---------|
| K-01 | Ouverture | `POST kyc-files` sur un client sans dossier | `201`, statut `Collecting`, `alreadyExisted: false` |
| K-02 | Unicité | rejouer K-01 | `200`, `alreadyExisted: true`, **même** `kycFileId` |
| K-03 | Dépôt recto | `POST documents?kind=IdentityDocumentFront` | `201`, `storageRef` non vide |
| K-04 | Dépôt selfie | `POST documents?kind=Selfie` | `201` |
| K-05 | Vérification | `POST verify` (fake) | `confidenceScore: 82`, `confidenceLevel: High`, statut `Validating` |
| K-06 | Circuit créé | `GET approval` | 2 échelons `Agent`, `BranchManager`, tous `Pending` |
| K-07 | 1ère signature | `decisions` `Agent`/`Approved` par B | `200`, statut inchangé `Validating`, `circuitCompleted: false` |
| K-08 | 2ème signature | `decisions` `BranchManager`/`Approved` par B | statut `Full`, `tier: Full`, `circuitCompleted: true`, `validatedAt` renseigné |
| K-09 | Activation client | `GET clients/{id}` après K-08 | statut `Active` (requiert RabbitMQ) |
| K-10 | Plafonds | `GET by-customer/{id}/caps` | plafonds du tier `Full` |

### 5.2 Quatre yeux — le cœur du dispositif

| ID | Cas | Action | Attendu |
|----|-----|--------|---------|
| K-20 | Auto-approbation, échelon final | A signe `BranchManager` | `KYC_SELF_APPROVAL_FORBIDDEN` |
| K-21 | Auto-approbation, échelon intermédiaire | A signe `Agent` | `KYC_SELF_APPROVAL_FORBIDDEN` — l'échelon intermédiaire ne touche aucune méthode de l'agrégat, le handler le vérifie lui-même |
| K-22 | Auto-refus | A signe `Rejected` | `KYC_SELF_APPROVAL_FORBIDDEN` |
| K-23 | Second ancrage | A valide à la main puis signe `Agent` | `KYC_SELF_APPROVAL_FORBIDDEN` sur `ManuallyValidatedBy` |
| K-24 | Aucune signature enregistrée | après K-20 : `GET approval` | l'échelon est resté `Pending` — le dossier est déplacé **avant** le marquage de l'échelon, jamais l'inverse |

### 5.3 Ordre et concurrence

| ID | Cas | Action | Attendu |
|----|-----|--------|---------|
| K-30 | Saut d'échelon | signer `BranchManager` avant `Agent` | `409 KYC_APPROVAL_OUT_OF_ORDER` |
| K-31 | Écran périmé | signer un échelon déjà décidé | `409 KYC_APPROVAL_STEP_ALREADY_DECIDED` |
| K-32 | Dossier sans circuit | `decisions` sur un dossier en `Collecting` | `KYC_APPROVAL_OUT_OF_ORDER` |
| K-33 | Décision `Pending` | `decision: "Pending"` | `KYC_INVALID_TRANSITION` |

`409` et non `400` pour K-30/K-31 : la requête était bien formée, elle est juste arrivée en
retard — le front recharge au lieu de demander à l'utilisateur de corriger un champ.

### 5.4 Échelles d'approbation

| ID | Vigilance / état | Échelons attendus |
|----|------------------|-------------------|
| K-40 | `Low` | `Agent` seul |
| K-41 | `Standard` | `Agent`, `BranchManager` |
| K-42 | `High` | `Agent`, `BranchManager`, `ComplianceOfficer` |
| K-43 | `Low` + doublon suspecté | `Agent`, `BranchManager`, `ComplianceOfficer` (le flag monte la vigilance à `High`) |
| K-44 | `Low` + `FaceMatchAttempts = 2` | `Agent`, `BranchManager` — la seule clause qui ajoute une signature à un dossier `Low` |
| K-45 | `Low` + validation manuelle | `Agent`, `BranchManager` |
| K-46 | Pas de doublon | lever le flag via `POST {id}/duplicate-flag/clear` puis relire | le `ComplianceOfficer` d'un circuit **déjà créé** ne disparaît pas : le circuit ne change pas sous les pieds d'un approbateur |

### 5.5 Tier

| ID | Preuves présentes | Tier attendu |
|----|-------------------|--------------|
| K-50 | lecture OCR + face-match `true` | `Full` |
| K-51 | lecture OCR seule | `Simplified` |
| K-52 | face-match seul | `Simplified` |
| K-53 | validation manuelle (aucune preuve) | `Simplified` |
| K-54 | score 95 sans face-match | `Simplified` — le tier ne vient pas du score |

### 5.6 Sorties non nominales de la vérification

Forcer via `FakeBiometryClient.Rejecting(code)` / `.Unavailable(code)` en test, ou
`UseFake: false` sans service pour le cas indisponible.

| ID | Cas | Attendu |
|----|-----|---------|
| K-60 | Capture refusée (photo illisible) | statut `ComplementRequired`, `outcome: CaptureRejected`, **aucune** évaluation de confiance écrite |
| K-61 | Service indisponible | statut reste `Verifying`, `outcome: ServiceUnavailable`, `ReplayKycVerificationJob` **planifié avec un délai** (et non enfilé immédiatement : le stockage Hangfire n'est pas dans cette transaction) |
| K-62 | Score sous le plancher (`< 40`) | niveau `Rejected` → `ComplementRequired` |
| K-63 | Face non match malgré un bon score | niveau `Rejected` → `ComplementRequired` |
| K-64 | Sortie de `ComplementRequired` par la machine | `POST verify` → `Verifying` → `Validating` |
| K-65 | Sortie de `ComplementRequired` à la main | `POST manual-validation` → `Validating` |
| K-66 | Panne enregistrée comme refus | **ne doit pas arriver** : une indisponibilité n'écrit aucun rejet (on ne rejette pas un client honnête pour notre propre panne) |

### 5.7 Garde-fous et étanchéité

| ID | Cas | Attendu |
|----|-----|---------|
| K-70 | Dossier d'un autre tenant | `404 KYC_FILE_NOT_FOUND`, jamais `403` — l'existence d'un client ne doit pas fuir |
| K-71 | Dossier hors périmètre d'agence | `404`, même raison |
| K-72 | Dépôt sur un dossier décidé | `409 KYC_INVALID_TRANSITION` + le statut courant |
| K-73 | Fichier vide | `400 KYC_DOCUMENT_EMPTY` |
| K-74 | `storageRef` inconnu dans `verify` | `KYC_VERIFICATION_IMAGE_NOT_FOUND`, **et aucune transition** : les images sont chargées avant que le dossier bouge |
| K-75 | Numéro de document | aucune réponse de `verify`, `documents` ou `approval` ne le contient — il a sa propre route derrière `kyc:document:reveal` |
| K-76 | Token biométrique | `GET kyc-settings/biometry-token` ne renvoie qu'un indice masqué, jamais la valeur |
| K-77 | Édition concurrente | deux validations manuelles simultanées → `KYC_CONCURRENCY_CONFLICT` (jeton `xmin`) |

### 5.8 Effets de bord inter-modules

| ID | Cas | Attendu |
|----|-----|---------|
| K-80 | Client `PendingKyc` | `KycValidatedEvent` → `Active` + `ClientActivatedEvent` |
| K-81 | Client `Suspended` | **seul** `KycStatus` est rafraîchi : aucune activation, aucun événement — une approbation automatique ne contredit pas une décision humaine |
| K-82 | Client `Archived` | idem K-81 |
| K-83 | Rejeu du même événement | garde d'inbox : aucune seconde ligne d'historique, aucun second `ClientActivatedEvent` |
| K-84 | Changement de tier | `KycTierChangedEvent` avec `previousTier: "None"` sur une première validation |
| K-85 | Refus | `KycRejectedEvent`, `reason` = le commentaire de l'approbateur, ou `KYC_APPROVAL_REJECTED` s'il n'en a pas laissé |
| K-86 | Miroir M12 | `WorkflowInstanceId` renseigné si un template existe ; une instance absente **n'empêche pas** la validation (best-effort) |
| K-87 | Demande de complément | l'instance M12 est annulée et le lien effacé, la passe suivante en ouvre une neuve |

### 5.9 Tests automatisés existants

```bash
dotnet test src/Modules/Kyc/Sankore.Modules.Kyc.Tests
```

| Classe | Couvre |
|--------|--------|
| `KycFileTests` | table de transitions, quatre yeux dans l'agrégat |
| `DecideKycApprovalHandlerTests` | ordre, finalité, résolution du tier |
| `FourEyesOnEveryLevelTests` | §5.2, y compris les échelons intermédiaires |
| `KycApprovalCircuitTests` | §5.4 |
| `FaceAttemptRequirementTests` | K-44 |
| `ManuallyValidateKycFileHandlerTests` | §4 |
| `RunKycVerificationHandlerTests` | §5.6 |
| `RejectionFloorTests` | K-62 |
| `KycDuplicateDetectorTests`, `ClearDuplicateFlagHandlerTests` | K-43, K-46 |
| `GetKycFileAgencyPerimeterTests` | K-71 |
| `KycStatusMappingTests` | projection sur les six statuts publics |
| `KycWorkflowMirrorTests` | K-86, K-87 |
| `UploadKycDocumentTests`, `ReadKycDocumentTests` | K-72 à K-75 |

Les tests tournent sur EF InMemory + `FakeBiometryClient` : aucune dépendance externe, pas
de Flask, pas de Postgres.

Un cas à ouvrir en test manuel plutôt qu'automatisé : **K-09** et **§5.8** dépendent du
broker, donc d'un environnement intégré.

---

## 6. Les quatre pièges, par fréquence

1. **`KYC_SELF_APPROVAL_FORBIDDEN`** — vous signez avec le compte qui a appelé `/verify`.
   C'est `LastSubmittedBy`, posé par la vérification, qui barre, y compris sur l'échelon
   `Agent`. Il faut deux comptes.
2. **`KYC_APPROVAL_OUT_OF_ORDER`** — `level` n'est pas l'échelon en attente. Relire
   `GET {id}/approval` plutôt que deviner.
3. **Dossier figé en `Verifying`** — `UseFake: false` sans Flask ni token. Mettre
   `UseFake: true` et redémarrer, ou poser le token.
4. **Dossier validé, client toujours `PendingKyc`** — RabbitMQ injoignable. Le bus retente
   en boucle en écrivant `Connection refused` dans les logs pendant que l'API continue de
   répondre : le symptôme se lit comme du bruit.

---

## 7. Fichiers de référence

| Sujet | Fichier |
|-------|---------|
| Agrégat, transitions, quatre yeux | `src/Modules/Kyc/Sankore.Modules.Kyc/Domain/KycFile.cs` |
| Échelle d'approbation | `Features/Approval/KycApprovalCircuit.cs` |
| Décision, ordre, tier | `Features/Approval/Decide/DecideKycApprovalHandler.cs` |
| Vérification biométrique | `Features/Verification/RunVerification/RunKycVerificationHandler.cs` |
| Validation manuelle | `Features/Verification/ManualValidation/ManuallyValidateKycFileHandler.cs` |
| Double biométrique | `Infrastructure/Biometry/FakeBiometryClient.cs` |
| Codes d'erreur | `Domain/KycErrors.cs` |
| Paramètres tenant | `Domain/KycSettingKeys.cs` |
| Activation du client | `src/Modules/Customers/.../Lifecycle/Consumers/KycValidatedConsumer.cs` |
