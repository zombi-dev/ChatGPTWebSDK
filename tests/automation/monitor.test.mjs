import { test } from 'node:test';
import assert from 'node:assert/strict';
import { validateSmoke, fingerprintHtml, probePublic, summarize } from '../../tools/monitor/check.mjs';
import { createRequire } from 'node:module';
const { publishMonitorIssue } = createRequire(import.meta.url)('../../tools/ci/monitor-issue.cjs');

const hash = 'a'.repeat(64), other = 'b'.repeat(64);
const baseline = () => ({ schema: 1, ui: hash, apiShape: hash, catalog: ['gpt-6'] });
const smoke = () => ({ schema: 1, status: 'passed', code: 'ok', apiShape: hash, catalog: ['gpt-6'], models: [{ model: 'gpt-6', status: 'passed', code: 'ok', streaming: true, continuation: true }] });
const ui = () => ({ status: 'passed', code: 'ok', fingerprint: hash });

test('Unchanged successful UI and example checks are healthy', () => assert.equal(summarize(ui(), smoke(), baseline()).severity, 'HEALTHY'));
for (const [name, mutate] of Object.entries({
  'UI assets': (u, s) => { u.fingerprint = other; },
  'API schema': (u, s) => { s.apiShape = other; },
  'model catalog addition': (u, s) => { s.catalog.push('gpt-next'); },
  'model catalog removal': (u, s) => { s.catalog = []; },
  'partial SDK failure': (u, s) => { s.status = 'degraded'; },
  'blocked SDK': (u, s) => { s.status = 'blocked'; },
  'public UI failure': u => { u.status = 'failed'; u.fingerprint = null; }
})) test(`Daily monitor reports ${name} as CHANGE`, () => {
  const u = ui(), s = smoke(); mutate(u, s); assert.equal(summarize(u, s, baseline()).severity, 'CHANGE');
});
test('Complete SDK failure escalates to CRITICAL even if the public UI works', () => {
  const s = smoke(); s.status = 'failed'; s.models[0].status = 'failed'; assert.equal(summarize(ui(), s, baseline()).severity, 'CRITICAL');
});
test('A challenge is not mistaken for a UI deployment', () => {
  const u = { status: 'blocked', code: 'public_access_challenge', fingerprint: null }; const r = summarize(u, smoke(), baseline());
  assert(!r.changes.includes('ui_assets_changed')); assert.equal(r.severity, 'HEALTHY');
});
test('Browser UI observation takes precedence over a blocked public HTTP fetch', () => {
  const s = smoke(); s.uiFingerprint = hash; assert.equal(summarize({ status: 'blocked', code: 'public_access_challenge', fingerprint: null }, s, { ...baseline(), uiSource: 'browser' }).ui.status, 'passed');
});
test('A browser baseline is never compared to the signed-out HTTP page', () => {
  const r = summarize({ status: 'passed', code: 'ok', fingerprint: other }, smoke(), { ...baseline(), uiSource: 'browser' }); assert.equal(r.ui.status, 'blocked'); assert(!r.changes.includes('ui_assets_changed'));
});
test('Missing baseline is actionable instead of silently accepting new contracts', () => assert(summarize(ui(), smoke(), { schema: 1, ui: null, apiShape: null, catalog: [] }).changes.includes('baseline_incomplete')));
test('Catalog order and duplicates do not cause changes', () => {
  const s = smoke(); s.catalog = ['gpt-6', 'gpt-6']; assert.equal(summarize(ui(), s, baseline()).severity, 'HEALTHY');
});
for (const [name, mutate] of Object.entries({
  schema: s => { s.schema = 2; }, status: s => { s.status = 'maybe'; }, code: s => { s.code = 'token\nsecret'; },
  catalog: s => { s.catalog = ['private message with spaces']; }, 'too many models': s => { s.models = Array(4).fill(s.models[0]); },
  'bad model status': s => { s.models[0].status = 'unknown'; }, 'API hash': s => { s.apiShape = 'raw secret'; },
  'browser hash': s => { s.uiFingerprint = 'private html'; }
})) test(`Monitor rejects malformed ${name} report data`, () => { const s = smoke(); mutate(s); assert.throws(() => validateSmoke(s)); });
test('Unexpected properties containing secrets are omitted from sanitized reports', () => {
  const s = smoke(); s.secret = 'private-token'; s.models[0].response = 'private conversation'; const json = JSON.stringify(validateSmoke(s)); assert(!json.includes('private'));
});
const html = '<html><head><title>ChatGPT</title><script src="https://cdn.oaistatic.com/assets/app-123.js?cache=one"></script></head><body data-testid="composer" role="textbox">private one</body></html>';
test('UI fingerprints ignore content, query values and unrelated remote assets', () => {
  assert.equal(fingerprintHtml(html), fingerprintHtml(html.replace('private one', 'private two').replace('cache=one', 'cache=two').replace('</head>', '<script src="https://attacker.invalid/evil.js"></script></head>')));
});
test('Changed UI asset or composer selector changes the fingerprint', () => {
  assert.notEqual(fingerprintHtml(html), fingerprintHtml(html.replace('app-123', 'app-456'))); assert.notEqual(fingerprintHtml(html), fingerprintHtml(html.replace('composer', 'new-composer')));
});
for (const body of ['challenge', '<html><title>ChatGPT</title></html>', '<html><script src="https://attacker.invalid/app.js"></script></html>']) test('Unrecognized HTML does not become a healthy UI baseline', () => assert.throws(() => fingerprintHtml(body)));
for (const status of [403, 429, 500, 503]) test(`Public HTTP ${status} is blocked rather than a confirmed SDK failure`, async () => {
  const r = await probePublic(async () => new Response('body', { status })); assert.equal(r.status, 'blocked'); assert.equal(r.fingerprint, null);
});
test('Public HTML is bounded and a normal UI is fingerprinted', async () => {
  assert.equal((await probePublic(async () => new Response(html))).status, 'passed');
  assert.equal((await probePublic(async () => new Response('X'.repeat(4 * 1024 * 1024 + 1)))).status, 'failed');
});

