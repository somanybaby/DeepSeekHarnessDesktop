import { createRequire } from 'node:module';
import { resolve, join } from 'node:path';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';
const release = resolve(process.argv[2]);
const require = createRequire(join(release, 'package.json'));
const sharp = require('sharp');
const png = await sharp({ create: { width: 2, height: 2, channels: 4, background: '#000000' } }).png().toBuffer();
assert.equal((await sharp(png).metadata()).width, 2);
console.log('PASS Windows image native module');
const koffi = require('koffi');
assert.equal(koffi.load('kernel32.dll').func('uint32_t GetCurrentProcessId()')(), process.pid);
console.log('PASS Windows native FFI');
const { rgPath } = require('@vscode/ripgrep');
assert.match(execFileSync(rgPath, ['--version'], { encoding: 'utf8', windowsHide: true }), /ripgrep/);
console.log('PASS Bundled Windows search executable');
const pty = require('node-pty');
await new Promise((resolvePromise, reject) => {
  const terminal = pty.spawn(process.env.ComSpec, ['/d', '/s', '/c', 'echo DSH_PTY_OK'], {
    name: 'xterm-color', cols: 80, rows: 24, cwd: release, env: process.env
  });
  let output = '';
  const timeout = setTimeout(() => { terminal.kill(); reject(new Error('PTY timed out')); }, 10000);
  terminal.onData(data => { output += data; });
  terminal.onExit(({ exitCode }) => {
    clearTimeout(timeout);
    if (exitCode !== 0 || !output.includes('DSH_PTY_OK')) reject(new Error('Windows PTY smoke failed'));
    else resolvePromise();
  });
});
console.log('PASS Windows terminal native module');
// ConPTY's package can retain idle worker handles after onExit in short CLI
// tests. The application owns a kill-on-close job; this test owns its process.
process.exit(0);
