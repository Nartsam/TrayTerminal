const assert = require('node:assert/strict');
const path = require('node:path');
const protocol = require(path.join(
  __dirname,
  '..',
  'src',
  'TrayTerminal.App',
  'Assets',
  'remote-protocol.js'));

async function main() {
  const order = [];
  let completeWrite;
  const writeFinished = new Promise(resolve => {
    completeWrite = resolve;
  });
  const processor = protocol.createSerialProcessor(async message => {
    order.push(`start:${message}`);
    if (message === 'snapshot') {
      await writeFinished;
    }
    order.push(`end:${message}`);
  }, error => {
    throw error;
  });

  processor.push('snapshot');
  processor.push('size');
  processor.push('syncComplete');
  await Promise.resolve();
  await Promise.resolve();
  assert.deepEqual(
    order,
    ['start:snapshot'],
    'size/syncComplete advanced before the xterm write callback');

  completeWrite();
  await processor.drain();
  assert.deepEqual(order, [
    'start:snapshot',
    'end:snapshot',
    'start:size',
    'end:size',
    'start:syncComplete',
    'end:syncComplete'
  ]);
  const boundary = protocol.parseSyncStart('40', '42');
  assert.equal(boundary.snapshot, 40n);
  assert.equal(boundary.latest, 42n);
  assert.throws(
    () => protocol.parseSyncStart('43', '42'),
    /newer than advertised/,
    'syncStart accepted a snapshot beyond advertised latest');
  assert.equal(
    protocol.isSyncComplete('42', boundary.latest, 42n),
    true,
    'valid synchronization completion was rejected');
  assert.equal(
    protocol.isSyncComplete('41', boundary.latest, 41n),
    false,
    'syncComplete was accepted before advertised latest');
  assert.equal(
    protocol.shouldHideInputStatus('input', true),
    false,
    'ordinary output hid the unresolved-input result warning');
  assert.equal(
    protocol.shouldHideInputStatus('input', false),
    true,
    'ordinary acknowledged-input status was not cleared by output');
  console.log('PASS remote protocol waits for terminal write callbacks');
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
