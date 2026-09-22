# Workflow complet — Module Leads (M13)

Ce document décrit le workflow end-to-end de gestion des leads dans SankoreCRM,
de la configuration initiale jusqu'à la conversion, en listant chaque endpoint
avec sa méthode HTTP, son payload et sa permission.

---

## Phase 0 — Configuration préalable (admin)

### 0.1 Créer les sources de leads
```
POST /api/v1/leads/lead-sources
Permission: lead:source:manage
Body: { "code": "WEB", "label": "Site Web", "displayOrder": 1 }
```

### 0.2 Configurer les étapes du pipeline
```
POST /api/v1/leads/pipeline-stages
Permission: lead:pipeline-stage:manage
Body: { "code": "NEW", "label": "Nouveau", "displayOrder": 1, "color": "#3498db" }
```

### 0.3 Créer une règle de dispatching
```
POST /api/v1/leads/dispatching-rules
Permission: lead:dispatching-rule:manage
Body: {
  "name": "Règle standard",
  "strategy": "CompatibilityScoring",
  "weights": { "language": 0.3, "product": 0.25, "geography": 0.2, "workload": 0.15, "performance": 0.1, "agency": 0 },
  "maxLeadsPerAgent": 50,
  "maxTasksPerAgent": 20,
  "antiMonopolyThreshold": 10,
  "firstContactSla": "02:00:00"
}
```

### 0.4 Activer la règle
```
POST /api/v1/leads/dispatching-rules/{ruleId}/activate
Permission: lead:dispatching-rule:manage
```

### 0.5 Configurer les SLA
```
POST /api/v1/leads/sla-configs
Permission: lead:sla-config:manage
Body: {
  "name": "SLA par défaut",
  "agencyId": null,
  "firstContactDeadline": "02:00:00",
  "qualificationDeadline": "72:00:00",
  "followUpDeadline": "24:00:00",
  "escalationDeadline": "04:00:00"
}
```
```
POST /api/v1/leads/sla-configs/{id}/activate
```

### 0.6 Configurer le scoring
```
POST /api/v1/leads/scoring-configs
Permission: lead:scoring-config:manage
Body: {
  "name": "Scoring v1",
  "qualificationThreshold": 60,
  "weightDemographics": 0.2,
  "weightEngagement": 0.3,
  "weightProduct": 0.2,
  "weightChannel": 0.15,
  "weightRecency": 0.15
}
```
```
POST /api/v1/leads/scoring-configs/{id}/activate
```

### 0.7 Configurer les types de tâches
```
POST /api/v1/leads/task-types
Permission: lead:task-type:manage
Body: { "code": "FIRST_CONTACT", "label": "Premier contact", "displayOrder": 1 }
```

### 0.8 Créer une règle de génération de tâches
```
POST /api/v1/leads/task-generation-rules
Permission: lead:task-rule:manage
Body: {
  "triggerEventType": "LeadDispatched",
  "taskType": "FirstContact",
  "priority": "High",
  "titleTemplate": "Premier contact — Lead {LeadId}",
  "slaDuration": "02:00:00",
  "dueDuration": "24:00:00"
}
```

### 0.9 Créer un template de qualification
```
POST /api/v1/leads/qualification-templates
Permission: lead:qualification-template:manage
```

### 0.10 Configurer une séquence de nurturing
```
POST /api/v1/leads/nurturing-sequences
Permission: lead:nurturing-sequence:manage
Body: {
  "name": "Séquence bienvenue",
  "steps": [
    { "order": 1, "delayFromPrevious": "00:00:00", "emailTemplateKey": "nurture.welcome" },
    { "order": 2, "delayFromPrevious": "3.00:00:00", "emailTemplateKey": "nurture.followup-1" },
    { "order": 3, "delayFromPrevious": "7.00:00:00", "emailTemplateKey": "nurture.followup-2" }
  ]
}
```

---

## Phase 1 — Capture d'un lead

### 1.1 Capturer un lead (multi-canal)
```
POST /api/v1/leads
Permission: lead:create
Body: {
  "firstName": "Amadou",
  "lastName": "Traoré",
  "phoneNumber": "+22370123456",
  "source": "Web",
  "interestedProduct": "Crédit Agricole",
  "preferredLanguage": "fr",
  "latitude": 12.63,
  "longitude": -8.0,
  "email": "amadou@example.com",
  "gender": "Male",
  "channel": "Web"
}
```
**Résultat :**
- Lead créé avec statut `New`, pipeline stage `New`
- Blind index HMAC-SHA256 calculé sur le téléphone (dédup)
- Détection de doublons multi-signal (phone, email, nationalId)
- Événement `LeadCaptured` publié via outbox
- Événement `WorkflowTriggerSignal` publié (LEAD_CAPTURED)

