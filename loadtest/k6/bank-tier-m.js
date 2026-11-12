// Tier-M load test of the Bank API (plan 06 PERF-09, WP-4.7; doc 09 §5 tier M: 1,000 commands/s at peak).
//
//   docker run --rm -i -e BASE_URL=http://host.docker.internal:8080 grafana/k6:2.3.0 run - < loadtest/k6/bank-tier-m.js
//
// Environment: BASE_URL (default http://localhost:8080), RATE (commands/s, default 1000), DURATION (default 5m),
// RAMP (default 30s). The service must run with Demo__QueryApi=true for the read-model freshness probe.
//
// Thresholds are the SLOs of doc 08 §6, measured at the HTTP edge:
//   write availability  failed commands < 0.1 %          (99.9 %)
//   write latency       p99 command request < 50 ms
//   read-model freshness p99 command → summary visible < 5 s
//   delivery and health /health/sourceflow stays Healthy (outbox age, subscriptions, store, transport)
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';
import exec from 'k6/execution';

const BASE = __ENV.BASE_URL || 'http://localhost:8080';
const RATE = parseInt(__ENV.RATE || '1000', 10);
const DURATION = __ENV.DURATION || '5m';
const RAMP = __ENV.RAMP || '30s';
const JSON_HEADERS = { 'Content-Type': 'application/json' };

const commandFailed = new Rate('command_failed');
const commandDuration = new Trend('command_duration', true);
const freshness = new Trend('read_model_freshness', true);
const healthy = new Rate('sourceflow_healthy');
const commands = new Counter('commands');

export const options = {
  discardResponseBodies: false,
  scenarios: {
    commands: {
      executor: 'ramping-arrival-rate',
      exec: 'command',
      startRate: Math.max(1, Math.floor(RATE / 10)),
      timeUnit: '1s',
      preAllocatedVUs: Math.max(50, Math.floor(RATE / 4)),
      maxVUs: Math.max(200, RATE),
      stages: [
        { target: RATE, duration: RAMP },
        { target: RATE, duration: DURATION },
      ],
    },
    freshness: {
      executor: 'constant-arrival-rate',
      exec: 'freshnessProbe',
      rate: 1,
      timeUnit: '2s',
      duration: DURATION,
      startTime: RAMP,
      preAllocatedVUs: 5,
      maxVUs: 20,
    },
    health: {
      executor: 'constant-arrival-rate',
      exec: 'healthProbe',
      rate: 1,
      timeUnit: '1s',
      duration: DURATION,
      startTime: RAMP,
      preAllocatedVUs: 2,
      maxVUs: 5,
    },
  },
  thresholds: {
    command_failed: ['rate<0.001'],
    command_duration: ['p(99)<50'],
    read_model_freshness: ['p(99)<5000'],
    sourceflow_healthy: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

// Each VU keeps the accounts it opened: deposits and withdrawals go to distinct streams (no hot stream contention).
const owned = [];

function uuid() {
  // A v4-shaped id from Math.random: unique enough for a load test, and k6 has no crypto.randomUUID.
  const hex = [];
  for (let i = 0; i < 32; i++) hex.push(Math.floor(Math.random() * 16).toString(16));
  hex[12] = '4';
  hex[16] = ((parseInt(hex[16], 16) & 0x3) | 0x8).toString(16);
  const s = hex.join('');
  return `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20)}`;
}

function send(path, body) {
  const response = http.post(`${BASE}${path}`, JSON.stringify(body), { headers: JSON_HEADERS, tags: { name: path.split('/').slice(0, 2).join('/') } });
  const ok = response.status === 200 || response.status === 202;
  commandFailed.add(!ok);
  commandDuration.add(response.timings.duration);
  commands.add(1);
  return ok;
}

// The command mix: 30 % opens, 55 % deposits, 15 % withdrawals.
export function command() {
  const roll = Math.random();
  if (owned.length < 5 || roll < 0.3) {
    const id = uuid();
    if (send(`/accounts/${id}`, { holder: `load-${exec.vu.idInTest}`, initialDeposit: 1000 })) {
      owned.push(id);
      if (owned.length > 200) owned.shift();
    }
    return;
  }

  const id = owned[Math.floor(Math.random() * owned.length)];
  if (roll < 0.85) {
    send(`/accounts/${id}/deposits`, { amount: 1 });
  } else {
    send(`/accounts/${id}/withdrawals`, { amount: 1 });
  }
}

// Opens an account and polls the account summary through the query API until it appears.
export function freshnessProbe() {
  const id = uuid();
  const started = Date.now();
  const opened = http.post(`${BASE}/accounts/${id}`, JSON.stringify({ holder: 'probe', initialDeposit: 1 }), { headers: JSON_HEADERS, tags: { name: 'probe-open' } });
  if (opened.status !== 200 && opened.status !== 202) {
    return;
  }

  const query = JSON.stringify({ query: 'from readmodel "account-summary" | where id = @id | project id', parameters: { id: id.replace(/-/g, '') } });
  for (let attempt = 0; attempt < 100; attempt++) {
    const response = http.post(`${BASE}/query`, query, { headers: JSON_HEADERS, tags: { name: 'probe-query' } });
    if (response.status === 200 && response.json('rows') && response.json('rows').length > 0) {
      freshness.add(Date.now() - started);
      return;
    }

    sleep(0.1);   // the query API rate-limits each caller (429): poll gently
  }

  freshness.add(60000);   // not visible within about 10 s: counts against the SLO
}

export function healthProbe() {
  const response = http.get(`${BASE}/health/sourceflow`, { tags: { name: 'health' } });
  const ok = check(response, { 'sourceflow healthy': (r) => r.status === 200 && r.body === 'Healthy' });
  healthy.add(ok);
}
