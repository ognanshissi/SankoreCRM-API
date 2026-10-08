# Procédure de qualification d'un lead & plan de test (M13)

Ce document décrit (1) la procédure pour amener un lead de sa capture jusqu'au statut
`Qualified` puis à sa conversion, et (2) le plan de test qui couvre cette procédure.

Toutes les routes sont préfixées `api/v1` et le groupe est `leads`.
Le parcours complet des endpoints du module est dans `workflow-complet-leads.md` ; ce
document-ci ne traite que la qualification et ce qui l'encadre.

---

## 1. Rappel du modèle

### 1.1 Statuts

```
New → Open → Qualifying → Qualified → Converted
                   ↘ Nurturing ⇄ Recycled
                   ↘ Lost | Disqualified | Archived   (terminaux)
```

`Lead.Qualify(score, seuil)` ne connaît que deux issues :

| Condition | Statut résultant |
|-----------|------------------|
| `score >= seuil` | `Qualified` |
| `score < seuil` | `Qualifying` |

Elle refuse tout lead déjà `Converted`, `Archived`, `Lost` ou `Disqualified`
(`LEAD_CANNOT_BE_QUALIFIED_FROM_CURRENT_STATUS`), et tout score hors `[0, 100]`
(`SCORE_OUT_OF_RANGE`).

### 1.2 Le seuil est configurable

`Leads:QualificationThreshold` — **défaut 60**, mais **35 en développement** :

```jsonc
// src/Bootstrapper/Sankore.Api/appsettings.Development.json
"Leads": {
  "AutoDispatchOnCapture": true,
  "QualificationThreshold": 35
}
```

**Tout plan de test doit vérifier la valeur en vigueur avant d'interpréter un statut** :
le même lead à 45 points est `Qualifying` avec le seuil historique et `Qualified` en dev.

### 1.3 Le dispatch ne requiert PAS `Qualified`

`Lead.IsDispatchable` est la définition unique, partagée par `Lead.AssignTo` et
`DispatchLeadHandler` : tout lead vivant peut être dispatché, seuls les statuts terminaux
refusent (`LEAD_NOT_DISPATCHABLE`). Router un lead capturé vers un agent est précisément
la façon dont il se fait qualifier.

### 1.4 Le barème automatique (0-100)

`LeadScoreCalculator` — six dimensions, chacune sérialisée dans `FactorsJson` pour
l'explicabilité :

| # | Dimension | Max | Détail du barème |
|---|-----------|-----|------------------|
| 1 | Complétude du profil | 25 | prénom+nom 4, email 3, date de naissance 3, pièce d'identité 4, montant souhaité 3, raison sociale 3, + `QualificationCompleteness × 5` |
| 2 | Qualité de la source | 20 | `Agency` 20 · `CallCenter`/`Referral` 18 · `WhatsApp`/`MobileAgent` 15 · `Web`/`Partner` 12 · `Sms`/`Ussd`/`Campaign` 10 · `FileImport` 5 · autre 8 |
| 3 | Interactions | 20 | volume : 0→0, 1→3, 2-3→6, 4-6→9, 7+→12 ; récence de la dernière : ≤7j→8, ≤30j→5, ≤90j→2, sinon 1 |
| 4 | Comportement | 15 | ratio d'issues positives (`Interested`/`Completed`/`Reached`) : >75%→10, >50%→8, >25%→5, >0→3 ; + 5 si ≥2 types d'activité distincts |
| 5 | Cohérence déclarative | 10 | pièce d'identité de 5-20 caractères 3, téléphone de 7-15 chiffres 3, montant souhaité > 0 4 |
| 6 | Localisation | 10 | point géographique 5, agence préférée 5 |

**Conséquence structurante : 35 des 100 points (dimensions 3 et 4) viennent de
l'historique d'activité, qui est vide à la capture.** Les plafonds mesurés sur un lead
tout juste capturé sont donc :

