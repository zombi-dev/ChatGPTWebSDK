const { test } = require('node:test');
const assert = require('node:assert/strict');
const { validateTitle } = require('../../tools/ci/check-commit.cjs');

for (const title of ['feat(models): add GPT-6', 'fix(security): verify release provenance', 'build(deps): update test tooling',
  'ci(windows): support CRLF', 'chore(release): 1.5.0', 'refactor(mcp)!: simplify registrations'])
  test(`Categorized Conventional Commit is accepted: ${title}`, () => assert(validateTitle(title)));
for (const title of ['', 'update stuff', 'fix: missing category', 'security: protect assets', 'feat(models): ',
  'fix(Security): invalid scope', 'fix(ci): line one\nline two', 'fix(ci): trailing space ', 'fix(ci): ' + 'x'.repeat(100)])
  test('Uncategorized or malformed commit title is rejected: ' + JSON.stringify(title), () => assert(!validateTitle(title)));
