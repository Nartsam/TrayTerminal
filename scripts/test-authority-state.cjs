const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const assets = path.join(
  __dirname,
  '..',
  'src',
  'TrayTerminal.App',
  'Assets');

async function main() {
  let messageHandler;
  const waiters = new Map();
  const context = {
    TextEncoder,
    TextDecoder,
    Uint8Array,
    ArrayBuffer,
    BigInt,
    Promise,
    Map,
    Math,
    Number,
    String,
    Error,
    RegExp,
    setTimeout,
    clearTimeout,
    atob,
    btoa,
    console,
    addEventListener() {},
    navigator: {
      userAgent: 'node',
      platform: 'Win32',
      language: 'en-US'
    },
    trayAuthorityMaxStateBytes: 64 * 1024
  };
  context.window = context;
  context.self = context;
  context.globalThis = context;
  context.chrome = {
    webview: {
      addEventListener(type, handler) {
        if (type === 'message') messageHandler = handler;
      },
      postMessage(message) {
        if (message.type === 'result') {
          const resolve = waiters.get(message.operationId);
          if (resolve) {
            waiters.delete(message.operationId);
            resolve(message);
          }
        }
      }
    }
  };
  vm.createContext(context);
  vm.runInContext(
    fs.readFileSync(path.join(assets, 'xterm', 'xterm.js'), 'utf8'),
    context);
  vm.runInContext(
    fs.readFileSync(path.join(assets, 'xterm', 'addon-serialize.js'), 'utf8'),
    context);
  // This suite runs the same un-opened xterm InputHandler path as the hidden
  // authority. The separate App probe covers actual WebView2 message delivery.
  const html = fs.readFileSync(
    path.join(assets, 'authority-terminal.html'),
    'utf8');
  const start = html.lastIndexOf('<script>') + '<script>'.length;
  const end = html.lastIndexOf('</script>');
  vm.runInContext(html.slice(start, end), context);
  assert.equal(typeof messageHandler, 'function', 'authority message handler was not installed');

  let operationId = 0;
  function invoke(command) {
    const id = ++operationId;
    const result = new Promise(resolve => waiters.set(id, resolve));
    messageHandler({
      data: {
        version: 3,
        operationId: id,
        ...command
      }
    });
    return result;
  }

  for (const sessionId of ['a', 'b']) {
    const created = await invoke({
      type: 'create',
      sessionId,
      sequence: '0',
      cols: 80,
      rows: 24
    });
    assert.equal(created.ok, true);
    assert.equal(created.sequence, '0');
  }

  const createdUnsafe = await invoke({
    type: 'create',
    sessionId: 'unsafe',
    sequence: '0',
    cols: 80,
    rows: 24
  });
  assert.equal(createdUnsafe.ok, true);
  const partialOsc = await invoke({
    type: 'output',
    sessionId: 'unsafe',
    sequence: '1',
    data: Buffer.from('\x1b]0;partial', 'binary').toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(partialOsc.ok, true);
  assert.equal(partialOsc.checkpointSafe, false, 'partial OSC must retain replay tail');
  const completedOsc = await invoke({
    type: 'output',
    sessionId: 'unsafe',
    sequence: '2',
    data: Buffer.from('\x07', 'binary').toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(completedOsc.ok, true);
  assert.equal(completedOsc.checkpointSafe, true, 'completed OSC must permit a checkpoint');

  const utf8Bytes = Buffer.from('中', 'utf8');
  const partialUtf8 = await invoke({
    type: 'output',
    sessionId: 'unsafe',
    sequence: '3',
    data: utf8Bytes.subarray(0, 1).toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(partialUtf8.checkpointSafe, false, 'partial UTF-8 must retain replay tail');
  const completedUtf8 = await invoke({
    type: 'output',
    sessionId: 'unsafe',
    sequence: '4',
    data: utf8Bytes.subarray(1).toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(completedUtf8.checkpointSafe, true, 'complete UTF-8 must permit a checkpoint');

  const createdOrdinary = await invoke({
    type: 'create',
    sessionId: 'ordinary',
    sequence: '0',
    cols: 80,
    rows: 24
  });
  assert.equal(createdOrdinary.ok, true);
  let ordinaryCheckpoints = 0;
  for (let index = 1; index <= 100; index++) {
    const result = await invoke({
      type: 'output',
      sessionId: 'ordinary',
      sequence: String(index),
      data: Buffer.from('normal output\r\n').toString('base64'),
      cols: 80,
      rows: 24,
      checkpoint: false
    });
    assert.equal(result.ok, true, 'sustained ordinary output failed');
    if (result.snapshot) ordinaryCheckpoints++;
  }
  assert.equal(
    ordinaryCheckpoints,
    0,
    'authority performed a full serialized checkpoint for ordinary chunks');
  const concurrentOne = invoke({
    type: 'output',
    sessionId: 'ordinary',
    sequence: '101',
    data: Buffer.from('one').toString('base64'),
    cols: 80,
    rows: 24
  });
  const concurrentTwo = invoke({
    type: 'output',
    sessionId: 'ordinary',
    sequence: '102',
    data: Buffer.from('two').toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal((await concurrentOne).ok, true);
  assert.equal((await concurrentTwo).ok, true, 'per-session JS operations lost ordering');

  const ordinarySnapshot = await invoke({
    type: 'output',
    sessionId: 'ordinary',
    sequence: '103',
    data: Buffer.from('checkpoint').toString('base64'),
    cols: 80,
    rows: 24,
    checkpoint: true
  });
  assert.equal(ordinarySnapshot.ok, true);
  assert.ok(ordinarySnapshot.snapshot, 'same-id restore test needs a checkpoint');
  const restoredOrdinary = await invoke({
    type: 'restore',
    sessionId: 'ordinary',
    sequence: '103',
    snapshot: ordinarySnapshot.snapshot,
    cols: 80,
    rows: 24
  });
  assert.equal(restoredOrdinary.ok, true, 'same-id restore failed');
  assert.equal((await invoke({
    type: 'output',
    sessionId: 'ordinary',
    sequence: '104',
    data: Buffer.from('after restore').toString('base64'),
    cols: 80,
    rows: 24
  })).ok, true, 'replacement engine did not accept the continuous tail');

  const createdC1 = await invoke({
    type: 'create',
    sessionId: 'c1',
    sequence: '0',
    cols: 80,
    rows: 24
  });
  assert.equal(createdC1.ok, true);
  const c1Osc = await invoke({
    type: 'output',
    sessionId: 'c1',
    sequence: '1',
    data: Buffer.from('\u009d0;partial', 'utf8').toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(c1Osc.checkpointSafe, false, 'UTF-8 C1 OSC must retain replay tail');
  const c1Terminated = await invoke({
    type: 'output',
    sessionId: 'c1',
    sequence: '2',
    data: Buffer.from('\u009c', 'utf8').toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(c1Terminated.checkpointSafe, true, 'UTF-8 C1 ST must close OSC');

  let sequence = 0n;
  for (const byte of Buffer.from('fragmented output\r\n', 'utf8')) {
    sequence++;
    const result = await invoke({
      type: 'output',
      sessionId: 'a',
      sequence: sequence.toString(),
      data: Buffer.from([byte]).toString('base64'),
      cols: 80,
      rows: 24
    });
    assert.equal(result.ok, true, 'fragmented output failed');
  }

  sequence++;
  assert.equal((await invoke({
    type: 'resize',
    sessionId: 'a',
    sequence: sequence.toString(),
    cols: 100,
    rows: 30
  })).ok, true, 'sequenced resize failed');

  // A fragmented OSC that exceeds the lowered test budget must fail only A.
  let failed = false;
  const oscChunk = Buffer.alloc(4096, 0x61);
  const fragments = [Buffer.from('\x1b]0;', 'binary')];
  for (let index = 0; index < 20; index++) fragments.push(oscChunk);
  for (const fragment of fragments) {
    sequence++;
    const result = await invoke({
      type: 'output',
      sessionId: 'a',
      sequence: sequence.toString(),
      data: fragment.toString('base64'),
      cols: 100,
      rows: 30
    });
    if (!result.ok) {
      assert.equal(result.reason, 'stateTooLarge');
      failed = true;
      break;
    }
  }
  assert.equal(failed, true, 'oversized fragmented OSC was not rejected');

  const isolated = await invoke({
    type: 'output',
    sessionId: 'b',
    sequence: '1',
    data: Buffer.from('isolated\r\n').toString('base64'),
    cols: 80,
    rows: 24
  });
  assert.equal(isolated.ok, true, 'one failed engine damaged another session');

  let bSequence = 1n;
  for (let index = 0; index < 100; index++) {
    bSequence++;
    const resized = await invoke({
      type: 'resize',
      sessionId: 'b',
      sequence: bSequence.toString(),
      cols: 80 + (index % 20),
      rows: 24 + (index % 10)
    });
    assert.equal(resized.ok, true, 'resize storm broke authority sequencing');
  }

  const createdD = await invoke({
    type: 'create',
    sessionId: 'd',
    sequence: '0',
    cols: 80,
    rows: 24
  });
  assert.equal(createdD.ok, true);
  let dSequence = 0n;
  failed = false;
  // BEL ends OSC but not DCS. Treating it as a universal terminator would let
  // a fragmented DCS evade the incomplete-control-string budget.
  const dcsFragments = [
    Buffer.from('\x1bPq', 'binary'),
    Buffer.from('\x07', 'binary')
  ];
  for (let index = 0; index < 20; index++) dcsFragments.push(oscChunk);
  for (const fragment of dcsFragments) {
    dSequence++;
    const result = await invoke({
      type: 'output',
      sessionId: 'd',
      sequence: dSequence.toString(),
      data: fragment.toString('base64'),
      cols: 80,
      rows: 24
    });
    if (!result.ok) {
      assert.equal(result.reason, 'stateTooLarge');
      failed = true;
      break;
    }
  }
  assert.equal(failed, true, 'oversized fragmented DCS was not rejected');

  // Combining-cell growth is visible to serialization and hits the same cap.
  const createdC = await invoke({
    type: 'create',
    sessionId: 'c',
    sequence: '0',
    cols: 80,
    rows: 24
  });
  assert.equal(createdC.ok, true);
  let cSequence = 0n;
  failed = false;
  for (let index = 0; index < 20; index++) {
    cSequence++;
    const combining = 'x' + '\u0301'.repeat(4096);
    const result = await invoke({
      type: 'output',
      sessionId: 'c',
      sequence: cSequence.toString(),
      data: Buffer.from(combining).toString('base64'),
      cols: 80,
      rows: 24
    });
    if (!result.ok) {
      assert.equal(result.reason, 'stateTooLarge');
      failed = true;
      break;
    }
  }
  assert.equal(failed, true, 'combining-cell growth was not bounded');

  assert.equal((await invoke({
    type: 'create', sessionId: 'amp', sequence: '0', cols: 80, rows: 24
  })).ok, true);
  assert.equal((await invoke({
    type: 'output',
    sessionId: 'amp',
    sequence: '1',
    data: Buffer.from('x\u0301\x1b[500b').toString('base64'),
    cols: 80,
    rows: 24
  })).reason, 'stateAmplificationUnsupported', 'REP amplification reached xterm');
  await invoke({ type: 'dispose', sessionId: 'amp', sequence: '1' });

  // Repeated canceled/removed-session equivalents must not retain JS engines.
  for (let index = 0; index < 100; index++) {
    assert.equal((await invoke({
      type: 'create', sessionId: 'reused', sequence: '0', cols: 80, rows: 24
    })).ok, true, 'create/dispose churn leaked an authority engine');
    assert.equal((await invoke({
      type: 'dispose', sessionId: 'reused', sequence: '0'
    })).ok, true);
  }
  for (let index = 0; index < 28; index++) {
    assert.equal((await invoke({
      type: 'create',
      sessionId: `capacity-${index}`,
      sequence: '0',
      cols: 80,
      rows: 24
    })).ok, true, 'authority rejected a session below its hard cap');
  }
  const overflow = await invoke({
    type: 'create',
    sessionId: 'capacity-overflow',
    sequence: '0',
    cols: 80,
    rows: 24
  });
  assert.equal(overflow.ok, false, 'authority exceeded its 32-engine hard cap');
  assert.equal(overflow.reason, 'sessionLimit');
  console.log('PASS authority state is sequenced, bounded, and session-isolated');
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
