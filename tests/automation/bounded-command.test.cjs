const { test } = require('node:test');
const assert = require('node:assert/strict');
const { runBounded } = require('../../tools/ci/run-bounded.cjs');
const capture = () => {
  let output = '', errors = '';
  return { options: { stdout: { write: chunk => { output += chunk; } }, stderr: { write: chunk => { errors += chunk; } } },
    output: () => output, errors: () => errors };
};
test('The bounded command preserves successful output from both streams', async () => {
  const c = capture();
  assert.equal(await runBounded(process.execPath, ['-e', "console.log('synthetic output'); console.error('synthetic diagnostic')"], c.options), 0);
  assert.match(c.output(), /synthetic output/); assert.match(c.errors(), /synthetic diagnostic/);
});
test('The bounded command retains a failing exit code', async () => {
  assert.equal(await runBounded(process.execPath, ['-e', 'process.exitCode=7'], capture().options), 7);
});
test('A missing bounded command fails without printing its arguments', async () => {
  const c = capture();
  assert.equal(await runBounded('sdk-nonexistent-command-839472', ['synthetic-private-argument'], c.options), 1);
  assert(!c.errors().includes('synthetic-private-argument'));
});
test('A stalled real process is terminated and returns the timeout exit code', async () => {
  const c = capture();
  assert.equal(await runBounded(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { ...c.options, timeoutMs: 500, graceMs: 100 }), 124);
  assert.match(c.errors(), /deadline/);
});
test('An exited parent cannot leave verification hanging on inherited output pipes', async () => {
  const c = capture();
  const source = "const p=require('node:child_process').spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{detached:true,stdio:'inherit'}); console.log(p.pid); p.unref();";
  try {
    assert.equal(await runBounded(process.execPath, ['-e', source], { ...c.options, timeoutMs: 4000, pipeGraceMs: 200 }), 1);
    assert.match(c.errors(), /descendant kept its output pipes open/);
  } finally {
    const pid = Number(c.output().trim());
    if (Number.isSafeInteger(pid) && pid > 0) try { process.kill(pid, 'SIGKILL'); } catch (error) { if (error.code !== 'ESRCH') throw error; }
  }
});
test('Invalid commands or deadlines fail before launching a process', () => {
  for (const [command, args, options] of [['', [], {}], ['node', 'invalid', {}], ['node', [], { timeoutMs: 0 }],
    ['node', [], { timeoutMs: Infinity }], ['node', [], { graceMs: -1 }], ['node', [], { pipeGraceMs: 1000000 }]])
    assert.throws(() => runBounded(command, args, options), /Expected a command/);
});
