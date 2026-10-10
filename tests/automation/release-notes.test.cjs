const { test } = require('node:test');
const assert = require('node:assert/strict');
const { collectReleaseChanges, formatReleaseNotes } = require('../../tools/ci/release.cjs');

const repo = { owner: 'owner', repo: 'sdk' };
const commit = (n, message, tree = n) => ({ sha: n.toString(16).padStart(40, '0'),
  commit: { message, tree: { sha: tree.toString(16).padStart(40, '0') } } });
const render = (commits, options = {}) => formatReleaseNotes('Release summary.\n\n- One useful change.', { commits, repo, ...options });
function historyFixture(history, releases = [], targets = {}, version = '1.5.0') {
  const calls = [];
  const repos = { listCommits() {}, listReleases() {}, getCommit: async ({ ref }) => {
    calls.push(ref); assert(targets[ref], 'Unexpected tag lookup'); return { data: targets[ref] };
  } };
  const github = { rest: { repos }, paginate: async (method, args) => {
    assert.equal(args.per_page, 100); assert.equal(args.owner, repo.owner); assert.equal(args.repo, repo.repo);
    if (method === repos.listCommits) { assert.equal(args.sha, history[0]?.sha); return history; }
    assert.equal(method, repos.listReleases); return releases;
  } };
  return { collect: () => collectReleaseChanges({ github, context: { repo } }, { version, commit: history[0]?.sha }), github, calls };
}

