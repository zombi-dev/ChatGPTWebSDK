const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '../..');
const read = name => fs.readFileSync(path.join(root, name), 'utf8').replace(/\r\n/g, '\n');
function scripts(workflow) {
  return [...workflow.matchAll(/          script: \|\n((?:            .*\n|\n)+)/g)].map(m => m[1].split('\n').map(l => l.startsWith('            ') ? l.slice(12) : l).join('\n').trim());
}
for (const [workflow, source, invocation] of [
  ['release.yml', 'release.cjs', 'await publishRelease({ github, context, core });'],
  ['monitor.yml', 'monitor-issue.cjs', 'await publishMonitorIssue({ github, context, core });']
]) test(`${workflow} executes the same tested publisher implementation`, () => {
  const implementation = read('tools/ci/' + source).split('// The module export')[0].trim();
  assert.equal(scripts(read('.github/workflows/' + workflow)).at(-1), implementation + '\n' + invocation);
});
test('Workflow synchronization accepts a Windows checkout without changing the tested publisher code', () => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'chatgptweb-workflows-'));
  try {
    for (const name of ['.github/workflows/release.yml', '.github/workflows/monitor.yml', 'tools/ci/release.cjs', 'tools/ci/monitor-issue.cjs']) {
      const filename = path.join(temporary, name);
      fs.mkdirSync(path.dirname(filename), { recursive: true });
      fs.writeFileSync(filename, read(name).replace(/\n/g, '\r\n'));
    }
    const result = spawnSync(process.execPath, [path.join(root, 'tools/ci/sync-workflows.mjs')], { cwd: temporary, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr);
    for (const name of ['release.yml', 'monitor.yml'])
      assert.equal(fs.readFileSync(path.join(temporary, '.github/workflows', name), 'utf8'), read('.github/workflows/' + name));
  } finally { fs.rmSync(temporary, { recursive: true, force: true }); }
});
test('Privileged release workflow checks provenance before downloading and has no repository checkout or script execution', () => {
  const release = read('.github/workflows/release.yml'); assert(!release.includes('actions/checkout')); assert(!/^\s+run:/m.test(release));
  assert(release.indexOf('await verifyReleaseSource') < release.indexOf('actions/download-artifact'));
  assert(!release.includes('ref: ${{')); assert(release.includes('head_repository.full_name == github.repository'));
});
test('Build is reusable and follows every successful test matrix job', () => {
  const build = read('.github/workflows/build.yml'), tests = read('.github/workflows/tests.yml');
  assert(build.includes('workflow_call:')); assert(!build.includes('workflow_run')); assert(tests.includes('needs: tests')); assert(tests.includes('uses: ./.github/workflows/build.yml'));
  assert(tests.includes("github.event_name != 'pull_request'")); assert(!build.includes('contents: write'));
  const prepare = build.indexOf('Bundle-Browser.ps1 -Stage Prepare'), verify = build.indexOf('Bundle-Browser.ps1 -Stage Verify'), archive = build.indexOf('Bundle-Browser.ps1 -Stage Archive');
  assert(prepare > 0 && prepare < verify && verify < archive && archive < build.indexOf('Upload platform browser bundle'));
});
test('Daily authenticated probe is limited to the default branch and its credential never reaches the publisher', () => {
  const monitor = read('.github/workflows/monitor.yml'); const publisher = monitor.slice(monitor.indexOf('  report:\n'));
  assert(monitor.includes('cron: "15 7 * * *"')); assert(monitor.includes('github.event.repository.default_branch')); assert(monitor.includes('--smoke-test'));
  assert(monitor.indexOf('./test.ps1') < monitor.indexOf('dotnet publish')); assert(monitor.includes('CHATGPT_WEB_MONITOR_AUTH'));
  assert(monitor.includes('xvfb-run -a')); assert(monitor.includes('--smoke-test --visible'));
  assert(!publisher.includes('CHATGPT_WEB_AUTH')); assert(!publisher.includes('actions/checkout')); assert(publisher.includes('issues: write')); assert(publisher.includes('contents: none'));
});
test('Dependabot covers actions and NuGet, and the README delegates references', () => {
  const deps = read('.github/dependabot.yml'); assert(deps.includes('package-ecosystem: github-actions')); assert(deps.includes('package-ecosystem: nuget'));
  assert(!read('README.md').includes('## Sources')); assert(read('README.md').includes('docs/REFERENCES.md')); assert(read('SECURITY.md').includes('private vulnerability'));
});
