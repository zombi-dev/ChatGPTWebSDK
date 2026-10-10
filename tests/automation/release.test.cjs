const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { verifyReleaseSource, publishRelease } = require('../../tools/ci/release.cjs');

function fixture() {
  const sha = 'a'.repeat(40), writes = [];
  const run = { id: 10, path: '.github/workflows/tests.yml', conclusion: 'success', event: 'push', head_sha: sha,
    head_branch: 'main', head_repository: { full_name: 'owner/sdk' }, repository: { full_name: 'owner/sdk' } };
  const artifacts = ['browser-linux-x64', 'browser-osx-arm64', 'browser-osx-x64', 'browser-win-x64', 'release-common']
    .map((name, i) => ({ id: i, name, expired: false, size_in_bytes: 50, workflow_run: { id: 10, head_sha: sha } }));
  const state = { version: '1.4.0', comparison: 'ahead', tag: null, tagRef: null, existing: null, artifacts, run,
    commits: [{ sha, commit: { message: 'chore(release): 1.4.0' } }], releases: [] };
  const missing = () => { const error = new Error('missing'); error.status = 404; throw error; };
  const github = { paginate: async method => method === github.rest.repos.listCommits ? state.commits :
    method === github.rest.repos.listReleases ? state.releases : artifacts, rest: { actions: {
    getWorkflowRun: async () => ({ data: run }), listWorkflowRunArtifacts() {}
  }, git: {
    getRef: async ({ ref }) => {
      assert.equal(ref, 'tags/v1.4.0');
      return state.tag ? { data: { object: { sha: state.tagRef ?? state.tag } } } : missing();
    }
  }, repos: {
    listCommits() {}, listReleases() {},
    get: async () => ({ data: { default_branch: 'main' } }),
    compareCommitsWithBasehead: async () => ({ data: { status: state.comparison } }),
    getContent: async ({ path: name, ref }) => {
      assert.equal(ref, sha); const content = name === 'VERSION' ? state.version : 'SDK release notes.';
      return { data: { type: 'file', encoding: 'base64', content: Buffer.from(content).toString('base64'), size: Buffer.byteLength(content) } };
    },
    getCommit: async ({ ref }) => {
      assert.equal(ref, 'refs/tags/v1.4.0');
      if (state.tag) return { data: { sha: state.tag } };
      const error = new Error('No commit found for SHA'); error.status = 422; throw error;
    },
    getReleaseByTag: async () => state.existing ? { data: state.existing } : missing(),
    createRelease: async data => { writes.push(['create', data]); return { data: { id: 20, draft: true, assets: [] } }; },
    deleteReleaseAsset: async data => { writes.push(['delete', data]); },
    uploadReleaseAsset: async data => { writes.push(['upload', data]); },
    updateRelease: async data => { writes.push(['update', data]); }
  } } };
  return { state, github, context: { repo: { owner: 'owner', repo: 'sdk' }, payload: { workflow_run: { id: 10 } } }, core: { info() {} }, writes, sha };
}

