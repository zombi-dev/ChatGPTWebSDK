// Embedded in the issue-writing job. Its only input is bounded, sanitized report data.
async function publishMonitorIssue({ github, context, core }, io = {}) {
  const fs = io.fs ?? require('node:fs/promises');
  const filename = io.filename ?? 'artifacts/monitor/report.json';
  const stat = await fs.lstat(filename);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > 32768) throw new Error('Invalid monitor report file.');
  const report = JSON.parse(await fs.readFile(filename, 'utf8'));
  const identifier = s => typeof s === 'string' && /^[a-zA-Z0-9_.-]{1,80}$/.test(s);
  const statuses = ['passed', 'failed', 'blocked', 'degraded'];
  if (report.schema !== 1 || !['HEALTHY', 'CHANGE', 'CRITICAL', 'UNAVAILABLE'].includes(report.severity) ||
      !Array.isArray(report.changes) || report.changes.length > 8 || report.changes.some(c => !identifier(c)) ||
      !statuses.includes(report.example?.status) || !identifier(report.example?.code) ||
      !['passed', 'failed', 'blocked'].includes(report.ui?.status) || !identifier(report.ui?.code) ||
      !Array.isArray(report.example.models) || report.example.models.length > 3 ||
      report.example.models.some(m => !identifier(m.model) || !statuses.includes(m.status) || !identifier(m.code))) throw new Error('Invalid monitor report data.');
  if (report.severity === 'CRITICAL' && report.example.status !== 'failed') throw new Error('Critical status lacks confirmed SDK failure.');
  if (report.severity === 'CHANGE' && report.changes.length === 0) throw new Error('Change status lacks observed changes.');
  if (report.severity === 'UNAVAILABLE') {
    if (report.changes.length || report.example.status === 'failed') throw new Error('Unavailable status hides a confirmed change or failure.');
    core.warning('ChatGPT checks were blocked or incomplete. See the sanitized artifact; no change issue was opened.');
    return;
  }
  const marker = '<!-- chatgptwebsdk-daily-monitor:v1 -->';
  const repo = context.repo;
  const issues = await github.paginate(github.rest.issues.listForRepo, { ...repo, state: 'open', creator: 'github-actions[bot]', per_page: 100 });
  const existing = issues.find(i => !i.pull_request && i.body?.startsWith(marker));
  if (report.severity === 'HEALTHY') {
    // Never close an incident while either probe is blocked or before all model checks pass.
    if (existing && report.example.status === 'passed' && report.ui.status === 'passed') {
      await github.rest.issues.update({ ...repo, issue_number: existing.number, state: 'closed', state_reason: 'completed' });
      core.info('Daily checks recovered; monitor issue closed.');
    }
    return;
  }
  const title = `[${report.severity}] ChatGPT web changes and SDK example health`;
  const lines = [marker, '', 'The daily monitor observed a change or SDK failure.', '',
    `- Changes: ${report.changes.join(', ') || 'example_failure'}`, `- Public UI: ${report.ui.status} (${report.ui.code})`,
    `- SDK example: ${report.example.status} (${report.example.code})`,
    ...report.example.models.map(m => `- ${m.model}: ${m.status} (${m.code}); streaming=${m.streaming === true}, continuation=${m.continuation === true}`), '',
    'Blocked checks alone do not open issues. [CRITICAL] means all attempted supported models failed, or the SDK could not parse/use the available API.', '',
    `Workflow: https://github.com/${repo.owner}/${repo.repo}/actions/workflows/monitor.yml`,
    'Download the sanitized monitor-report artifact for fingerprints. Refresh CHATGPT_WEB_MONITOR_AUTH when sign-in expires.'];
  const body = lines.join('\n');
  if (existing) {
    if (existing.title !== title || existing.body !== body) await github.rest.issues.update({ ...repo, issue_number: existing.number, title, body });
    core.info('Existing monitor issue retained.');
  } else {
    await github.rest.issues.create({ ...repo, title, body }); core.info('Monitor issue created.');
  }
}

// The module export is omitted from the embedded workflow script.
module.exports = { publishMonitorIssue };
