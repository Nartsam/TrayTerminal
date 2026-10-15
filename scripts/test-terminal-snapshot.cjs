const assert = require('node:assert/strict');
const path = require('node:path');
const assets = path.join(__dirname, '..', 'src', 'TrayTerminal.App', 'Assets');
const { Terminal } = require(path.join(assets, 'xterm', 'xterm.js'));
const { SerializeAddon } = require(path.join(assets, 'xterm', 'addon-serialize.js'));
const snapshot = require(path.join(assets, 'terminal-snapshot.js'));

function create(scrollback = 256) {
  const terminal = new Terminal({ cols: 20, rows: 5, scrollback, convertEol: true, cursorBlink: true });
  const addon = new SerializeAddon();
  terminal.loadAddon(addon);
  snapshot.prepare(terminal);
  return { terminal, addon };
}

function write(terminal, text) {
  return new Promise(resolve => terminal.write(
    typeof text === 'string' ? Buffer.from(text, 'utf8') : text,
    resolve));
}

function screen(terminal) {
  const buffer = terminal._core._bufferService.buffer;
  const lines = [];
  for (let row = 0; row < terminal.rows; row++) {
    const line = buffer.lines.get(buffer.ybase + row);
    const cells = [];
    for (let col = 0; col < terminal.cols; col++) {
      const cell = line.loadCell(col, terminal._core._inputHandler._workCell);
      cells.push([cell.getChars(), cell.getWidth(), cell.fg, cell.bg, cell.extended._ext]);
    }
    lines.push({ wrapped: line.isWrapped, cells });
  }
  return lines;
}

async function roundTrip(name, setup, continuations) {
  const source = create();
  const replicas = [create(), create(5000)];
  try {
    await write(source.terminal, setup);
    const saved = snapshot.encode(source.terminal, source.addon);
    for (const replica of replicas) {
      const decoded = snapshot.decode(saved);
      await write(replica.terminal, decoded.content);
      snapshot.applyState(replica.terminal, decoded.state);
      assert.deepEqual(screen(replica.terminal), screen(source.terminal), `${name}: initial screen`);
    }
    for (const continuation of continuations) {
      if (typeof continuation === 'string') {
        await write(source.terminal, continuation);
        for (const replica of replicas) await write(replica.terminal, continuation);
      } else {
        source.terminal.resize(...continuation);
        for (const replica of replicas) replica.terminal.resize(...continuation);
      }
      for (const replica of replicas) {
        assert.deepEqual(screen(replica.terminal), screen(source.terminal), `${name}: continued screen`);
        assert.deepEqual(
          snapshot.decode(snapshot.encode(replica.terminal, replica.addon)).state,
          snapshot.decode(snapshot.encode(source.terminal, source.addon)).state,
          `${name}: continued parser state`);
      }
    }
    console.log(`PASS snapshot round-trip: ${name}`);
  } finally {
    source.terminal.dispose();
    for (const replica of replicas) replica.terminal.dispose();
  }
}

async function main() {
  const alternate = create();
  try {
    await write(alternate.terminal, '\x1b[?1049h\x1b[3;1H\x1b7\x1b[5;1H\n\x1b8X');
    assert.equal(alternate.terminal.buffer.active.getLine(2).translateToString(true), 'X',
      'Alternate-screen scrolling moved the saved screen-relative cursor');
  } finally {
    alternate.terminal.dispose();
  }
  console.log('PASS alternate-screen scrolling preserves the saved cursor row');

  await roundTrip('scroll region', '\x1b[2;5r\x1b[5;1H', ['\nA\nB\nC']);
  await roundTrip('origin and scroll region', '\x1b[2;4r\x1b[?6h\x1b[2;4H', ['X\nY', '\x1b[1;1HZ']);
  await roundTrip('saved cursor and attributes', '\x1b[31m\x1b[2;3H\x1b[s\x1b[0m\x1b[4;5H', ['\x1b[uX']);
  await roundTrip('G0 and saved charset', '\x1b(0\x1b7\x1b(B', ['q\x1b8q']);
  await roundTrip('G1 shift state', '\x1b)0\x0e', ['q\x0fq\x0eq']);
  await roundTrip('tab stops', '\x1b[3g\x1b[1;4H\x1bH', ['\r\tX', [30, 7], '\r\tY']);
  await roundTrip('alternate and normal buffers', 'normal\x1b[2;3H\x1b7\x1b[?1049halt\x1b[2;4r\x1b[4;3H',
    ['\nX', '\x1b[?1049lZ', '\x1b8Q']);
  await roundTrip('pending wrap and combining mark', '12345678901234567890', ['\u0301X', [30, 7], '中']);
  await roundTrip('mouse encoding and cursor mode', '\x1b[?1003h\x1b[?1006h\x1b[?25l\x1b[5 q', ['X']);
  await roundTrip('bounded history and saved cursor', 'line\r\n'.repeat(300) + '\x1b[2;3H\x1b7',
    ['tail\r\n'.repeat(20), '\x1b8X']);

  // Every split of a valid escape must retain the tail until the real parser is
  // finished; replaying that tail must produce the same result as one write.
  const sequences = ['\x1b(0', '\x1b[31m', '\x1b[\x1b[31m', '\x1b]0;title\x1b\\',
    '\x1bP$qm\x1b\\', '\u009b31m', '中'];
  for (const sequence of sequences) {
    const bytes = Buffer.from(sequence, 'utf8');
    for (let split = 1; split < bytes.length; split++) {
      const source = create();
      const restored = create();
      try {
        const initial = snapshot.decode(snapshot.encode(source.terminal, source.addon));
        await write(source.terminal, bytes.subarray(0, split));
        assert.equal(snapshot.isCheckpointSafe(source.terminal), false, `unsafe split ${JSON.stringify(sequence)} at ${split}`);
        assert.throws(() => snapshot.encode(source.terminal, source.addon), /unsafeCheckpoint/);
        await write(restored.terminal, initial.content);
        snapshot.applyState(restored.terminal, initial.state);
        await write(restored.terminal, bytes.subarray(0, split));
        await write(source.terminal, bytes.subarray(split));
        await write(restored.terminal, bytes.subarray(split));
        await write(source.terminal, 'q');
        await write(restored.terminal, 'q');
        assert.equal(snapshot.isCheckpointSafe(source.terminal), true);
        assert.deepEqual(screen(restored.terminal), screen(source.terminal));
      } finally {
        source.terminal.dispose();
        restored.terminal.dispose();
      }
    }
  }
  for (const sequence of ['\x1b[31\x18', '\x1b[31\x1a', '\x1b(\x1b[0m']) {
    const source = create();
    await write(source.terminal, sequence);
    assert.equal(snapshot.isCheckpointSafe(source.terminal), true, 'cancelled sequence prevented checkpoints');
    source.terminal.dispose();
  }
  console.log('PASS actual parser checkpoint boundaries, cancellation, and replay');
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
