const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { createGithubReader, main } = require('../../tools/ci/format-release-notes.cjs');

test('The manual publisher reads every GitHub API page, including an exact full last page', async () => {
  const calls = [], reader = createGithubReader({ owner: 'owner', repo: 'sdk' }, route => {
    calls.push(route); return route.endsWith('page=1') || route.endsWith('page=2') ? Array(100).fill({}) : [];
  });
  const data = await reader.paginate(reader.rest.repos.listCommits, { sha: 'a'.repeat(40), per_page: 100 });
  assert.equal(data.length, 200); assert.equal(calls.length, 3);
  assert(calls[0].includes('?sha=' + 'a'.repeat(40) + '&per_page=100&page=1'));
});
test('The manual publisher encodes tag references as data', async () => {
  const reader = createGithubReader({ owner: 'owner', repo: 'sdk' }, route => ({ route }));
  const result = await reader.rest.repos.getCommit({ ref: 'refs/tags/v1.5.0' });
  assert.equal(result.data.route, 'repos/owner/sdk/commits/refs%2Ftags%2Fv1.5.0');
});
test('The manual publisher rejects malformed GitHub pages', async () => {
  const reader = createGithubReader({ owner: 'owner', repo: 'sdk' }, () => ({ unexpected: true }));
  await assert.rejects(() => reader.paginate(reader.rest.repos.listReleases, { per_page: 100 }), /API page/);
});
test('The manual command writes the same collapsed commit appendix as the workflow', async t => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'sdk-release-notes-'));
  t.after(() => fs.rm(root, { recursive: true, force: true }));
  const source = path.join(root, 'notes.md'), output = path.join(root, 'formatted.md'), sha = 'a'.repeat(40);
  await fs.writeFile(source, 'A useful release summary.');
  await main(['owner/sdk', sha, '1.5.1', source, output], route =>
    route.includes('/commits?') ? [{ sha, commit: { message: 'chore(release): 1.5.1' } }] : []);
  const text = await fs.readFile(output, 'utf8');
  assert(text.startsWith('A useful release summary.\n\n<details>'));
  assert(text.includes('https://github.com/owner/sdk/commit/' + sha)); assert(text.includes('All 1 commit'));
});
test('Invalid manual command arguments fail before any API access', async () => {
  await assert.rejects(() => main(['bad;repo', 'invalid', '1.5.1', 'notes', 'output'], () => assert.fail('No API access')), /Usage/);
});
