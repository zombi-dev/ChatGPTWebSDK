import { readFile, writeFile } from 'node:fs/promises';

for (const [workflow, module, invocation] of [
  ['release.yml', 'release.cjs', 'await publishRelease({ github, context, core });'],
  ['monitor.yml', 'monitor-issue.cjs', 'await publishMonitorIssue({ github, context, core });']
]) {
  const source = (await readFile(`tools/ci/${module}`, 'utf8')).split('// The module export')[0].trim();
  const filename = `.github/workflows/${workflow}`;
  let index = 0;
  const text = (await readFile(filename, 'utf8')).replace(/^          script: \|\n((?: {10,}.*\n|\n)+)/gm, () => {
    const verify = workflow === 'release.yml' && index++ === 0;
    const implementation = verify ? source.split('async function publishRelease')[0].trim() : source;
    const call = verify ? 'await verifyReleaseSource({ github, context });' : invocation;
    return '          script: |\n' + (implementation + '\n' + call).split('\n').map(line => line ? '            ' + line : '').join('\n') + '\n';
  });
  await writeFile(filename, text);
}
