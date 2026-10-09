const { execFileSync } = require('node:child_process');

function validateTitle(title) {
  return typeof title === 'string' && title.length <= 100 && title === title.trim() &&
    /^(feat|fix|perf|refactor|test|docs|build|ci|chore|style|revert)\([a-z0-9][a-z0-9-]*\)!?: \S[^\r\n]*$/.test(title);
}

if (require.main === module) {
  const ref = process.argv[2] ?? 'HEAD';
  if (!/^(?:HEAD|[a-f0-9]{40})$/.test(ref)) throw new Error('Expected HEAD or a full commit SHA.');
  const title = execFileSync('git', ['show', '-s', '--format=%s', ref], { encoding: 'utf8' }).trim();
  if (!validateTitle(title)) throw new Error('Use a short Conventional Commit title: type(scope): summary.');
}

module.exports = { validateTitle };