### 1.2 Capture système (import CSV/Excel)
```
POST /api/v1/leads/import
Permission: lead:import
Body: multipart/form-data (fichier CSV)
```
**Job Hangfire** : chaque ligne passe par le même pipeline que 1.1.

### 1.3 Enregistrer le consentement du prospect
```
POST /api/v1/leads/{leadId}/consents
Permission: lead:consent:record
Body: { "type": "DataProcessing", "channel": "InPerson", "proofReference": "form-signed-2026-09-22" }
```

---

## Phase 2 — Qualification & Scoring

### 2.1 Qualifier le lead
```
POST /api/v1/leads/{leadId}/qualify
Permission: lead:qualify
Body: { "score": 75 }
```
**Résultat :**
- Score >= 60 → statut `Qualified` (éligible au dispatching)
- Score < 60 → statut `Qualifying` (travail supplémentaire nécessaire)
- Événement `LeadQualified` publié

### 2.2 Recalculer le score automatiquement
```
POST /api/v1/leads/{leadId}/recalculate-score
```
Déclenché aussi automatiquement après chaque activité si `EnableAutoScoreRecalculation = true`.

### 2.3 Définir le niveau d'intention
```
PUT /api/v1/leads/{leadId}/intent-level
Body: { "level": "Hot" }
```

---

## Phase 3 — Dispatching & Assignation

### 3.1 Dispatcher le lead (auto-assignation scorée)
```
POST /api/v1/leads/{leadId}/dispatch
Permission: lead:assign
Body: { "tenantId": "...", "strategy": "CompatibilityScoring" }
```
**Résultat :**
- Scoring des agents éligibles (langue, produit, géographie, charge, performance, agence)
- Anti-monopole appliqué
- Capacité des agents vérifiée
- `LeadAssignment` créé avec facteurs de compatibilité (audit)
- Événement `LeadDispatched` publié → **déclenche la règle de génération de tâches** (0.8)
- Tâche CRM créée automatiquement (ex: "Premier contact — Lead {id}")
- **Email de notification envoyé à l'agent** via M08

### 3.2 Tâche auto-générée → notification agent
Le consumer `LeadDispatchedTaskConsumer` crée la tâche, puis `TaskAssignedNotificationConsumer` envoie l'email.

### 3.3 Consulter mes tâches (agent)
```
GET /api/v1/leads/tasks?status=Pending
Permission: lead:task:read
```
**Scoping automatique** : l'agent voit uniquement ses tâches, le manager voit son équipe.

### 3.4 Démarrer la tâche
```
POST /api/v1/leads/tasks/{taskId}/start
Permission: lead:task:manage
```
Vérification : seul l'agent assigné ou un superviseur peut démarrer.

### 3.5 Enregistrer le premier contact
```
POST /api/v1/leads/{leadId}/first-contact
```

### 3.6 Terminer la tâche
```
POST /api/v1/leads/tasks/{taskId}/complete
Permission: lead:task:manage
```
**Résultat :**
- Événement `TaskCompleted` publié
- Consumer `TaskCompletedTaskConsumer` évalue les règles de génération → crée la tâche suivante si configurée
- Cache de capacité invalidé

### 3.7 Refuser une tâche (agent)
```
POST /api/v1/leads/tasks/{taskId}/decline
Permission: lead:task:manage
Body: { "agentId": "...", "reason": "Pas disponible" }
```
Agent temporairement exclu → tâche retourne dans le pool de dispatching.

### 3.8 Réassigner une tâche (manager)
```
PUT /api/v1/leads/tasks/{taskId}/reassign
Permission: lead:task:manage
Body: { "newAgentId": "...", "reason": "Changement de secteur", "slaExtensionHours": 2 }
```
Historique de réassignation + notification email au nouvel agent.

---

## Phase 4 — Interactions & Activités

### 4.1 Enregistrer une activité
```
POST /api/v1/leads/{leadId}/activities
Permission: lead:activity:log
Body: {
  "type": "Call",
  "subject": "Appel de suivi",
  "durationMinutes": 15,
  "outcome": "Interested",
  "ctiCallReference": "SID-12345"
}
```
Supporte : Call, Meeting, Email, Visit, Note, Sms, WhatsApp, Task.
Pièces jointes via `attachmentsJson` (références stockage objet externe).

### 4.2 Enregistrer une visite terrain
```
POST /api/v1/leads/{leadId}/activities
Body: {
  "type": "Visit",
  "subject": "Visite exploitation agricole",
  "visitLatitude": 12.63,
  "visitLongitude": -8.0,
  "visitPhotoReference": "photos/visit-abc123.jpg"
}
```
**Prérequis** : consentement `LocationTracking` actif (vérifié automatiquement).
Rétention automatique : 365 jours pour les données sensibles (GPS + photo).

