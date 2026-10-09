import { createHash } from 'node:crypto';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const digest = value => createHash('sha256').update(JSON.stringify(value)).digest('hex');
const statuses = new Set(['passed', 'degraded', 'failed', 'blocked']);
const identifier = value => typeof value === 'string' && /^[a-zA-Z0-9_.-]{1,80}$/.test(value);

export function validateSmoke(value) {
  if (!value || value.schema !== 1 || !statuses.has(value.status) || !identifier(value.code) ||
      !Array.isArray(value.catalog) || value.catalog.length > 128 || value.catalog.some(v => !identifier(v)) ||
      !Array.isArray(value.models) || value.models.length > 3 || value.models.some(m => !m || !identifier(m.model) ||
      !['passed', 'failed', 'blocked'].includes(m.status) || !identifier(m.code))) throw new Error('Invalid example report.');
  if (value.apiShape !== undefined && !/^[a-f0-9]{64}$/.test(value.apiShape)) throw new Error('Invalid API fingerprint.');
  if (value.uiFingerprint != null && !/^[a-f0-9]{64}$/.test(value.uiFingerprint)) throw new Error('Invalid browser UI fingerprint.');
  return {
    schema: 1, status: value.status, code: value.code, catalog: [...new Set(value.catalog)].sort(), apiShape: value.apiShape ?? null, uiFingerprint: value.uiFingerprint ?? null,
    models: value.models.map(m => ({ model: m.model, status: m.status, code: m.code, streaming: m.streaming === true, continuation: m.continuation === true }))
  };
}

