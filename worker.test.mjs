import { test } from 'node:test';
import assert from 'node:assert/strict';
import worker, { chinaDate, observation, runCheckin } from './worker.mjs';

const now = () => Date.parse('2026-10-26T02:00:00Z');
const record = { business: 'checkin', time: '2026-10-26T01:17:00Z', change: 10 };
const json = (body, status = 200) => new Response(JSON.stringify(body), {
  status, headers: { 'content-type': 'application/json' }
});
function fixture(responses, extra = {}) {
  const state = new Map();
  const calls = [];
  const env = {
    GLADOS_COOKIE: 'TEST_ONLY=not-a-real-session',
    CHECKIN_STATE: {
      async get(key) { return state.get(key) || null; },
      async put(key, value) { state.set(key, JSON.parse(value)); }
    }, ...extra
  };
  const fetcher = async (url, options) => {
    calls.push({ url, ...options });
    assert.ok(url === 'https://glados.rocks/api/user/points' || url === 'https://glados.rocks/api/user/checkin');
    assert.equal(options.redirect, 'manual');
    assert.equal(options.headers.Cookie, env.GLADOS_COOKIE);
    assert.equal('User-Agent' in options.headers, false);
    const next = responses.shift();
    if (next instanceof Error) throw next;
    assert.ok(next, 'Unexpected extra network request');
    return next;
  };
  return { state, env, calls, fetcher, run: () => runCheckin(env, { fetcher, now }) };
}

