const { spawn } = require('node:child_process');

function runBounded(command, args, { timeoutMs = 150000, graceMs = 5000, pipeGraceMs = 5000,
  stdout = process.stdout, stderr = process.stderr } = {}) {
  if (typeof command !== 'string' || !command || !Array.isArray(args) ||
      ![timeoutMs, graceMs, pipeGraceMs].every(value => Number.isSafeInteger(value) && value > 0 && value <= 900000))
    throw new TypeError('Expected a command, argument array and positive bounded timeouts.');
  return new Promise(resolve => {
    const child = spawn(command, args, { shell: false, windowsHide: true, detached: process.platform !== 'win32', stdio: ['ignore', 'pipe', 'pipe'] });
    let done = false, timedOut = false, hardTimer, pipeTimer;
    const stop = force => {
      if (!child.pid) return;
      if (process.platform === 'win32') {
        const killer = spawn('taskkill', ['/PID', String(child.pid), '/T', ...(force ? ['/F'] : [])], { windowsHide: true, stdio: 'ignore' });
        killer.on('error', () => {}); killer.unref();
      } else {
        try { process.kill(-child.pid, force ? 'SIGKILL' : 'SIGTERM'); }
        catch (error) { if (error.code !== 'ESRCH') stderr.write('Could not terminate the owned command group.\n'); }
      }
    };
    const finish = code => {
      if (done) return; done = true;
      clearTimeout(timer); clearTimeout(hardTimer); clearTimeout(pipeTimer);
      child.stdout.destroy(); child.stderr.destroy(); child.unref(); resolve(code);
    };
    child.stdout.on('data', chunk => stdout.write(chunk)); child.stderr.on('data', chunk => stderr.write(chunk));
    child.once('error', () => { stderr.write('The bounded command could not start.\n'); finish(1); });
    child.once('exit', () => {
      if (timedOut) { stop(true); finish(124); return; }
      pipeTimer = setTimeout(() => {
        stderr.write('The command exited but a descendant kept its output pipes open.\n'); stop(true); finish(1);
      }, pipeGraceMs);
    });
    child.once('close', code => finish(timedOut ? 124 : code ?? 1));
    const timer = setTimeout(() => {
      timedOut = true; stderr.write('Native verification exceeded its ' + timeoutMs / 1000 + ' second deadline.\n'); stop(false);
      hardTimer = setTimeout(() => { stop(true); finish(124); }, graceMs);
    }, timeoutMs);
  });
}

if (require.main === module) {
  const [seconds, command, ...args] = process.argv.slice(2);
  try { runBounded(command, args, { timeoutMs: Number(seconds) * 1000 }).then(code => { process.exitCode = code; }); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { runBounded };