| Cas | Calcul | Total |
|-----|--------|-------|
| Guichet complet, personne morale (`Agency`) | 20 + 20 + 0 + 0 + 10 + 10 | **60** |
| Guichet complet, personne physique (pas de raison sociale) | 17 + 20 + 0 + 0 + 10 + 10 | **57** |
| Import de fichier complet (`FileImport`) | 20 + 5 + 0 + 0 + 10 + 10 | **45** |
| Une ligne complète de `sample-lead-import.xlsx` | — | **30** |

Autrement dit : avec le seuil historique de 60, **aucun lead ne peut atteindre `Qualified`
à la capture sauf le cas guichet-personne-morale parfaitement renseigné**. C'est voulu, et
c'est la raison du seuil abaissé à 35 en dev.

### 1.5 Niveau d'intention et action suivante

Dérivés du score par `QualifyLeadHandler`, indépendamment du seuil configuré :

| Score | `IntentLevel` | `NextAction` |
|-------|---------------|--------------|
| ≥ 80 | `Hot` | `DispatchToAgent` |
| 60-79 | `Warm` | `DispatchToAgent` |
| 40-59 | `Cold` | `CollectMoreData` |
| < 40 | `Unknown` | `Disqualify` |

> **Piège de lecture** : ces paliers sont **codés en dur à 60/40**, alors que le statut
> suit `Leads:QualificationThreshold`. En dev (seuil 35), un lead à 45 est donc
> `Qualified` **avec** `nextAction: CollectMoreData`. Ce n'est pas une incohérence de
> données, c'est le seuil configuré qui s'écarte des paliers d'intention.

### 1.6 Les trois chemins de `POST {id}/qualify`

Évalués dans cet ordre par le handler :

| # | Déclencheur | Score |
|---|-------------|-------|
| 1 | `templateId` **et** `answers` fournis | `earnedWeight / totalWeight × 100`, et `QualificationCompleteness` recalculée |
| 2 | `score` fourni (sans template) | la valeur telle quelle — override explicite |
| 3 | rien de tout cela | `LeadScoreCalculator` sur les attributs + l'historique |

Un `score` envoyé **en même temps** qu'un `templateId` est ignoré.

---

## 2. Prérequis

### 2.1 Permissions

| Acteur | Permissions |
|--------|-------------|
| Agent de capture | `lead:create` |
| Agent commercial | `lead:qualify`, `lead:activity:log`, `lead:read` |
| Chef d'équipe | `lead:assign`, `lead:convert` |
| Administrateur | `lead:qualification-template:manage`, `lead:scoring-config:manage` |

Un seul compte suffit pour tout le parcours — contrairement à la validation KYC, la
qualification n'a pas de règle de quatre yeux.

### 2.2 Configuration préalable (facultative mais recommandée)

| Objet | Route | Permission |
|-------|-------|------------|
| Étapes de pipeline | `POST pipeline-stages` | `lead:pipeline-stage:manage` |
| Règle de dispatching | `POST dispatching-rules` puis `/{id}/activate` | `lead:dispatching-rule:manage` |
| Modèle de qualification | `POST qualification-templates` puis `/{id}/publish` | `lead:qualification-template:manage` |

