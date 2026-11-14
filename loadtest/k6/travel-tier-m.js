// Tier-M load test of the TravelBooking sample (plan 06 PERF-09, WP-4.7; doc 09 §5 tier M: 1,000 commands/s at peak),
// against the Bookings service, with the trip-booking workflow running to the Hotels service over the broker.
//
//   docker compose up -d --build --wait postgres localstack hotels bookings
//   docker run --rm -i --network sourceflow-demo_default -e BOOKINGS_URL=http://bookings:8080 -e HOTELS_URL=http://hotels:8080 \
//     grafana/k6:2.3.0 run - < loadtest/k6/travel-tier-m.js
//
// Environment: BOOKINGS_URL (default http://localhost:8080), HOTELS_URL (default http://localhost:8081), RATE (commands/s,
// default 1000), DURATION (default 5m), RAMP (default 30s). The services must run with Demo__QueryApi=true.
//
// Thresholds are the SLOs of doc 08 §6, measured at the HTTP edge:
//   write availability  failed commands < 0.1 %          (99.9 %)
//   write latency       p99 command request < 50 ms
//   workflow latency    p99 booking requested → room held at the hotel (across both services) < 5 s
//   delivery and health /health/ready stays Healthy on the Bookings service
import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend, Counter } from 'k6/metrics';

const BOOKINGS = __ENV.BOOKINGS_URL || 'http://localhost:8080';
const HOTELS = __ENV.HOTELS_URL || 'http://localhost:8081';
const RATE = parseInt(__ENV.RATE || '1000', 10);
const DURATION = __ENV.DURATION || '5m';
const RAMP = __ENV.RAMP || '30s';
const JSON_HEADERS = { 'Content-Type': 'application/json' };

const commandFailed = new Rate('command_failed');
const commandDuration = new Trend('command_duration', true);
const held = new Trend('workflow_room_held', true);
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
    workflow: {
      executor: 'constant-arrival-rate',
      exec: 'workflowProbe',
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
    workflow_room_held: ['p(99)<5000'],
    sourceflow_healthy: ['rate>0.99'],
  },
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

function uuid() {
  // A v4-shaped id from Math.random: unique enough for a load test, and k6 has no crypto.randomUUID.
  const hex = [];
  for (let i = 0; i < 32; i++) hex.push(Math.floor(Math.random() * 16).toString(16));
  hex[12] = '4';
  hex[16] = ((parseInt(hex[16], 16) & 0x3) | 0x8).toString(16);
  const s = hex.join('');
  return `${s.slice(0, 8)}-${s.slice(8, 12)}-${s.slice(12, 16)}-${s.slice(16, 20)}-${s.slice(20)}`;
}

// One hotel with more rooms than the test can book, so no booking is declined as sold out.
export function setup() {
  const hotelId = uuid();
  const response = http.post(`${HOTELS}/hotels/${hotelId}`, JSON.stringify({ name: 'Load Test Inn', rooms: 100000000 }), { headers: JSON_HEADERS });
  if (response.status !== 200) {
    throw new Error(`Registering the hotel failed: ${response.status} ${response.body}`);
  }

  return { hotelId };
}

function send(path, body, name) {
  const response = http.post(`${BOOKINGS}${path}`, JSON.stringify(body), { headers: JSON_HEADERS, tags: { name } });
  const ok = response.status === 200 || response.status === 202;
  commandFailed.add(!ok);
  commandDuration.add(response.timings.duration);
  commands.add(1);
  return ok;
}

// Each VU keeps the bookings it requested; the command mix is 60 % requests and 40 % guest confirmations.
const requested = [];

export function command(data) {
  if (requested.length < 5 || Math.random() < 0.6) {
    const id = uuid();
    if (send(`/bookings/${id}`, { hotelId: data.hotelId, guest: 'Load Test', nights: 2 }, 'request')) {
      requested.push(id);
    }

    return;
  }

  // A booking is confirmed once: take it out of the list.
  const id = requested.splice(Math.floor(Math.random() * requested.length), 1)[0];
  send(`/bookings/${id}/confirm`, {}, 'confirm');
}

// Requests a booking and polls the workflow-instances read model until the room is held (the hold went to Hotels over
// the broker and Hotels' event came back): the latency of one step of the workflow across both services.
export function workflowProbe(data) {
  const id = uuid();
  const started = Date.now();
  const requestedProbe = http.post(`${BOOKINGS}/bookings/${id}`, JSON.stringify({ hotelId: data.hotelId, guest: 'Probe', nights: 1 }), { headers: JSON_HEADERS, tags: { name: 'probe-request' } });
  if (requestedProbe.status !== 200) {
    return;
  }

  const query = JSON.stringify({ query: 'from readmodel "workflow-instances" | where instance = @id and step = "await-guest" | project instance', parameters: { id } });
  for (let attempt = 0; attempt < 100; attempt++) {
    const response = http.post(`${BOOKINGS}/query`, query, { headers: JSON_HEADERS, tags: { name: 'probe-query' } });
    if (response.status === 200 && response.json('rows') && response.json('rows').length > 0) {
      held.add(Date.now() - started);
      return;
    }

    sleep(0.1);   // the query API rate-limits each caller (429): poll gently
  }

  held.add(60000);   // not held within about 10 s: counts against the SLO
}

export function healthProbe() {
  const response = http.get(`${BOOKINGS}/health/ready`, { tags: { name: 'health' } });
  const ok = check(response, { 'bookings ready': (r) => r.status === 200 && r.body === 'Healthy' });
  healthy.add(ok);
}
