// Non-régression : un seul agent, quelques parcours. À relancer après chaque lot.
//   k6 run -e TENANT_FQDN=http://localhost:4222 scenarios/smoke.js

import { loginAll, sessionOf } from '../lib/auth.js';
import { agentDay } from '../lib/agent-day.js';

export const options = {
  vus: 1,
  iterations: 3,
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    // Un agent seul : tout écran doit répondre vite.
    'http_req_duration{name:leads (liste)}': ['p(95)<800'],
    'http_req_duration{name:clients (liste)}': ['p(95)<800'],
    http_req_duration: ['p(95)<1000'],
  },
};

export function setup() {
  return loginAll(1);
}

export default function (sessions) {
  agentDay(sessionOf(sessions));
}