const unsafe = {
  'fork test run': f => { f.state.run.head_repository.full_name = 'attacker/sdk'; },
  'different repository': f => { f.state.run.repository.full_name = 'attacker/sdk'; },
  'pull request': f => { f.state.run.event = 'pull_request'; },
  'pull request target': f => { f.state.run.event = 'pull_request_target'; },
  'wrong workflow': f => { f.state.run.path = '.github/workflows/other.yml'; },
  'unsuccessful tests': f => { f.state.run.conclusion = 'failure'; },
  'feature branch': f => { f.state.run.head_branch = 'feature'; },
  'malformed commit': f => { f.state.run.head_sha = 'bad; execute'; },
  'unrelated history': f => { f.state.comparison = 'diverged'; },
  'commit beyond branch': f => { f.state.comparison = 'behind'; },
  'injected version': f => { f.state.version = '1.4.0\nexecute'; },
  'noncanonical version': f => { f.state.version = '01.4.0'; },
  'oversized version': f => { f.state.version = '1'.repeat(65); },
  'missing artifact': f => { f.state.artifacts.pop(); },
  'unexpected artifact': f => { f.state.artifacts.push({ ...f.state.artifacts[0], name: 'executable-code' }); },
  'duplicate artifact': f => { f.state.artifacts[1].name = f.state.artifacts[0].name; },
  'expired artifact': f => { f.state.artifacts[0].expired = true; },
  'empty artifact': f => { f.state.artifacts[0].size_in_bytes = 0; },
  'artifact from different run': f => { f.state.artifacts[0].workflow_run.id = 11; },
  'artifact from different commit': f => { f.state.artifacts[0].workflow_run.head_sha = 'b'.repeat(40); }
};
for (const [name, mutate] of Object.entries(unsafe)) test(`Release rejects ${name} before any write`, async () => {
  const f = fixture(); mutate(f); await assert.rejects(() => verifyReleaseSource(f)); assert.equal(f.writes.length, 0);
});
for (const event of ['push', 'workflow_dispatch']) for (const comparison of ['ahead', 'identical']) test(`Release accepts tested ${event} on ${comparison} default-branch history`, async () => {
  const f = fixture(); f.state.run.event = event; f.state.comparison = comparison;
  assert.deepEqual(await verifyReleaseSource(f), { version: '1.4.0', commit: f.sha, runId: 10 });
});

