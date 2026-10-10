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
  let tagExists = false;
  try {
    // Missing commit lookups return 422; exact reference lookups return 404.
    await github.rest.git.getRef({ ...repo, ref: `tags/${tag}` });
    tagExists = true;
  } catch (e) { if (!notFound(e)) throw e; }
  if (tagExists) {
    // Resolve annotated tags to their commit before comparing targets.
    const { data: tagged } = await github.rest.repos.getCommit({ ...repo, ref: `refs/tags/${tag}` });
    if (tagged.sha !== commit) throw new Error('Version is tagged at another commit. Bump VERSION.');
  }
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
    const changes = await collectReleaseChanges({ github, context }, { version, commit });
    release = (await github.rest.repos.createRelease({ ...repo, tag_name: tag, target_commitish: commit, name: tag,
      body: formatReleaseNotes(Buffer.from(notes.content, 'base64').toString('utf8'), { ...changes, repo }), draft: true })).data;
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

function compareVersions(left, right) {
  const a = left.replace(/^v/, '').split('.').map(Number), b = right.replace(/^v/, '').split('.').map(Number);
  for (let i = 0; i < 3; i++) if (a[i] !== b[i]) return a[i] - b[i];
  return 0;
}

async function collectReleaseChanges({ github, context }, { version, commit }) {
  const repo = context.repo;
  const [history, releases] = await Promise.all([
    github.paginate(github.rest.repos.listCommits, { ...repo, sha: commit, per_page: 100 }),
    github.paginate(github.rest.repos.listReleases, { ...repo, per_page: 100 })
  ]);
  if (!history.length || history[0].sha !== commit) throw new Error('Release history does not start at the tested commit.');
  const published = releases.filter(r => !r.draft && /^v\d+\.\d+\.\d+$/.test(r.tag_name));
  const candidates = published.filter(r => !r.prerelease && compareVersions(r.tag_name, version) < 0)
    .sort((a, b) => compareVersions(b.tag_name, a.tag_name));
  let boundary = history.length, rebased = false;
  for (const release of candidates) {
    const { data: target } = await github.rest.repos.getCommit({ ...repo, ref: `refs/tags/${release.tag_name}` });
    // Rebuilt history retains each published source tree even though its commit IDs changed.
    const index = history.findIndex(c => c.sha === target.sha ||
      (target.commit?.tree?.sha && c.commit?.tree?.sha === target.commit.tree.sha));
    if (index >= 0) { boundary = index; rebased = history[index].sha !== target.sha; break; }
  }
  const commits = history.slice(0, boundary).reverse();
  const publicTags = new Set(published.map(r => r.tag_name));
  const skippedVersions = [...new Set(commits.flatMap(c => {
    const match = /^chore\(release\): v?(\d+\.\d+\.\d+)\s*$/.exec(c.commit.message.split(/\r?\n/, 1)[0]);
    return match && compareVersions(match[1], version) < 0 && !publicTags.has(`v${match[1]}`) ? [match[1]] : [];
  }))].sort(compareVersions);
  return { commits, skippedVersions, rebased };
}

function formatReleaseNotes(notes, { commits, repo, skippedVersions = [], rebased = false }) {
  const groups = new Map();
  const seen = new Set();
  const escape = text => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/[\\`*_\[\]!|]/g, '\\$&');
  for (const commit of commits) {
    if (!/^[a-f0-9]{40}$/.test(commit.sha) || seen.has(commit.sha)) throw new Error('Invalid or duplicate release commit.');
    seen.add(commit.sha);
    const title = commit.commit.message.split(/\r?\n/, 1)[0];
    const category = /^\w+\(security\):/.test(title) ? 'Security' : /^\w+\(deps\):/.test(title) ? 'Dependencies' :
      /^feat\(/.test(title) ? 'Features' : /^fix\(/.test(title) ? 'Fixes' : /^test\(/.test(title) ? 'Tests' :
      /^(?:build|ci)\(/.test(title) ? 'Build and CI' : /^(?:perf|refactor)\(/.test(title) ? 'Implementation' : 'Documentation and maintenance';
    if (!groups.has(category)) groups.set(category, []);
    const url = `https://github.com/${encodeURIComponent(repo.owner)}/${encodeURIComponent(repo.repo)}/commit/${commit.sha}`;
    groups.get(category).push(`- [${commit.sha.slice(0, 7)}](${url}) ${escape(title)}`);
  }
  let body = notes.trim();
  if (skippedVersions.length) {
    const versions = skippedVersions.map(v => `v${v}`).join(', ');
    body = `> **Version note:** ${versions} ${skippedVersions.length === 1 ? 'was an internal development version and was' : 'were internal development versions and were'} not publicly released. ${skippedVersions.length === 1 ? 'Its' : 'Their'} changes are included in this release.\n\n${body}`;
  }
  if (commits.length) {
    const sections = [...groups].map(([name, lines]) => `### ${name}\n\n${lines.join('\n')}`).join('\n\n');
    const historyNote = rebased ? 'Commit links follow the rebuilt main history. Published tags and downloads retain their original targets.\n\n' : '';
    body += `\n\n<details>\n<summary>All ${commits.length} ${commits.length === 1 ? 'commit' : 'commits'}</summary>\n\n${historyNote}${sections}\n\n</details>`;
  }
  if (Buffer.byteLength(body) > 120000) throw new Error('Release notes exceed the publishing size limit.');
  return body + '\n';
}

// The module export is omitted from the embedded workflow script.
module.exports = { verifyReleaseSource, publishRelease, collectReleaseChanges, formatReleaseNotes };