### 4.3 Consulter les activités
```
GET /api/v1/leads/{leadId}/activities?page=1&pageSize=20
```

### 4.4 Ajouter/retirer des tags
```
POST /api/v1/leads/{leadId}/tags
DELETE /api/v1/leads/{leadId}/tags/{tag}
Permission: lead:tag
```

### 4.5 Créer un rappel
```
POST /api/v1/leads/{leadId}/reminders
Permission: lead:reminder:manage
```

---

## Phase 5 — Pipeline & Suivi

### 5.1 Voir le pipeline (Kanban)
```
GET /api/v1/leads/pipeline?agencyId=...&maxPerStage=50
Permission: lead:read
```
Retourne les leads groupés par stage avec cards (nom, score, intent, owner).

### 5.2 Déplacer dans le pipeline
```
PUT /api/v1/leads/{leadId}/pipeline-stage
Permission: lead:pipeline
Body: { "newStage": "ProductProposed" }
```
Validation serveur + événement `LeadPipelineStageChanged` via outbox.

### 5.3 Consulter la timeline
```
GET /api/v1/leads/{leadId}/timeline
```
Agrège : activités, changements de score, assignations, rappels, fusions, consentements.

### 5.4 Consulter les statistiques
```
GET /api/v1/leads/stats?from=...&to=...&agencyId=...
```

---

## Phase 6 — Nurturing & Recyclage

### 6.1 Placer en nurturing
```
POST /api/v1/leads/{leadId}/nurture
Permission: lead:nurture
Body: { "sequenceId": "..." }
```
**Prérequis** : consentement `Marketing` actif (vérifié automatiquement).
Enrollment dans la séquence → job Hangfire envoie les emails séquentiels via M08.

### 6.2 Retrait de consentement → arrêt immédiat
```
POST /api/v1/leads/{leadId}/consents/{consentId}/withdraw
Permission: lead:consent:withdraw
Body: { "withdrawnBy": "...", "reason": "Demande prospect" }
```
Si consentement `Marketing` → événement `MarketingConsentWithdrawnEvent` → consumer annule immédiatement tous les enrollments actifs.

### 6.3 Recycler un lead fermé
```
POST /api/v1/leads/{leadId}/recycle
Permission: lead:recycle
Body: { "newSource": "Campaign", "newCampaign": "Relance Q4" }
```
Score remis à 0, statut → `Recycled`.

### 6.4 Réactivation automatique (job planifié)
**Job Hangfire quotidien** (`ReactivateRecycledLeadsJob`) :
- Leads recyclés depuis > 30 jours (configurable)
- Re-vérification du consentement si dormant > 90 jours
- Réactivation → statut `Open`
- Re-déclenchement automatique du scoring

---

## Phase 7 — Conversion

### 7.1 Convertir en nouveau client
```
POST /api/v1/leads/{leadId}/convert
Permission: lead:convert
Body: { "force": false }
```
**Résultat :**
- Détection de doublons par blind index avant conversion
- Lead → statut `Converted`, pipeline → `Converted`
- Événement `LeadConverted` publié via outbox
- Événement `KycRequested` publié via outbox (consommé par futur module KYC)

### 7.2 Convertir vers un client existant
```
POST /api/v1/leads/{leadId}/convert
Body: { "customerId": "...", "force": true }
```
Validation de l'existence du client via `ICustomerModule.ExistsAsync()`.

### 7.3 Créer une opportunité
```
POST /api/v1/leads/opportunities
Permission: lead:opportunity:manage
Body: {
  "leadId": "...",
  "title": "Crédit agricole 2026",
  "product": "CRED-AGRI-01",
  "estimatedAmount": 500000,
  "estimatedCurrency": "XOF"
}
```

### 7.4 Transférer le propriétaire commercial
```
PUT /api/v1/leads/{leadId}/owner
Permission: lead:assign
Body: { "ownerId": "...", "assignmentMethod": "Manual", "reason": "Changement de zone" }
```
Vérification PermissionAttribution : superviseurs transfèrent uniquement dans leur équipe.

---

## Phase 8 — Monitoring & Dashboards

### 8.1 Dashboard acquisition
```
GET /api/v1/leads/stats?from=...&to=...&agencyId=...
```
Breakdown par statut, source, stage pipeline, taux de conversion.

### 8.2 Dashboard conversion (funnel)
```
GET /api/v1/leads/funnel-metrics?from=...&to=...&agencyId=...
Permission: lead:analytics:view
```