function publisher(report, existing = null) {
  const writes = [];
  const github = { paginate: async () => existing ? [existing] : [], rest: { issues: {
    listForRepo() {}, create: async r => writes.push(['create', r]), update: async r => writes.push(['update', r])
  } } };
  const options = { github, context: { repo: { owner: 'owner', repo: 'sdk' } }, core: { info() {} } };
  const io = { fs: { lstat: async () => ({ isFile: () => true, isSymbolicLink: () => false, size: 100 }), readFile: async () => JSON.stringify(report) } };
  return { options, io, writes };
}
for (const severity of ['CHANGE', 'CRITICAL']) test(`Issue publisher creates [${severity}] incident`, async () => {
  const s = smoke(); s.status = severity === 'CRITICAL' ? 'failed' : 'blocked';
  const f = publisher(summarize(ui(), s, baseline())); await publishMonitorIssue(f.options, f.io); assert(f.writes[0][1].title.startsWith(`[${severity}]`));
});
test('Unchanged incidents are deduplicated without daily comments', async () => {
  const s = smoke(); s.status = 'blocked'; const report = summarize(ui(), s, baseline()); const f = publisher(report); await publishMonitorIssue(f.options, f.io);
  const i = f.writes[0][1]; const again = publisher(report, { number: 9, title: i.title, body: i.body }); await publishMonitorIssue(again.options, again.io); assert.equal(again.writes.length, 0);
});
test('Existing CHANGE incident escalates in place when the SDK stops working', async () => {
  const s = smoke(); s.status = 'failed'; const f = publisher(summarize(ui(), s, baseline()), { number: 9, title: '[CHANGE] previous', body: '<!-- chatgptwebsdk-daily-monitor:v1 -->' });
  await publishMonitorIssue(f.options, f.io); assert.equal(f.writes[0][0], 'update'); assert.equal(f.writes[0][1].issue_number, 9); assert(f.writes[0][1].title.startsWith('[CRITICAL]'));
});
test('Recovery closes only the owned monitor issue', async () => {
  const f = publisher(summarize(ui(), smoke(), baseline()), { number: 9, body: '<!-- chatgptwebsdk-daily-monitor:v1 -->' }); await publishMonitorIssue(f.options, f.io); assert.equal(f.writes[0][1].state, 'closed');
  const otherIssue = publisher(summarize(ui(), smoke(), baseline()), { number: 10, body: 'Unrelated issue' }); await publishMonitorIssue(otherIssue.options, otherIssue.io); assert.equal(otherIssue.writes.length, 0);
});
test('A blocked public UI does not falsely close a previous incident', async () => {
  const f = publisher(summarize({ status: 'blocked', code: 'public_access_challenge', fingerprint: null }, smoke(), baseline()), { number: 9, body: '<!-- chatgptwebsdk-daily-monitor:v1 -->' }); await publishMonitorIssue(f.options, f.io); assert.equal(f.writes.length, 0);
});
test('Issue publisher refuses CRITICAL for a blocked example', async () => {
  const report = summarize(ui(), smoke(), baseline()); report.severity = 'CRITICAL'; report.example.status = 'blocked'; const f = publisher(report); await assert.rejects(() => publishMonitorIssue(f.options, f.io)); assert.equal(f.writes.length, 0);
});
test('Issue publisher rejects unsafe interpolated fields instead of displaying them', async () => {
  const report = summarize(ui(), smoke(), baseline()); report.example.code = 'private-token\n@everyone'; const f = publisher(report); await assert.rejects(() => publishMonitorIssue(f.options, f.io)); assert.equal(f.writes.length, 0);
});
