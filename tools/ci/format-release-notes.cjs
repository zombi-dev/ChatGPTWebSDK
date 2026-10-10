const { execFileSync } = require('node:child_process');
const fs = require('node:fs/promises');
const { collectReleaseChanges, formatReleaseNotes } = require('./release.cjs');

function createGithubReader(repo, request) {
  const root = `repos/${repo.owner}/${repo.repo}`;
  const repos = {
    listCommits: args => request(`${root}/commits?sha=${encodeURIComponent(args.sha)}&per_page=${args.per_page}&page=${args.page}`),
    listReleases: args => request(`${root}/releases?per_page=${args.per_page}&page=${args.page}`),
    getCommit: async args => ({ data: await request(`${root}/commits/${encodeURIComponent(args.ref)}`) })
  };
  return { rest: { repos }, paginate: async (method, args) => {
    const result = [];
    for (let page = 1; ; page++) {
      const data = await method({ ...args, page });
      if (!Array.isArray(data)) throw new Error('Expected a GitHub API page.');
      result.push(...data);
      if (data.length < args.per_page) return result;
    }
  } };
}

async function main(args, request = route => JSON.parse(execFileSync('gh', ['api', route], { encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 }))) {
  const [repository, commit, version, source, output] = args;
  if (args.length !== 5 || !/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository) ||
      !/^[a-f0-9]{40}$/.test(commit) || !/^\d+\.\d+\.\d+$/.test(version))
    throw new Error('Usage: format-release-notes.cjs owner/repo commit version notes-file output-file');
  const [owner, name] = repository.split('/'), repo = { owner, repo: name };
  const github = createGithubReader(repo, request);
  const changes = await collectReleaseChanges({ github, context: { repo } }, { version, commit });
  await fs.writeFile(output, formatReleaseNotes(await fs.readFile(source, 'utf8'), { ...changes, repo }));
}

if (require.main === module) main(process.argv.slice(2)).catch(error => { console.error(error.message); process.exitCode = 1; });
module.exports = { createGithubReader, main };