async function assets(t) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), 'websdk-release-')); t.after(() => fs.rm(root, { recursive: true, force: true }));
  const names = [
    'ChatGPTWebSdk.1.4.0.nupkg', 'ChatGPTWebSdk.Browser.1.4.0.nupkg', 'ChatGPTWebSdk.OpenAI.1.4.0.nupkg',
    'ChatGPTWebSdk-DLLs-1.4.0.zip', 'ChatGPTWebSdk-Proxy-1.4.0.zip', 'chatgpt-web-sdk-auth-chromium-1.4.0.zip',
    'chatgpt-web-sdk-auth-firefox-1.4.0.zip', 'chatgpt-web-sdk-auth-firefox-1.4.0.xpi', 'ChatGPTWebSdk-Bundle-win-x64-1.4.0.zip',
    ...['linux-x64', 'osx-x64', 'osx-arm64'].map(p => `ChatGPTWebSdk-Bundle-${p}-1.4.0.tar.gz`)
  ];
  for (const name of names) await fs.writeFile(path.join(root, name), name.endsWith('.gz') ? Buffer.from([0x1f, 0x8b, 8, 0, 1, 2]) : Buffer.from([0x50, 0x4b, 3, 4, 1, 2]));
  return { root, names };
}
test('Release uploads twelve validated binary assets and checksums, then publishes its draft', async t => {
  const f = fixture(), files = await assets(t); await publishRelease(f, files);
  assert.equal(f.writes[0][0], 'create'); assert.equal(f.writes[0][1].draft, true); assert.equal(f.writes[0][1].target_commitish, f.sha);
  assert.equal(f.writes[0][1].name, 'v1.4.0');
  assert(f.writes[0][1].body.includes('<summary>All 1 commit</summary>'));
  assert(f.writes[0][1].body.includes('https://github.com/owner/sdk/commit/' + f.sha));
  const uploads = f.writes.filter(w => w[0] === 'upload'); assert.equal(uploads.length, 13);
  const sums = uploads.find(w => w[1].name === 'SHA256SUMS.txt')[1].data.toString();
  for (const [, a] of uploads.filter(w => w[1].name !== 'SHA256SUMS.txt')) {
    assert(sums.includes(crypto.createHash('sha256').update(a.data).digest('hex') + '  ' + a.name));
    assert.equal(a.headers['content-length'], a.data.length);
  }
  assert.deepEqual(f.writes.at(-1), ['update', { owner: 'owner', repo: 'sdk', release_id: 20, draft: false }]);
});
for (const [name, mutate] of Object.entries({
  missing: async f => fs.unlink(path.join(f.root, f.names[0])),
  unexpected: async f => fs.writeFile(path.join(f.root, 'run-me.cjs'), 'malicious code'),
  empty: async f => fs.writeFile(path.join(f.root, f.names[0]), ''),
  'not an archive': async f => fs.writeFile(path.join(f.root, f.names[0]), 'malicious code'),
  directory: async f => { await fs.unlink(path.join(f.root, f.names[0])); await fs.mkdir(path.join(f.root, f.names[0])); }
})) test(`Release rejects ${name} asset data without publishing`, async t => {
  const f = fixture(), files = await assets(t); await mutate(files); await assert.rejects(() => publishRelease(f, files)); assert.equal(f.writes.length, 0);
});
test('Release rejects symbolic-link asset metadata', async t => {
  const f = fixture(), files = await assets(t);
  const unsafeFs = { ...fs, lstat: async () => ({ isFile: () => true, isSymbolicLink: () => true, size: 6 }) };
  await assert.rejects(() => publishRelease(f, { ...files, fs: unsafeFs })); assert.equal(f.writes.length, 0);
});
test('Release refuses to move an existing version tag', async t => {
  const f = fixture(), files = await assets(t); f.state.tag = 'b'.repeat(40);
  await assert.rejects(() => publishRelease(f, files), /another commit/); assert.equal(f.writes.length, 0);
});
test('Release creates a new tag when reference lookup returns 404, without a missing-commit lookup', async t => {
  const f = fixture(), files = await assets(t); await publishRelease(f, files);
  assert.equal(f.writes[0][0], 'create'); assert.equal(f.writes.at(-1)[0], 'update');
});
for (const status of [401, 403, 422, 500]) test(`Release propagates reference lookup HTTP ${status} without writing`, async t => {
  const f = fixture(), files = await assets(t);
  f.github.rest.git.getRef = async () => { const error = new Error('Reference lookup failed'); error.status = status; throw error; };
  await assert.rejects(() => publishRelease(f, files), error => error.status === status); assert.equal(f.writes.length, 0);
});
test('Release resolves annotated tags to the tested commit', async t => {
  const f = fixture(), files = await assets(t); f.state.tag = f.sha; f.state.tagRef = 'c'.repeat(40);
  await publishRelease(f, files); assert.equal(f.writes[0][0], 'create'); assert.equal(f.writes.at(-1)[0], 'update');
});
test('Release preserves complete immutable assets on an exact rerun', async t => {
  const f = fixture(), files = await assets(t); await publishRelease(f, files);
  f.state.tag = f.sha; f.state.existing = { id: 20, immutable: true, assets: f.writes.filter(w => w[0] === 'upload').map(([, a]) =>
    ({ name: a.name, size: a.data.length, digest: 'sha256:' + crypto.createHash('sha256').update(a.data).digest('hex') })) };
  f.writes.length = 0; await publishRelease(f, files); assert.equal(f.writes.length, 0);
  f.state.existing.assets[0].digest = 'sha256:' + '0'.repeat(64); await assert.rejects(() => publishRelease(f, files), /Immutable/); assert.equal(f.writes.length, 0);
});
test('Release leaves an already published mutable release unchanged', async t => {
  const f = fixture(), files = await assets(t); f.state.tag = f.sha; f.state.existing = { id: 20, draft: false, immutable: false, assets: [] };
  await assert.rejects(() => publishRelease(f, files), /mutable/); assert.equal(f.writes.length, 0);
});
test('Release resumes an interrupted draft, uploads missing assets and publishes last', async t => {
  const f = fixture(), files = await assets(t); f.state.tag = f.sha; f.state.existing = { id: 20, draft: true, assets: [{ id: 30, name: 'untrusted.cjs', size: 10, digest: null }] };
  await publishRelease(f, files); assert.equal(f.writes[0][0], 'delete'); assert.equal(f.writes.filter(w => w[0] === 'upload').length, 13); assert.equal(f.writes.at(-1)[0], 'update');
});