Le dispatching **fonctionne avec une table de règles vide** (un test l'épingle) :
`DispatchingRuleResolver` retombe sur `DispatchingRule.Default()`. Et
`DispatchingRuleSeeder` donne à chaque tenant une règle visible « Par défaut » —
uniquement si le tenant n'a **aucune** règle, pour ne pas défaire la suppression
délibérée d'un administrateur.

### 2.3 Attention à l'auto-dispatch en dev

`Leads:AutoDispatchOnCapture` est à `true` dans `appsettings.Development.json`.
`CaptureLeadHandler` publie alors `LeadCapturedEvent` **par l'outbox**, et
`LeadAutoDispatchConsumer` score, qualifie et dispatche **hors bande**. Deux effets sur un
plan de test :

- un lead peut arriver déjà `Qualified` et déjà assigné **sans** que vous ayez appelé
  `/qualify` — ne testez pas le chemin manuel avec l'auto-dispatch actif, ou vous
  mesurerez l'autre ;
- le consommateur ignore un lead capturé avec un propriétaire, un doublon suspecté, et
  tout lead déjà assigné (son idempotence est `CurrentAssignmentId`, pas une table d'inbox).

Pour tester le chemin manuel, mettre `AutoDispatchOnCapture: false` et redémarrer.

---

## 3. Procédure nominale

### 3.1 Capturer
```
POST /api/v1/leads
Permission: lead:create
Body: {
  "fullName": "Awa Ouattara", "firstName": "Awa", "lastName": "Ouattara",
  "phoneNumber": "+2250708091011", "email": "awa@example.ci",
  "source": "Agency", "interestedProduct": "CREDIT_PME", "preferredLanguage": "fr",
  "latitude": 5.300489, "longitude": -4.016107, "preferredAgencyId": "<AGENCY_ID>",
  "dateOfBirth": "1987-04-02", "nationalId": "CI0012345678",
  "desiredAmount": 2500000, "desiredCurrency": "XOF",
  "gender": "Female", "prospectType": "Individual"
}
→ 201 { leadId, duplicateDetected, ... }   statut: New
```
Champs obligatoires : `fullName`, `phoneNumber`, `source`, `interestedProduct`,
`preferredLanguage`, `latitude`, `longitude`.

La porte anti-doublon est réglable dans le corps : `gateMode` `Block` (défaut, `409` sur
doublon) ou `Warn` (`201` + `duplicateDetected: true`), `minConfidenceThreshold` (défaut
30), `force: true` pour la contourner.

`leadSourceConfigId` est **absent du contrat et ne peut pas être envoyé** : il est posé par
le serveur. Un client capable de le fixer pourrait désigner n'importe quelle source du
tenant, hériter de sa règle de dispatching — donc choisir quel pool d'agents reçoit ses
leads — et imputer son `CostPerLead` à cette source.

### 3.2 Alimenter l'historique — c'est ce qui débloque 35 points
```
POST /api/v1/leads/{id}/activities
Permission: lead:activity:log
Body: { "type": "Call", "subject": "Premier contact", "outcome": "Reached" }
Body: { "type": "Meeting", "subject": "RDV agence", "outcome": "Interested" }
```
`type` ∈ `Call` | `Meeting` | `Email` | `Visit` | `Note` | `Sms` | `WhatsApp` | `Task`.
`outcome` ∈ `Reached` | `NoAnswer` | `Voicemail` | `Callback` | `Interested` |
`NotInterested` | `Rescheduled` | `Completed`.

Avec ces deux activités : interactions 6 + 8 = 14, comportement 10 + 5 = 15 → **+29 points**.
Deux types distincts sont nécessaires pour les 5 points multi-canal.

### 3.3 Qualifier
```
POST /api/v1/leads/{id}/qualify
Permission: lead:qualify
Body: {}                                    ← chemin 3, barème automatique
→ 200 { score, status, intentLevel, factorsJson, nextAction, nextActionDetail }
```
Variantes :
```jsonc
// chemin 1 — par modèle
{ "templateId": "<TPL>", "answers": [ { "questionId": "<Q1>", "value": "true" } ] }

// chemin 2 — override explicite
{ "score": 75, "triggerEvent": "MANUAL_REVIEW" }

// protection contre l'édition concurrente
{ "expectedUpdatedAt": "2026-10-08T09:12:33.1234567+00:00" }
```
Chaque appel écrit une ligne dans `ScoreHistory` avec `factorsJson` — relisible par
`GET {id}/score-history`.

### 3.4 Vérifier
```
GET /api/v1/leads/{id}                 → status, score, intentLevel
GET /api/v1/leads/{id}/score-history   → toutes les passes, avec le détail par dimension
GET /api/v1/leads/{id}/next-action     → action recommandée
```

### 3.5 Dispatcher
```
GET  /api/v1/leads/{id}/dispatch-preview     Permission: lead:assign  ← à blanc
POST /api/v1/leads/{id}/dispatch             Permission: lead:assign
Body: {}                                      ← stratégie résolue depuis le lead
Body: { "agentId": "<AGENT>", "overrideReason": "Client demande son conseiller" }
```
Quelle règle s'applique est résolu par `DispatchingRuleResolver` : la règle épinglée sur
la source d'origine du lead (`LeadSourceConfig.DefaultDispatchingRuleId`, atteinte par
`Lead.LeadSourceConfigId`), sinon la règle active de plus haute priorité, sinon
`DispatchingRule.Default()`. La règle porte la stratégie. `LeadAssignment.RuleId`
enregistre laquelle a produit l'assignation (`null` = valeurs par défaut intégrées).

### 3.6 Premier contact, puis convertir
```
POST /api/v1/leads/{id}/first-contact   Permission: lead:assign   ← arrête le SLA
POST /api/v1/leads/{id}/convert         Permission: lead:convert
Body: {}                                 ← ou { "customerId": "<EXISTANT>" }
→ statut Converted, pipelineStage Converted
```
La conversion publie `LeadConvertedIntegrationEvent` **et**
`KycRequestedIntegrationEvent` — c'est ce second événement qui fait ouvrir un dossier KYC
par M02 (voir `plan-test-validation-kyc.md`). `Lead.Convert` refuse seulement les leads
`Lost`, `Disqualified` et `Archived` : **un lead non qualifié est convertible**.

### 3.7 Script de bout en bout

```bash
API=http://localhost:5080/api/v1
T=$(curl -s $API/auth/login -H 'Content-Type: application/json' \
     -d '{"email":"agent@tenant.ci","password":"..."}' | jq -r .accessToken)
H=(-H "Authorization: Bearer $T" -H 'Content-Type: application/json')

LEAD=$(curl -s $API/leads "${H[@]}" -d '{
  "fullName":"Awa Ouattara","firstName":"Awa","lastName":"Ouattara",
  "phoneNumber":"+2250708091011","email":"awa@example.ci","source":"Agency",
  "interestedProduct":"CREDIT_PME","preferredLanguage":"fr",
  "latitude":5.300489,"longitude":-4.016107,"preferredAgencyId":"<AGENCY_ID>",
  "dateOfBirth":"1987-04-02","nationalId":"CI0012345678",
  "desiredAmount":2500000,"desiredCurrency":"XOF"}' | jq -r .leadId)

# Score à la capture (attendu 57 : personne physique, pas de raison sociale)
curl -s $API/leads/$LEAD/qualify "${H[@]}" -d '{}' | jq '{score,status,intentLevel,nextAction}'

# Deux activités de types distincts, issues positives → +29
curl -s $API/leads/$LEAD/activities "${H[@]}" \
  -d '{"type":"Call","subject":"Premier contact","outcome":"Reached"}' > /dev/null
curl -s $API/leads/$LEAD/activities "${H[@]}" \
  -d '{"type":"Meeting","subject":"RDV agence","outcome":"Interested"}' > /dev/null

# Re-score : attendu 86 → Hot, Qualified quel que soit le seuil
curl -s $API/leads/$LEAD/qualify "${H[@]}" -d '{}' | jq '{score,status,intentLevel}'
curl -s $API/leads/$LEAD/score-history "${H[@]}" | jq '.[].factorsJson'

curl -s $API/leads/$LEAD/dispatch-preview "${H[@]}" | jq
curl -s $API/leads/$LEAD/dispatch "${H[@]}" -d '{}' | jq
curl -s $API/leads/$LEAD/first-contact "${H[@]}" -d '{}' | jq
curl -s $API/leads/$LEAD/convert "${H[@]}" -d '{}' | jq
```

---

## 4. Plan de test

### 4.1 Barème automatique — valeurs attendues

À exécuter avec un seuil connu. Les scores ci-dessous sont **déterministes** : le barème
n'a ni horloge ni aléa (seule la dimension 3 dépend du temps, par paliers de 7/30/90 jours).

| ID | Lead | Score attendu | Détail |
|----|------|---------------|--------|
| L-01 | Guichet, personne morale, tous champs | **60** | 20+20+0+0+10+10 |
| L-02 | Guichet, personne physique, tous champs | **57** | 17+20+0+0+10+10 |
| L-03 | Import de fichier, tous champs | **45** | 20+5+0+0+10+10 |
| L-04 | Web, champs obligatoires seuls | **20** | 0+12+0+0+3+5 |
| L-05 | L-02 + 2 activités `Reached`/`Interested`, types distincts | **86** | 57 + (6+8) + (10+5) |
| L-06 | L-02 + 1 activité unique `NoAnswer` | **68** | 57 + (3+8) + (0+0) — `NoAnswer` n'est pas une issue positive |
| L-07 | L-02 sans agence préférée | **52** | dimension 6 à 5 au lieu de 10 |
| L-08 | Activité de plus de 90 jours | récence = 1 | tester le palier le plus bas |

Deux remarques sur L-04 : `fullName` seul ne rapporte rien en dimension 1, qui exige
`firstName` **et** `lastName` ; et la dimension 6 ne peut jamais être nulle sur un lead
capturé, puisque `CaptureLeadHandler` construit toujours un `GeoPoint` depuis les
`latitude`/`longitude` obligatoires — seuls les 5 points de l'agence préférée sont en jeu.

### 4.2 Statut et seuil

| ID | Seuil | Score | Statut attendu |
|----|-------|-------|----------------|
| L-10 | 60 | 60 | `Qualified` (comparaison `>=`) |
| L-11 | 60 | 59 | `Qualifying` |
| L-12 | 35 (dev) | 45 | `Qualified` |
| L-13 | 35 (dev) | 34 | `Qualifying` |
| L-14 | 60 | 45 | `Qualifying` — **le même lead que L-12** |

L-12 et L-14 sont le même lead sous deux configurations : c'est le test qui empêche de
conclure trop vite qu'un statut est un bug.

### 4.3 Niveau d'intention et action suivante

| ID | Score | `intentLevel` | `nextAction` |
|----|-------|---------------|--------------|
| L-20 | 85 | `Hot` | `DispatchToAgent` |
| L-21 | 60 | `Warm` | `DispatchToAgent` |
| L-22 | 45 | `Cold` | `CollectMoreData` |
| L-23 | 20 | `Unknown` | `Disqualify` |
| L-24 | 45, seuil 35 | `Cold` | `CollectMoreData` **alors que le statut est `Qualified`** — paliers codés en dur vs seuil configuré |

### 4.4 Les trois chemins de `/qualify`

| ID | Corps | Attendu |
|----|-------|---------|
| L-30 | `{}` | barème automatique, `factorsJson` avec les 6 dimensions |
| L-31 | `{ "score": 75 }` | score 75 exactement, `factorsJson` = `{}` |
| L-32 | `{ "score": 101 }` | `SCORE_OUT_OF_RANGE` |
| L-33 | `{ "score": -1 }` | `SCORE_OUT_OF_RANGE` |
| L-34 | `templateId` + `answers` complètes | score = `earnedWeight / totalWeight × 100`, `QualificationResponse` créée |
| L-35 | `templateId` + `score` ensemble | le `score` est **ignoré**, le modèle gagne |
| L-36 | `templateId` d'un modèle en brouillon | `QUALIFICATION_TEMPLATE_NOT_FOUND` — seuls les `Published` sont résolus |
| L-37 | `templateId` sans `answers` | bascule sur le chemin automatique (les deux sont requis ensemble) |
| L-38 | question obligatoire non répondue | `REQUIRED_QUESTIONS_NOT_ANSWERED` |
| L-39 | question masquée par une règle | exclue du dénominateur **et** de la complétude |
| L-40 | question rendue obligatoire par une règle | même refus que L-38 |

### 4.5 Barème par modèle

| ID | Type de question | Réponse | Points gagnés |
|----|------------------|---------|---------------|
| L-50 | `YesNo`, poids 10 | `"true"` | 10 |
| L-51 | `YesNo`, poids 10 | `"false"` | 0 |
| L-52 | `SingleChoice`, poids 10 | une option | 10 (le poids entier, quelle que soit l'option) |
| L-53 | `MultiChoice`, poids 10 | ≥1 option | 10 |
| L-54 | `Numeric` poids 10, min 0 max 100 | `"50"` | 5 (ratio × poids) |
| L-55 | `Numeric` poids 10, sans bornes | `"7"` | 10 (tout positif vaut le poids) |
| L-56 | `Numeric` poids 10, sans bornes | `"0"` | 0 |
| L-57 | `Numeric` | `"abc"` | 0 |
| L-58 | `Text`, poids 10 | texte non vide | 10 |
| L-59 | n'importe lequel | `""` ou blanc | 0 |
| L-60 | modèle à poids total 0 | — | score 0, pas de division par zéro |

### 4.6 Garde-fous de statut et concurrence

| ID | Cas | Attendu |
|----|-----|---------|
| L-70 | Qualifier un lead `Converted` | `LEAD_CANNOT_BE_QUALIFIED_FROM_CURRENT_STATUS` |
| L-71 | Qualifier un lead `Lost` / `Disqualified` / `Archived` | idem |
| L-72 | Qualifier un lead `Nurturing` | **autorisé** |
| L-73 | `expectedUpdatedAt` périmé | `CONFLICT` |
| L-74 | `expectedUpdatedAt` absent | pas de contrôle de concurrence |
| L-75 | Lead d'un autre tenant | `LEAD_NOT_FOUND` — filtre global, l'existence ne fuit pas |
| L-76 | Re-qualifier plusieurs fois | une ligne `ScoreHistory` par passe, aucune écrasée |

### 4.7 Dispatch

| ID | Cas | Attendu |
|----|-----|---------|
| L-80 | Dispatcher un lead `New` non qualifié | **succès** — `IsDispatchable` n'exige pas `Qualified` |
| L-81 | Dispatcher un lead `Converted` | `LEAD_NOT_DISPATCHABLE` |
| L-82 | Aucun agent éligible | `NO_AGENT_AVAILABLE` |
| L-83 | `agentId` explicite non éligible | `AGENT_NOT_ELIGIBLE` |
| L-84 | `agentId` exclu par la règle | `AGENT_EXCLUDED_BY_RULE` |
| L-85 | Tous les agents à capacité | `ALL_AGENTS_AT_TASK_CAPACITY` |
| L-86 | Plafond anti-monopole atteint | `ANTI_MONOPOLY_BLOCKED` |
| L-87 | Table de règles vide | succès via `DispatchingRule.Default()`, `LeadAssignment.RuleId` à `null` |
| L-88 | Règle épinglée sur la source | c'est elle qui est choisie, pas la plus prioritaire |
| L-89 | `dispatch-preview` | aucun effet de bord : rien n'est assigné |
| L-90 | Re-dispatch | l'assignation précédente est clôturée, pas dupliquée |

### 4.8 Auto-dispatch à la capture

À tester avec `Leads:AutoDispatchOnCapture: true`.

| ID | Cas | Attendu |
|----|-----|---------|
| L-100 | Capture simple | le lead finit assigné **sans** appel à `/qualify` ni `/dispatch` |
| L-101 | Capture avec `ownerId` | ignoré par le consommateur |
| L-102 | Capture avec doublon suspecté | ignoré |
| L-103 | Lead déjà assigné | ignoré (idempotence sur `CurrentAssignmentId`) |
| L-104 | Aucun agent disponible | **le lead persiste quand même** — le dispatch est hors transaction |
| L-105 | Import de 400 lignes | 400 captures, dispatch hors bande, pas 400 dispatches synchrones dans la boucle |

### 4.9 Conversion

| ID | Cas | Attendu |
|----|-----|---------|
| L-110 | Convertir un lead `Qualified` | statut `Converted`, `convertedToCustomerId` renseigné |
| L-111 | Convertir un lead non qualifié | **autorisé** — seuls les statuts clos refusent |
| L-112 | Convertir un lead `Lost` | refus |
| L-113 | Lead sans agence | `LEAD_HAS_NO_AGENCY` à la création du client |
| L-114 | `customerId` fourni mais inexistant | `CUSTOMER_NOT_FOUND` |
| L-115 | Doublon à la conversion | `409` sauf `force: true` (seuil par défaut 70) |
| L-116 | Effet aval | `LeadConvertedIntegrationEvent` **et** `KycRequestedIntegrationEvent` publiés |
| L-117 | Dossier KYC | M02 ouvre un dossier pour le client issu de la conversion |

### 4.10 Tests automatisés existants

```bash
dotnet test src/Modules/Leads/Sankore.Modules.Leads.Tests
dotnet test src/Modules/Leads/Sankore.Modules.Leads.Tests --filter "FullyQualifiedName~QualificationThresholdTests"
```

| Classe | Couvre |
|--------|--------|
| `QualificationThresholdTests` | §4.2 |
| `QualifyLeadValidatorTests` | §4.4, validation d'entrée |
| `LeadDispatchabilityTests` | L-80, L-81 |
| `DispatchLeadHandlerTests` | §4.7 |
| `DispatchLeadExplicitAgentTests` | L-83, L-84 |
| `DispatchingRuleResolverTests` | L-88 |
| `DispatchingRuleSeederTests` | L-87 |
| `PreviewDispatchHandlerTests` | L-89 |
| `CompatibilityScorerTests` | pondérations de la stratégie de compatibilité |
| `ConvertLeadHandlerTests` | §4.9 |
| `ImportAutoDispatchEndToEndTests` | §4.8 |
| `CaptureLeadSourceConfigTests` | `LeadSourceConfigId` posé par le serveur |
| `DuplicateQualificationTemplateHandlerTests`, `UnarchiveAndRepublishTests` | cycle de vie des modèles |

EF InMemory + NSubstitute, aucune dépendance externe.

---

## 5. Les pièges, par fréquence

1. **Un score plafonné à 60 à la capture.** 35 points viennent de l'historique d'activité,
   vide par construction. Si le test attend `Qualified` avec le seuil historique, il
   faut journaliser des activités d'abord (§3.2).
2. **Le seuil n'est pas 60 en dev, il est 35.** Lire `appsettings.Development.json` avant
   d'interpréter un statut.
3. **`nextAction` et le statut peuvent se contredire** (L-24) : paliers d'intention codés
   en dur à 60/40, statut suivant le seuil configuré.
4. **L'auto-dispatch fausse le test manuel** : un lead peut être déjà qualifié et assigné
   par le consommateur. Le désactiver pour tester le chemin explicite.
5. **Le dispatch n'exige pas `Qualified`** — un test qui vérifie le contraire teste une
   règle qui n'existe plus ; le code d'erreur est `LEAD_NOT_DISPATCHABLE` et il ne sort que
   pour les statuts terminaux.
6. **`score` et `templateId` ensemble** : le `score` est silencieusement ignoré.

---

## 6. Fichiers de référence

| Sujet | Fichier |
|-------|---------|
| Agrégat, `Qualify`, `IsDispatchable`, `Convert` | `src/Modules/Leads/Sankore.Modules.Leads/Domain/Lead.cs` |
| Barème automatique | `Features/QualifyLead/LeadScoreCalculator.cs` |
| Trois chemins, intention, action suivante | `Features/QualifyLead/QualifyLeadHandler.cs` |
| Barème par modèle | même fichier, `ScoreFromTemplate` / `ComputeEarned` |
| Dispatch et ses refus | `Features/DispatchLead/DispatchLeadHandler.cs` |
| Choix de la règle | `Features/DispatchLead/DispatchingRuleResolver.cs` |
| Auto-dispatch | `Features/Consumers/LeadAutoDispatchConsumer.cs` |
| Conversion et événements | `Features/ConvertLead/ConvertLeadHandler.cs` |
| Modèles de qualification | `Features/QualificationTemplates/` |
| Seuil | `Leads:QualificationThreshold`, lié par `LeadModuleSettings.cs` (racine du module) |
| Parcours complet du module | `workflow-complet-leads.md` |
