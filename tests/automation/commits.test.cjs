const { test } = require('node:test');
const assert = require('node:assert/strict');
const { validateTitle, validateVersionMention } = require('../../tools/ci/check-commit.cjs');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

for (const title of ['feat(models): add GPT-6', 'fix(security): verify release provenance', 'build(deps): update test tooling',
  'ci(windows): support CRLF', 'chore(release): 1.5.0', 'refactor(mcp)!: simplify registrations'])
  test(`Categorized Conventional Commit is accepted: ${title}`, () => assert(validateTitle(title)));
for (const title of ['', 'update stuff', 'fix: missing category', 'security: protect assets', 'feat(models): ',
  'fix(Security): invalid scope', 'fix(ci): line one\nline two', 'fix(ci): trailing space ', 'fix(ci): ' + 'x'.repeat(100)])
  test('Uncategorized or malformed commit title is rejected: ' + JSON.stringify(title), () => assert(!validateTitle(title)));

for (const message of ['For 1.5.1.', 'Release v1.5.1', 'chore(release): 1.5.1', 'For 1.5.1\n\nCo-authored-by: Example',
  'Release (1.5.1).', '1.5.1, maintenance'])
  test('Version mention accepts punctuation and commit bodies: ' + JSON.stringify(message), () => assert(validateVersionMention(message, '1.5.1')));
for (const [message, version] of [['For 1.5.10.', '1.5.1'], ['For 11.5.1.', '1.5.1'], ['For 1.5.1.2.', '1.5.1'],
  ['For 1.5.0.', '1.5.1'], ['For 1.15.1.', '1.5.1'], ['', '1.5.1'], ['No release mentioned', '1.5.1'], ['For 1.5.1.', '1.5.*']])
  test('Version mention rejects different or malformed versions: ' + JSON.stringify([message, version]), () => assert(!validateVersionMention(message, version)));
test('The CI commit command checks the real commit body and rejects another version', t => {
  const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'sdk-commit-'));
  t.after(() => fs.rmSync(temporary, { recursive: true, force: true }));
  const git = args => {
    const result = spawnSync('git', args, { cwd: temporary, encoding: 'utf8' });
    assert.equal(result.status, 0, result.stderr); return result.stdout.trim();
  };
  git(['init', '--initial-branch=main']);
  git(['-c', 'user.name=SDK Test', '-c', 'user.email=sdk-test@example.invalid', '-c', 'commit.gpgsign=false',
    'commit', '--allow-empty', '-m', 'docs(ci): verify punctuation', '-m', 'For 1.5.1.']);
  const command = [path.resolve(__dirname, '../../tools/ci/check-commit.cjs'), git(['rev-parse', 'HEAD'])];
  const accepted = spawnSync(process.execPath, [...command, '1.5.1'], { cwd: temporary, encoding: 'utf8' });
  assert.equal(accepted.status, 0, accepted.stderr);
  const rejected = spawnSync(process.execPath, [...command, '1.5.2'], { cwd: temporary, encoding: 'utf8' });
  assert.notEqual(rejected.status, 0); assert.match(rejected.stderr, /Mention version 1\.5\.2/);
});