### 8.3 Dashboard performance agent
```
GET /api/v1/leads/agent-performance?from=...&to=...&agencyId=...
Permission: lead:analytics:view
```
Scoping automatique : agent voit ses stats, manager voit son équipe.

### 8.4 Breaches SLA
```
GET /api/v1/leads/sla-breaches?agentId=...&agencyId=...
Permission: lead:analytics:view
```

### 8.5 Export CSV
```
GET /api/v1/leads/export?format=csv
Permission: lead:export
```

---

## Jobs Hangfire (automatiques, identité SYSTEM)

| Job | Fréquence | Rôle |
|-----|-----------|------|
| `CheckSlaBreachesJob` | Toutes les 15 min | Détection SLA breachés → alerte M08 + escalade auto (max 3 niveaux) |
| `ExecuteNurturingJob` | Toutes les 10 min | Envoi séquentiel des emails de nurturing via M08 |
| `ReactivateRecycledLeadsJob` | Quotidien 03h00 | Réactivation des leads recyclés + re-scoring |
| `ProcessLeadImportJob` | À la demande | Import CSV/Excel de leads |

---

## Événements publiés via Outbox

| Événement | Publié par | Consommé par |
|-----------|------------|--------------|
| `LeadCaptured` | CaptureLeadHandler | Workflow module |
| `LeadDispatched` | DispatchLeadHandler | TaskConsumer (crée tâche), Notifications |
| `LeadDispatchingFailed` | DispatchLeadHandler | TaskConsumer (crée tâche manager) |
| `TaskAssigned` | Assign/Dispatch/ReassignTaskHandler | NotificationConsumer (email agent) |
| `TaskCompleted` | CompleteTaskHandler | TaskCompletedConsumer (tâche suivante) |
| `LeadConverted` | ConvertLeadHandler | Module Customer (futur) |
| `KycRequested` | ConvertLeadHandler | Module KYC (futur) |
| `LeadOwnerChanged` | UpdateLeadOwnerHandler | Analytics |
| `LeadPipelineStageChanged` | Lead.AdvancePipelineStage | Analytics |
| `SlaEscalated` | CheckSlaBreachesJob | Audit trail |
| `MarketingConsentWithdrawn` | WithdrawConsentHandler | NurturingConsumer (arrêt immédiat) |

---

## Permissions M13 (toutes dans `Permissions.All`)

| Code | Description |
|------|-------------|
| `lead:create` | Capturer un lead |
| `lead:read` | Consulter un lead |
| `lead:update` | Modifier un lead |
| `lead:assign` | Assigner/réassigner un owner |
| `lead:qualify` | Qualifier un lead |
| `lead:close` | Fermer un lead |
| `lead:pipeline` | Déplacer dans le pipeline |
| `lead:convert` | Convertir en client |
| `lead:nurture` | Placer en nurturing |
| `lead:recycle` | Recycler un lead |
| `lead:activity:log` | Enregistrer une activité |
| `lead:tag` | Gérer les tags |
| `lead:import` | Importer des leads |
| `lead:export` | Exporter en CSV |
| `lead:merge` | Fusionner des doublons |
| `lead:duplicate:dismiss` | Rejeter un doublon |
| `lead:reminder:manage` | Gérer les rappels |
| `lead:consent:record` | Enregistrer un consentement |
| `lead:consent:withdraw` | Retirer un consentement |
| `lead:task:read` | Consulter les tâches |
| `lead:task:manage` | Gérer les tâches |
| `lead:analytics:view` | Voir les dashboards |
| `lead:dispatching-rule:manage` | Gérer les règles de dispatching |
| `lead:dispatching-rule:read` | Lire les règles de dispatching |
| `lead:task-rule:manage` | Gérer les règles de génération de tâches |
| `lead:source:manage` | Gérer les sources |
| `lead:source:read` | Lire les sources |
| `lead:scoring-config:manage` | Gérer le scoring |
| `lead:scoring-config:read` | Lire le scoring |
| `lead:task-type:manage` | Gérer les types de tâches |
| `lead:task-type:read` | Lire les types de tâches |
| `lead:pipeline-stage:manage` | Gérer les stages pipeline |
| `lead:pipeline-stage:read` | Lire les stages pipeline |
| `lead:sla-config:manage` | Gérer les SLA |
| `lead:sla-config:read` | Lire les SLA |
| `lead:opportunity:manage` | Gérer les opportunités |
| `lead:opportunity:read` | Lire les opportunités |
| `lead:nurturing-sequence:manage` | Gérer les séquences nurturing |
| `lead:nurturing-sequence:read` | Lire les séquences nurturing |
