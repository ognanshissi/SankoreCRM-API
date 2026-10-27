// Montée en charge : combien d'agents simultanés l'application tient.
//   k6 run -e TENANT_FQDN=… -e MAX_VUS=50 scenarios/load.js
//
// Un utilisateur virtuel = un compte du CSV. Avec moins de comptes que MAX_VUS, plusieurs VU
// partagent un compte et la limite de 200 requêtes/minute/compte arrive plus vite : le test
// s'arrête alors de lui-même (seuil sur les 429).

import { loginAll, sessionOf, users } from '../lib/auth.js';
import { agentDay } from '../lib/agent-day.js';

const MAX_VUS = Number(__ENV.MAX_VUS || 50);

export const options = {
  scenarios: {
    agents: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '2m', target: Math.ceil(MAX_VUS / 4) },
        { duration: '3m', target: Math.ceil(MAX_VUS / 2) },
        { duration: '3m', target: MAX_VUS },
        { duration: '5m', target: MAX_VUS }, // le palier : c'est lui qu'on lit
        { duration: '1m', target: 0 },
      ],
      gracefulRampDown: '30s',
    },
  },
  // La connexion de tous les comptes peut prendre plus d'une minute (60/min/IP).
  setupTimeout: '5m',
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    http_req_duration: ['p(95)<1500', 'p(99)<3000'],
    'http_req_duration{name:leads (liste)}': ['p(95)<1500'],
    'http_req_duration{name:leads (recherche)}': ['p(95)<1500'],
    'http_req_duration{name:clients (liste)}': ['p(95)<1500'],
    // Un 429 veut dire « limite de débit atteinte » : le test ne mesure plus l'application.
    'http_reqs{status:429}': [{ threshold: 'count<1', abortOnFail: true, delayAbortEval: '10s' }],
  },
};

export function setup() {
  if (users.length < MAX_VUS) {
    console.warn(`${users.length} comptes pour ${MAX_VUS} VU : certains comptes seront partagés.`);
  }
  return loginAll(Math.min(users.length, MAX_VUS));
}

export default function (sessions) {
  agentDay(sessionOf(sessions));
}
