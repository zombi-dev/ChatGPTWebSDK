// Embedded verbatim in the Release workflow. The publishing job has no checkout and executes no artifact.
async function verifyReleaseSource({ github, context }) {
  const repo = context.repo;
  const { data: repository } = await github.rest.repos.get(repo);
  const expected = `${repo.owner}/${repo.repo}`;
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: context.payload.workflow_run.id });
  if (run.path !== '.github/workflows/tests.yml' || run.conclusion !== 'success' ||
      !['push', 'workflow_dispatch'].includes(run.event) || run.head_repository?.full_name !== expected ||
      run.repository?.full_name !== expected || run.head_branch !== repository.default_branch ||
      !/^[a-f0-9]{40}$/.test(run.head_sha)) throw new Error('Untrusted release provenance.');
  const { data: comparison } = await github.rest.repos.compareCommitsWithBasehead({ ...repo, basehead: `${run.head_sha}...${repository.default_branch}` });
  if (!['identical', 'ahead'].includes(comparison.status)) throw new Error('The tested commit is not on the default branch.');
  const { data: versionFile } = await github.rest.repos.getContent({ ...repo, path: 'VERSION', ref: run.head_sha });
  if (versionFile.type !== 'file' || versionFile.encoding !== 'base64' || versionFile.size > 64) throw new Error('Invalid version file.');
  const version = Buffer.from(versionFile.content, 'base64').toString('utf8').trim();
  if (!/^(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})\.(0|[1-9]\d{0,5})$/.test(version)) throw new Error('Invalid release version.');
  const artifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, { ...repo, run_id: run.id, per_page: 100 });
  const names = ['browser-linux-x64', 'browser-osx-arm64', 'browser-osx-x64', 'browser-win-x64', 'release-common'];
  if (artifacts.length !== names.length || names.some(n => artifacts.filter(a => a.name === n && !a.expired && a.size_in_bytes > 0 &&
      a.workflow_run?.id === run.id && a.workflow_run?.head_sha === run.head_sha).length !== 1)) throw new Error('Incomplete or mismatched build artifacts.');
  return { version, commit: run.head_sha, runId: run.id };
}

async function publishRelease({ github, context, core }, io = {}) {
  const fs = io.fs ?? require('node:fs/promises');
  const path = require('node:path');
  const crypto = require('node:crypto');
  const { version, commit } = await verifyReleaseSource({ github, context });
  const repo = context.repo;
  const tag = `v${version}`;
  const root = path.resolve(io.root ?? 'artifacts/release');
  const names = [
    ...['ChatGPTWebSdk', 'ChatGPTWebSdk.Browser', 'ChatGPTWebSdk.OpenAI'].map(n => `${n}.${version}.nupkg`),
    ...['DLLs', 'Proxy'].map(n => `ChatGPTWebSdk-${n}-${version}.zip`),
    `chatgpt-web-sdk-auth-chromium-${version}.zip`, `chatgpt-web-sdk-auth-firefox-${version}.zip`, `chatgpt-web-sdk-auth-firefox-${version}.xpi`,
    `ChatGPTWebSdk-Bundle-win-x64-${version}.zip`,
    ...['linux-x64', 'osx-x64', 'osx-arm64'].map(n => `ChatGPTWebSdk-Bundle-${n}-${version}.tar.gz`)
  ].sort();
  const entries = await fs.readdir(root);
  if (entries.length !== names.length || entries.some(n => !names.includes(n))) throw new Error('Unexpected release asset set.');
  const assets = [];
  for (const name of names) {
    const filename = path.join(root, name);
    const stat = await fs.lstat(filename);
    if (!stat.isFile() || stat.isSymbolicLink() || stat.size < 4 || stat.size > 1024 * 1024 * 1024) throw new Error('Invalid release asset.');
    const handle = await fs.open(filename, 'r');
    let digest;
    try {
      const magic = Buffer.alloc(4); await handle.read(magic, 0, 4, 0);
      if (name.endsWith('.gz') ? magic[0] !== 0x1f || magic[1] !== 0x8b : magic[0] !== 0x50 || magic[1] !== 0x4b)
        throw new Error('Release asset is not an archive.');
      const hash = crypto.createHash('sha256');
      for await (const chunk of handle.createReadStream({ start: 0, autoClose: false })) hash.update(chunk);
      digest = hash.digest('hex');
    } finally { await handle.close(); }
    assets.push({ name, filename, size: stat.size, digest });
  }
  const checksum = Buffer.from(assets.map(a => `${a.digest}  ${a.name}\n`).join(''));
  assets.push({ name: 'SHA256SUMS.txt', data: checksum, size: checksum.length, digest: crypto.createHash('sha256').update(checksum).digest('hex') });
  const notFound = e => e.status === 404;
  // Check a pre-existing tag even when the release was deleted. Versions never move between commits.
  try {
    const { data: tagged } = await github.rest.repos.getCommit({ ...repo, ref: tag });
    if (tagged.sha !== commit) throw new Error('Version is tagged at another commit. Bump VERSION.');
  } catch (e) { if (!notFound(e)) throw e; }
  let release;
  try { release = (await github.rest.repos.getReleaseByTag({ ...repo, tag })).data; }
  catch (e) { if (!notFound(e)) throw e; }
  if (release?.immutable) {
    if (release.assets.length !== assets.length || assets.some(a => !release.assets.some(b => b.name === a.name && b.size === a.size && b.digest === `sha256:${a.digest}`)))
      throw new Error('Immutable release assets differ from the verified build.');
    core.info(`${tag} is already complete and immutable.`); return;
  }
  if (!release) {
    const { data: notes } = await github.rest.repos.getContent({ ...repo, path: 'docs/RELEASE_NOTES.md', ref: commit });
    if (notes.type !== 'file' || notes.encoding !== 'base64' || notes.size > 32768) throw new Error('Invalid release notes.');
    release = (await github.rest.repos.createRelease({ ...repo, tag_name: tag, target_commitish: commit, name: tag,
      body: Buffer.from(notes.content, 'base64').toString('utf8'), draft: true })).data;
  }
  if (!release.draft) throw new Error('Existing published release is mutable. Inspect it before modifying it.');
  for (const existing of release.assets) {
    const expected = assets.find(a => a.name === existing.name);
    if (!expected || existing.size !== expected.size || existing.digest !== `sha256:${expected.digest}`)
      await github.rest.repos.deleteReleaseAsset({ ...repo, asset_id: existing.id });
  }
  for (const asset of assets) {
    if (release.assets.some(a => a.name === asset.name && a.size === asset.size && a.digest === `sha256:${asset.digest}`)) continue;
    await github.rest.repos.uploadReleaseAsset({ ...repo, release_id: release.id, name: asset.name,
      headers: { 'content-type': 'application/octet-stream', 'content-length': asset.size }, data: asset.data ?? await fs.readFile(asset.filename) });
  }
  await github.rest.repos.updateRelease({ ...repo, release_id: release.id, draft: false });
  core.info(`Published ${tag} from the tested commit ${commit}.`);
}

// The module export is omitted from the embedded workflow script.
module.exports = { verifyReleaseSource, publishRelease };
