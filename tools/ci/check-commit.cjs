const { execFileSync } = require('node:child_process');

function validateTitle(title) {
  return typeof title === 'string' && title.length <= 100 && title === title.trim() &&
    /^(feat|fix|perf|refactor|test|docs|build|ci|chore|style|revert)\([a-z0-9][a-z0-9-]*\)!?: \S[^\r\n]*$/.test(title);
}

function validateVersionMention(message, version) {
  return typeof message === 'string' && typeof version === 'string' && /^\d+\.\d+\.\d+$/.test(version) &&
    new RegExp('(?<![\\d.])v?' + version.replaceAll('.', '\\.') + '(?!\\d|\\.\\d)').test(message);
}

if (require.main === module) {
  const ref = process.argv[2] ?? 'HEAD';
  if (!/^(?:HEAD|[a-f0-9]{40})$/.test(ref)) throw new Error('Expected HEAD or a full commit SHA.');
  const message = execFileSync('git', ['show', '-s', '--format=%B', ref], { encoding: 'utf8' });
  if (!validateTitle(message.split(/\r?\n/, 1)[0])) throw new Error('Use a short Conventional Commit title: type(scope): summary.');
  const version = process.argv[3];
  if (version !== undefined && !validateVersionMention(message, version))
    throw new Error('Mention version ' + version + ' in the commit title or body.');
}

module.exports = { validateTitle, validateVersionMention };