test('Uses Beijing date even when UTC is the previous day', () => {
  assert.equal(chinaDate('2026-10-25T16:00:00Z'), '2026-10-26');
  assert.equal(chinaDate('bad'), null);
});
test('Understands the website history fields and explicit China local timestamps', () => {
  assert.deepEqual(observation([record], '2026-10-26'), { valid: true, signed: true, reward: 10 });
  assert.equal(observation([{ ...record, time: '2026-10-26 00:01:00' }], '2026-10-26').signed, true);
  assert.equal(observation([{ ...record, business: 'exchange' }], '2026-10-26').signed, false);
  assert.equal(observation([{ ...record, time: 'yesterday' }], '2026-10-26').valid, false);
});
test('Accepts epoch milliseconds without mistaking seconds or invalid epochs for valid history', () => {
  const date = '2026-10-26';
  const ms = Date.parse('2026-10-25T16:17:00Z');
  assert.deepEqual(observation([{ ...record, time: ms }], date), { valid: true, signed: true, reward: 10 });
  for (const value of [NaN, Infinity, ms / 1000, 0, -1]) {
    assert.equal(observation([{ ...record, time: value }], date).valid, false);
  }
});
test('Matches the live API shape: array history, millisecond time and numeric string reward', () => {
  assert.deepEqual(observation([{ ...record, time: Date.parse(record.time), change: '9' }], '2026-10-26'),
    { valid: true, signed: true, reward: 9 });
});
test('Verifies the live API record shape after a single check-in submission', async () => {
  const liveShape = { ...record, time: Date.parse(record.time), change: '9' };
  const f = fixture([json({code:0,history:[]}),json({code:0}),json({code:0,history:[liveShape]})]);
  assert.deepEqual(await f.run(),{date:'2026-10-26',status:'VERIFIED',reward:9});
  assert.equal(f.calls.filter(call=>call.method==='POST').length,1);
});
test('Already signed: only one GET, no POST', async () => {
  const f = fixture([json({ code: 0, history: [record] })]);
  assert.equal((await f.run()).status, 'ALREADY_DONE');
  assert.equal(f.calls.length, 1);
  assert.equal(f.calls[0].method, 'GET');
});
test('Signs once and independently verifies current-date history', async () => {
  const f = fixture([json({ code: 0, history: [] }), json({ code: 0 }), json({ code: 0, history: [record] })]);
  assert.deepEqual(await f.run(), { date: '2026-10-26', status: 'VERIFIED', reward: 10 });
  assert.equal(f.calls.filter(c => c.method === 'POST').length, 1);
  assert.deepEqual(JSON.parse(f.calls[1].body), { token: 'glados.rocks' });
  assert.equal((await f.run()).status, 'ALREADY_DONE');
  assert.equal(f.calls.length, 3);
});
test('A nominal success response without history evidence is not success', async () => {
  const f = fixture([json({ code: 0, history: [] }), json({ code: 0, message: 'success' }), json({ code: 0, history: [] })]);
  assert.equal((await f.run()).status, 'UNVERIFIED');
});
test('POST timeout: rechecks, recognizes successful record, never reposts', async () => {
  const f = fixture([json({ code: 0, history: [] }), new Error('sensitive-cookie-must-not-leak'), json({ code: 0, history: [record] })]);
  assert.equal((await f.run()).status, 'VERIFIED');
  assert.equal(f.calls.filter(c => c.method === 'POST').length, 1);
});
test('Uncertain attempt stays read-only on a subsequent run', async () => {
  const f = fixture([
    json({ code: 0, history: [] }), new Error('timeout'), json({ code: 0, history: [] }),
    json({ code: 0, history: [] })
  ]);
  assert.equal((await f.run()).status, 'UNVERIFIED');
  assert.equal((await f.run()).status, 'UNVERIFIED_NO_REPEAT');
  assert.equal(f.calls.filter(c => c.method === 'POST').length, 1);
});
test('A later history read can verify an earlier uncertain write', async () => {
  const f = fixture([
    json({ code: 0, history: [] }), new Error('timeout'), json({ code: 0, history: [] }),
    json({ code: 0, history: [record] })
  ]);
  await f.run();
  assert.equal((await f.run()).status, 'ALREADY_DONE');
  assert.equal(f.calls.filter(c => c.method === 'POST').length, 1);
});
test('Expired login, challenge, redirect and unknown format never trigger POST', async () => {
  for (const [response, status] of [
    [json({}, 401), 'LOGIN_REQUIRED'], [json({}, 403), 'SECURITY_CHECK'],
    [json({}, 429), 'SECURITY_CHECK'], [json({}, 302), 'REDIRECT_STOPPED'],
    [new Response('<html>challenge</html>'), 'UNRECOGNIZED_RESPONSE'],
    [json({ code: -1 }), 'LOGIN_OR_SITE_REJECTION'],
    [json({ code: 0, history: {} }), 'HISTORY_FORMAT_CHANGED']
  ]) {
    const f = fixture([response]);
    assert.equal((await f.run()).status, status);
    assert.equal(f.calls.length, 1);
  }
});
test('Device mismatch stops with no fingerprint spoofing or extra reads', async () => {
  const f = fixture([json({ code: 0, history: [] }), json({ code: 4, reason: 'device-mismatch', loginDevice: 'private' })]);
  const result = await f.run();
  assert.equal(result.status, 'DEVICE_MISMATCH');
  assert.equal(f.calls.length, 2);
  assert.equal(JSON.stringify(result).includes('private'), false);
});
test('A challenge during POST stops without retries', async () => {
  const f = fixture([json({ code: 0, history: [] }), json({}, 403)]);
  assert.equal((await f.run()).status, 'SECURITY_CHECK');
  assert.equal(f.calls.length, 2);
});
test('Missing credential or state never makes a network request', async () => {
  for (const extra of [{ GLADOS_COOKIE: '' }, { GLADOS_COOKIE: 'bad\r\ncookie' }, { CHECKIN_STATE: null }]) {
    const f = fixture([], extra);
    assert.ok(['LOGIN_NOT_CONFIGURED', 'STATE_NOT_CONFIGURED'].includes((await f.run()).status));
    assert.equal(f.calls.length, 0);
  }
});
test('State write failure prevents submission', async () => {
  const f = fixture([json({ code: 0, history: [] })]);
  f.env.CHECKIN_STATE.put = async () => { throw new Error('disk'); };
  assert.equal((await f.run()).status, 'STATE_WRITE_FAILED');
  assert.equal(f.calls.length, 1);
});
test('State read failure prevents all remote requests', async () => {
  const f = fixture([]);
  f.env.CHECKIN_STATE.get = async () => { throw new Error('storage'); };
  assert.equal((await f.run()).status, 'STATE_READ_FAILED');
  assert.equal(f.calls.length, 0);
});
test('Previous day record cannot prove current day completion', async () => {
  const f = fixture([
    json({ code: 0, history: [] }), json({ code: 0 }),
    json({ code: 0, history: [{ ...record, time: '2026-10-25T00:00:00Z' }] })
  ]);
  assert.equal((await f.run()).status, 'UNVERIFIED');
});
test('Does not POST when preflight crosses Beijing midnight', async () => {
  const f = fixture([json({ code: 0, history: [] })]);
  let calls = 0;
  const result = await runCheckin(f.env, { fetcher: f.fetcher,
    now: () => Date.parse(calls++ === 0 ? '2026-10-26T15:59:59Z' : '2026-10-26T16:00:01Z') });
  assert.equal(result.status, 'DATE_CHANGED');
  assert.equal(f.calls.length, 1);
});
test('No public endpoint can expose status, accept credentials or trigger sign-in', async () => {
  const response = await worker.fetch(new Request('https://example.invalid/checkin?cookie=secret', { method: 'POST' }));
  assert.equal(response.status, 404);
  assert.equal(await response.text(), 'Not found');
});
test('Scheduled failures disable platform retries and log only safe status', async () => {
  let noRetry = false;
  let pending;
  let logged;
  const oldLog = console.log;
  try {
    console.log = text => { logged = text; };
    await worker.scheduled({ noRetry() { noRetry = true; } }, {}, { waitUntil(p) { pending = p; } });
    await assert.rejects(pending, /STATE_NOT_CONFIGURED/);
  } finally { console.log = oldLog; }
  assert.equal(noRetry, true);
  assert.equal(JSON.parse(logged).status, 'STATE_NOT_CONFIGURED');
});
