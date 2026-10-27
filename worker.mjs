// GLaDOS free check-in only. No account settings, purchases or redemptions.
const ORIGIN = 'https://glados.rocks';
const POINTS = '/api/user/points';
const CHECKIN = '/api/user/checkin';
const TTL = 14 * 24 * 60 * 60;

/** @param {number|string} value */
export function chinaDate(value = Date.now()) {
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return null;
  return new Date(date.getTime() + 8 * 3600_000).toISOString().slice(0, 10);
}

function recordDate(value) {
  // Matches the website's new Date(time) when its API returns epoch milliseconds.
  if (typeof value === 'number') return Number.isFinite(value) && value >= 946684800000 && value < 4133980800000 ? chinaDate(value) : null;
  if (typeof value !== 'string') return null;
  let text = value.trim();
  // The website interprets timestamps without a zone in the user's local zone.
  if (/^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?$/.test(text)) {
    text = text.replace(' ', 'T') + '+08:00';
  }
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(text)) return null;
  return chinaDate(text);
}

export function observation(history, date) {
  if (!Array.isArray(history)) return { valid: false };
  for (const item of history) {
    if (!item || typeof item.business !== 'string' || !item.business.toLowerCase().includes('checkin')) continue;
    const day = recordDate(item.time);
    if (!day) return { valid: false };
    if (day === date) {
      const reward = typeof item.change === 'number' || typeof item.change === 'string'
        ? Number(item.change) : NaN;
      if (!Number.isFinite(reward)) return { valid: false };
      return { valid: true, signed: true, reward };
    }
  }
  return { valid: true, signed: false };
}

class SafeFailure extends Error {
  constructor(code) { super(code); this.code = code; }
}

async function api(fetcher, cookie, path, method = 'GET') {
  if (!((path === POINTS && method === 'GET') || (path === CHECKIN && method === 'POST'))) {
    throw new SafeFailure('DISALLOWED_OPERATION');
  }
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 20_000);
  try {
    const response = await fetcher(ORIGIN + path, {
      method, redirect: 'manual', signal: controller.signal,
      headers: {
        Cookie: cookie, Accept: 'application/json',
        Origin: ORIGIN, Referer: ORIGIN + '/console/checkin',
        ...(method === 'POST' ? { 'Content-Type': 'application/json' } : {})
      },
      ...(method === 'POST' ? { body: JSON.stringify({ token: new URL(ORIGIN).hostname }) } : {})
    });
    if (response.status === 401) throw new SafeFailure('LOGIN_REQUIRED');
    if (response.status === 403 || response.status === 429) throw new SafeFailure('SECURITY_CHECK');
    if (response.status >= 300 && response.status < 400) throw new SafeFailure('REDIRECT_STOPPED');
    if (!response.ok) throw new SafeFailure('REMOTE_ERROR');
    if (!(response.headers.get('content-type') || '').includes('application/json')) {
      throw new SafeFailure('UNRECOGNIZED_RESPONSE');
    }
    const data = await response.json();
    if (!data || typeof data !== 'object' || Array.isArray(data)) throw new SafeFailure('UNRECOGNIZED_RESPONSE');
    if (data.reason === 'device-mismatch') throw new SafeFailure('DEVICE_MISMATCH');
    if (data.code !== 0) throw new SafeFailure('LOGIN_OR_SITE_REJECTION');
    return data;
  } catch (error) {
    // Never log response bodies, exceptions, cookie values or account details.
    if (error instanceof SafeFailure) throw error;
    throw new SafeFailure('CONNECTION_FAILED');
  } finally { clearTimeout(timer); }
}

export async function runCheckin(env, { fetcher = fetch, now = () => Date.now() } = {}) {
  const date = chinaDate(now());
  const result = (status, extra = {}) => ({ date, status, ...extra });
  if (!env.CHECKIN_STATE) return result('STATE_NOT_CONFIGURED');
  if (typeof env.GLADOS_COOKIE !== 'string' || !env.GLADOS_COOKIE.trim() || /[\r\n]/.test(env.GLADOS_COOKIE)) {
    return result('LOGIN_NOT_CONFIGURED');
  }
  const key = `day:${date}`;
  let previous;
  try { previous = await env.CHECKIN_STATE.get(key, 'json'); }
  catch { return result('STATE_READ_FAILED'); }
  if (previous?.status === 'VERIFIED' || previous?.status === 'ALREADY_DONE') {
    return result('ALREADY_DONE', { reward: previous.reward });
  }
  const save = async data => {
    await env.CHECKIN_STATE.put(key, JSON.stringify(data), { expirationTtl: TTL });
    return data;
  };
  let before;
  try { before = observation((await api(fetcher, env.GLADOS_COOKIE, POINTS)).history, date); }
  catch (error) { return result(error.code || 'UNVERIFIED'); }
  if (!before.valid) return result('HISTORY_FORMAT_CHANGED');
  if (before.signed) {
    const done = result('ALREADY_DONE', { reward: before.reward });
    try { await save(done); } catch { /* Remote history still proves completion. */ }
    return done;
  }
  // An uncertain write is never automatically submitted again that day.
  if (previous?.attempted) return result('UNVERIFIED_NO_REPEAT');
  // Do not post across the China date boundary after preflight.
  if (chinaDate(now()) !== date) return result('DATE_CHANGED');
  try { await save(result('ATTEMPTING', { attempted: true })); }
  catch { return result('STATE_WRITE_FAILED'); }

  let postError;
  try { await api(fetcher, env.GLADOS_COOKIE, CHECKIN, 'POST'); }
  catch (error) { postError = error.code || 'UNVERIFIED'; }
  // A security/authentication rejection stops execution; never change fingerprints.
  if (['SECURITY_CHECK', 'DEVICE_MISMATCH', 'LOGIN_REQUIRED', 'REDIRECT_STOPPED', 'LOGIN_OR_SITE_REJECTION'].includes(postError)) {
    try { await save(result(postError, { attempted: true })); } catch {}
    return result(postError);
  }

  let after;
  try { after = observation((await api(fetcher, env.GLADOS_COOKIE, POINTS)).history, date); }
  catch {
    try { await save(result('UNVERIFIED', { attempted: true })); } catch {}
    return result('UNVERIFIED');
  }
  if (after.valid && after.signed) {
    const verified = result('VERIFIED', { reward: after.reward });
    try { await save(verified); } catch { /* Pre-post marker prevents a retry. */ }
    return verified;
  }
  try { await save(result('UNVERIFIED', { attempted: true })); } catch {}
  return result('UNVERIFIED');
}

export default {
  async scheduled(event, env, ctx) {
    // On runtimes that support it, disable infrastructure retries as well.
    event.noRetry?.();
    ctx.waitUntil(runCheckin(env).then(result => {
      // Only this small allowlisted result is logged, never remote response data.
      console.log(JSON.stringify(result));
      if (!['VERIFIED', 'ALREADY_DONE'].includes(result.status)) {
        throw new Error(`GLaDOS ${result.status}; check Worker logs for the date.`);
      }
    }));
  },
  async fetch() {
    // No public check-in, credential input, status or administrative endpoint.
    return new Response('Not found', { status: 404 });
  }
};
