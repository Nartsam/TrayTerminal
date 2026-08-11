const assert = require('node:assert/strict');
const path = require('node:path');
const policy = require(path.join(
  __dirname,
  '..',
  'src',
  'TrayTerminal.App',
  'Assets',
  'terminal-input-policy.js'));

const normal = {
  hasSelection: false,
  isTrusted: true,
  focused: true,
  isComposing: false,
  repeat: false,
  held: false
};

assert.equal(policy.classifyCtrlC(normal), 'interrupt');
assert.equal(policy.classifyCtrlC({ ...normal, hasSelection: true }), 'copy');
assert.equal(policy.classifyCtrlC({ ...normal, isTrusted: false }), 'consume');
assert.equal(policy.classifyCtrlC({ ...normal, focused: false }), 'consume');
assert.equal(policy.classifyCtrlC({ ...normal, isComposing: true }), 'consume');
assert.equal(policy.classifyCtrlC({ ...normal, repeat: true }), 'consume');
assert.equal(policy.classifyCtrlC({ ...normal, held: true }), 'consume');
console.log('PASS Ctrl+C policy is copy-safe and suppresses false interrupts');
