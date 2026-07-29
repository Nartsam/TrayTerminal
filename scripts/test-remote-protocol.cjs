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
  console.log('PASS remote protocol waits for terminal write callbacks');
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