export function fingerprintHtml(html) {
  if (typeof html !== 'string' || html.length > 4 * 1024 * 1024 || !/<(?:html|head)\b/i.test(html) || !/chatgpt/i.test(html))
    throw new Error('Unexpected public UI.');
  // Only public structural identifiers and asset paths are retained, never HTML or account content.
  const markers = [...html.matchAll(/(?:data-testid|data-test-id|role)=["']([a-zA-Z0-9_-]{1,80})["']/g)].map(m => m[1]);
  const assets = [...html.matchAll(/<(?:script|link)\b[^>]*?(?:src|href)=["']([^"']+)["']/gi)].flatMap(m => {
    try {
      const url = new URL(m[1].replaceAll('&amp;', '&'), 'https://chatgpt.com/');
      return url.protocol === 'https:' && ['chatgpt.com', 'cdn.oaistatic.com'].includes(url.hostname) && /\.(?:js|css)$/.test(url.pathname) ? [url.origin + url.pathname] : [];
    } catch { return []; }
  });
  if (assets.length === 0) throw new Error('Public UI has no recognized assets.');
  return digest({ markers: [...new Set(markers)].sort(), assets: [...new Set(assets)].sort() });
}

export async function probePublic(fetcher = fetch) {
  try {
    const response = await fetcher('https://chatgpt.com/', {
      redirect: 'manual', signal: AbortSignal.timeout(45000), headers: { 'user-agent': 'Mozilla/5.0 ChatGPTWebSdkMonitor/1.4', accept: 'text/html' }
    });
    if (response.status === 403 || response.status === 429 || response.headers.get('cf-mitigated') === 'challenge') return { status: 'blocked', code: 'public_access_challenge', fingerprint: null };
    if (response.status !== 200) return { status: response.status >= 500 ? 'blocked' : 'failed', code: `public_http_${response.status}`, fingerprint: null };
    const reader = response.body.getReader(); const parts = []; let size = 0;
    try {
      while (true) {
        const { value, done } = await reader.read(); if (done) break;
        size += value.byteLength; if (size > 4 * 1024 * 1024) throw new Error('Public UI exceeded limit.'); parts.push(value);
      }
    } finally { await reader.cancel(); }
    return { status: 'passed', code: 'ok', fingerprint: fingerprintHtml(Buffer.concat(parts).toString('utf8')) };
  } catch (error) {
    return { status: error.name === 'TimeoutError' || error.name === 'TypeError' ? 'blocked' : 'failed', code: 'public_probe_unavailable', fingerprint: null };
  }
}

export function summarize(publicUi, rawSmoke, baseline) {
  const smoke = validateSmoke(rawSmoke);
  if (smoke.uiFingerprint) publicUi = { status: 'passed', code: 'browser_ui_observed', fingerprint: smoke.uiFingerprint };
  else if (baseline.uiSource === 'browser') publicUi = { status: 'blocked', code: 'browser_ui_not_observed', fingerprint: null };
  if (!publicUi || !['passed', 'failed', 'blocked'].includes(publicUi.status) || !identifier(publicUi.code) ||
      publicUi.fingerprint !== null && !/^[a-f0-9]{64}$/.test(publicUi.fingerprint)) throw new Error('Invalid UI report.');
  if (!baseline || baseline.schema !== 1 || !Array.isArray(baseline.catalog) || baseline.catalog.some(s => !identifier(s)) ||
      ['apiShape', 'ui'].some(k => baseline[k] !== null && !/^[a-f0-9]{64}$/.test(baseline[k]))) throw new Error('Invalid monitor baseline.');
  const changes = [];
  if (publicUi.fingerprint && baseline.ui && publicUi.fingerprint !== baseline.ui) changes.push('ui_assets_changed');
  if (smoke.apiShape && baseline.apiShape && smoke.apiShape !== baseline.apiShape) changes.push('api_schema_changed');
  if (smoke.apiShape && JSON.stringify(smoke.catalog) !== JSON.stringify([...new Set(baseline.catalog)].sort())) changes.push('model_catalog_changed');
  if (!baseline.ui || !baseline.apiShape) changes.push('baseline_incomplete');
  if (publicUi.status === 'failed') changes.push('ui_probe_failed');
  if (smoke.status !== 'passed') changes.push(`example_${smoke.status}`);
  const severity = smoke.status === 'failed' ? 'CRITICAL' : changes.length ? 'CHANGE' : 'HEALTHY';
  return {
    schema: 1, severity, changes, ui: { status: publicUi.status, code: publicUi.code },
    example: { status: smoke.status, code: smoke.code, models: smoke.models },
    fingerprints: { ui: publicUi.fingerprint, apiShape: smoke.apiShape, catalog: smoke.catalog }
  };
}

async function main() {
  const args = process.argv.slice(2);
  const argument = (name, fallback) => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : fallback; };
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
  const smokePath = argument('--smoke', path.join(root, 'artifacts/monitor/smoke.json'));
  const output = argument('--output', path.join(root, 'artifacts/monitor/report.json'));
  const baselinePath = argument('--baseline', path.join(root, 'tools/monitor/baseline.json'));
  let smoke;
  try { smoke = JSON.parse(await readFile(smokePath, 'utf8')); }
  catch { smoke = { schema: 1, status: 'blocked', code: 'example_report_missing', catalog: [], models: [] }; }
  const ui = await probePublic();
  const baseline = JSON.parse(await readFile(baselinePath, 'utf8'));
  const report = summarize(ui, smoke, baseline);
  await mkdir(path.dirname(output), { recursive: true });
  await writeFile(output, JSON.stringify(report, null, 2) + '\n');
  if (args.includes('--accept-baseline')) {
    if (smoke.status !== 'passed' || report.ui.status !== 'passed') throw new Error('Both example and UI checks must pass before accepting a baseline.');
    await writeFile(baselinePath, JSON.stringify({ schema: 1, ui: report.fingerprints.ui, uiSource: smoke.uiFingerprint ? 'browser' : 'public', apiShape: smoke.apiShape, catalog: report.fingerprints.catalog }, null, 2) + '\n');
  }
  console.log(`Daily check: ${report.severity}; example=${report.example.status}; UI=${report.ui.status}.`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