test('Every commit is linked once inside one section that is closed by default', () => {
  const commits = Array.from({ length: 239 }, (_, i) => commit(i + 1, `feat(sdk): feature ${i + 1}`));
  const body = render(commits), [visible, hidden] = body.split('<details>');
  assert.equal(visible.trim(), 'Release summary.\n\n- One useful change.');
  assert.equal((body.match(/<details>/g) || []).length, 1); assert(!body.includes('<details open'));
  assert(hidden.includes('<summary>All 239 commits</summary>'));
  assert.equal((hidden.match(/\/commit\/[a-f0-9]{40}/g) || []).length, 239);
  for (const c of commits) assert(hidden.includes(`https://github.com/owner/sdk/commit/${c.sha}`));
  assert(hidden.indexOf('feature 1\n') < hidden.indexOf('feature 239\n'));
});
test('Commit categories include security and dependencies alongside normal change types', () => {
  const categories = { Security: 'fix(security): validate provenance', Dependencies: 'build(deps): update actions',
    Features: 'feat(mcp): scope tools', Fixes: 'fix(browser): bound shutdown', Tests: 'test(ci): cover notes',
    'Build and CI': 'ci(linux): package browser', Implementation: 'perf(sse): avoid copies',
    'Documentation and maintenance': 'docs(release): expand notes' };
  const body = render(Object.values(categories).map((title, i) => commit(i + 1, title)));
  for (const [category, title] of Object.entries(categories)) assert(body.includes(`### ${category}\n\n`) && body.includes(title));
});
test('Commit subjects cannot open details, inject HTML or create extra Markdown links', () => {
  const body = render([commit(1, 'fix(sdk): </details><details open> [link](javascript:bad) & `x` *bold*')]);
  assert.equal((body.match(/<details>/g) || []).length, 1); assert.equal((body.match(/<\/details>/g) || []).length, 1);
  assert(body.includes('&lt;/details&gt;&lt;details open&gt;')); assert(body.includes('\\[link\\]'));
  assert(body.includes('&amp;')); assert(body.includes('\\`x\\`')); assert(body.includes('\\*bold\\*'));
});
test('Only the subject is published; bodies and co-author trailers are excluded', () => {
  const body = render([commit(1, 'feat(sdk): Unicode café 雪\r\n\r\nPrivate synthetic body\nCo-authored-by: Example')]);
  assert(body.includes('Unicode café 雪')); assert(!body.includes('Private synthetic')); assert(!body.includes('Co-authored-by'));
});
for (const [name, commits] of [
  ['malformed SHA', [{ ...commit(1, 'fix(sdk): change'), sha: '../injected' }]],
  ['duplicate SHA', [commit(1, 'fix(sdk): change'), commit(1, 'fix(sdk): repeated')]]
]) test(`Release notes reject ${name}`, () => assert.throws(() => render(commits), /Invalid or duplicate/));
test('Empty commit history does not create an empty disclosure', () => assert(!render([]).includes('<details>')));
test('A skipped internal version is explained before the visible release summary', () => {
  const body = render([commit(1, 'chore(release): 1.5.0')], { skippedVersions: ['1.4.0'] });
  assert(body.startsWith('> **Version note:** v1.4.0 was an internal development version and was not publicly released.'));
  assert(body.includes('Its changes are included in this release.\n\nRelease summary.'));
});
test('Multiple internal versions get one note with plural wording', () => {
  const body = render([], { skippedVersions: ['1.4.0', '1.4.1'] });
  assert(body.startsWith('> **Version note:** v1.4.0, v1.4.1 were internal development versions'));
  assert(body.includes('Their changes are included'));
});
test('Rewritten history is explained inside the collapsed appendix', () => {
  const body = render([commit(1, 'chore(release): 1.0.0')], { rebased: true });
  assert(body.indexOf('Commit links follow the rebuilt main history') > body.indexOf('<details>'));
  assert(body.includes('Published tags and downloads retain their original targets.'));
});
test('Oversized commit appendices fail before publication', () => {
  assert.throws(() => render([commit(1, 'feat(sdk): ' + 'x'.repeat(120000))]), /size limit/);
});
test('The preceding public tag bounds the linked history and preserves chronological order', async () => {
  const base = commit(1, 'chore(release): 1.3.0'), a = commit(2, 'feat(sdk): a'), b = commit(3, 'feat(sdk): b');
  const f = historyFixture([b, a, base], [{ tag_name: 'v1.3.0' }], { 'refs/tags/v1.3.0': base });
  const result = await f.collect(); assert.deepEqual(result.commits, [a, b]); assert.equal(result.rebased, false);
});
test('A rewritten checkpoint is matched by its original source tree', async () => {
  const old = commit(90, 'old release', 1), base = commit(1, 'chore(release): 1.3.0'), a = commit(2, 'feat(sdk): a');
  const f = historyFixture([a, base], [{ tag_name: 'v1.3.0' }], { 'refs/tags/v1.3.0': old });
  const result = await f.collect(); assert.deepEqual(result.commits, [a]); assert.equal(result.rebased, true);
});
test('Pagination retains more than one page of commit data without dropping changes', async () => {
  const all = Array.from({ length: 239 }, (_, i) => commit(i + 1, 'feat(sdk): addition')).reverse();
  const f = historyFixture(all); assert.equal((await f.collect()).commits.length, 239);
});
test('Previous versions are compared numerically and drafts/future releases are excluded', async () => {
  const base = commit(1, 'chore(release): 1.9.0'), a = commit(2, 'chore(release): 1.10.0');
  const f = historyFixture([a, base], [{ tag_name: 'v1.8.0' }, { tag_name: 'v1.9.0' },
    { tag_name: 'v1.9.1', draft: true }, { tag_name: 'v2.0.0' }], { 'refs/tags/v1.9.0': base }, '1.10.0');
  assert.deepEqual((await f.collect()).commits, [a]); assert.deepEqual(f.calls, ['refs/tags/v1.9.0']);
});
test('Only real unpublished checkpoints create the skipped-version note', async () => {
  const base = commit(1, 'chore(release): 1.3.0'), internal = commit(2, 'chore(release): 1.4.0'), a = commit(3, 'chore(release): 1.5.0');
  const releases = [{ tag_name: 'v1.3.0' }, { tag_name: 'v1.4.0', draft: true }];
  const f = historyFixture([a, internal, base], releases, { 'refs/tags/v1.3.0': base });
  assert.deepEqual((await f.collect()).skippedVersions, ['1.4.0']);
});
test('A numeric version gap alone does not invent an internal release', async () => {
  const base = commit(1, 'chore(release): 1.3.0'), a = commit(2, 'chore(release): 1.5.0');
  const f = historyFixture([a, base], [{ tag_name: 'v1.3.0' }], { 'refs/tags/v1.3.0': base });
  assert.deepEqual((await f.collect()).skippedVersions, []);
});
test('A published prerelease is not described as an unpublished internal version', async () => {
  const base = commit(1, 'chore(release): 1.3.0'), pre = commit(2, 'chore(release): 1.4.0'), a = commit(3, 'chore(release): 1.5.0');
  const f = historyFixture([a, pre, base], [{ tag_name: 'v1.3.0' }, { tag_name: 'v1.4.0', prerelease: true }], { 'refs/tags/v1.3.0': base });
  assert.deepEqual((await f.collect()).skippedVersions, []);
});
test('Missing source-tree metadata cannot accidentally match a rewritten release', async () => {
  const base = { sha: commit(1, '').sha, commit: { message: 'chore(release): 1.3.0' } };
  const a = commit(2, 'feat(sdk): change'), old = { sha: commit(90, '').sha, commit: {} };
  const f = historyFixture([a, base], [{ tag_name: 'v1.3.0' }], { 'refs/tags/v1.3.0': old });
  assert.equal((await f.collect()).commits.length, 2);
});
test('Unreachable newer tags fall back to the closest matching published checkpoint', async () => {
  const base = commit(1, 'chore(release): 1.2.0'), a = commit(2, 'chore(release): 1.5.0');
  const f = historyFixture([a, base], [{ tag_name: 'v1.2.0' }, { tag_name: 'v1.3.0' }],
    { 'refs/tags/v1.3.0': commit(90, 'unrelated'), 'refs/tags/v1.2.0': base });
  assert.deepEqual((await f.collect()).commits, [a]);
});
test('History lookup errors propagate and do not produce incomplete notes', async () => {
  const f = historyFixture([commit(1, 'chore(release): 1.5.0')]);
  f.github.paginate = async () => { throw new Error('API unavailable'); };
  await assert.rejects(f.collect, /API unavailable/);
});
test('History must begin at the exact tested release commit', async () => {
  const f = historyFixture([commit(1, 'chore(release): 1.5.0')]);
  const original = f.github.paginate;
  f.github.paginate = async (method, args) => method === f.github.rest.repos.listCommits ? [commit(2, 'wrong head')] : original(method, args);
  await assert.rejects(f.collect, /tested commit/);
});
