const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assets = path.join(__dirname, '..', 'src', 'TrayTerminal.App', 'Assets');

function page(file) {
  const elements = new Map();
  const listeners = new Map();
  const sockets = [];
  const terminals = [];
  const processors = [];
  const messages = [];
  const timers = new Set();
  const element = id => {
    if (!elements.has(id)) elements.set(id, {
      style: {}, textContent: '', hidden: false, clientWidth: 180, clientHeight: 95,
      focus() {}, addEventListener() {}, contains() { return true; }
    });
    return elements.get(id);
  };
  class Socket {
    static OPEN = 1;
    readyState = 1;
    sent = [];
    constructor() { sockets.push(this); }
    send(text) { this.sent.push(JSON.parse(text)); }
    close(code) { this.readyState = 3; this.onclose?.({ code }); }
  }
  const context = {
    TextEncoder, TextDecoder, Uint8Array, ArrayBuffer, DataView, AbortController,
    URLSearchParams, atob, btoa, console, WebSocket: Socket,
    navigator: { userAgent: 'node', platform: 'Win32', language: 'en-US' },
    location: { protocol: 'http:', host: 'localhost:8848', pathname: '/test', search: '' },
    document: {
      body: element('body'), getElementById: element,
      addEventListener(name, callback) { listeners.set(name, callback); },
      getSelection() { return null; }
    },
    addEventListener() {},
    getSelection() { return null; },
    requestAnimationFrame(callback) { callback(); },
    setTimeout(callback, delay) {
      const timer = setTimeout(callback, delay);
      timers.add(timer);
      return timer;
    },
    clearTimeout(timer) { clearTimeout(timer); timers.delete(timer); },
    chrome: { webview: { postMessage(message) { messages.push(message); } } }
  };
  context.window = context;
  context.self = context;
  context.globalThis = context;
  vm.createContext(context);
  for (const script of ['xterm/xterm.js', 'xterm/addon-serialize.js',
    'terminal-snapshot.js', 'remote-protocol.js', 'terminal-input-policy.js']) {
    vm.runInContext(fs.readFileSync(path.join(assets, script), 'utf8'), context);
  }
  const Xterm = context.Terminal;
  context.Terminal = class extends Xterm {
    constructor(options) { super(options); terminals.push(this); }
    // Keep the real parser/write queue; DOM rendering and network transport are
    // the only substitutes in this page-level protocol regression suite.
    open() {}
    focus() {}
  };
  const serialProcessor = context.TrayTerminalRemoteProtocol.createSerialProcessor;
  context.TrayTerminalRemoteProtocol.createSerialProcessor = (...args) => {
    const processor = serialProcessor(...args);
    processors.push(processor);
    return processor;
  };
  const html = fs.readFileSync(path.join(assets, file), 'utf8');
  vm.runInContext(html.slice(html.lastIndexOf('<script>') + 8, html.lastIndexOf('</script>')), context);
  listeners.get('DOMContentLoaded')();
  return {
    context, sockets, terminals, messages, elements,
    async feed(message) {
      sockets.at(-1).onmessage({ data: JSON.stringify({ version: 4, ...message }) });
      await processors.at(-1).drain();
    },
    dispose() {
      for (const timer of timers) clearTimeout(timer);
      for (const terminal of terminals) terminal.dispose();
    }
  };
}

async function waitFor(condition) {
  for (let i = 0; i < 100; i++) {
    if (condition()) return;
    await new Promise(resolve => setTimeout(resolve, 5));
  }
  assert.fail('Page callback did not complete');
}

async function main() {
  const remote = page('remote-terminal.html');
  const local = page('terminal.html');
  const source = new remote.context.Terminal({ cols: 20, rows: 5, scrollback: 256, convertEol: true });
  const addon = new remote.context.SerializeAddon.SerializeAddon();
  source.loadAddon(addon);
  remote.context.TrayTerminalSnapshot.prepare(source);
  try {
    await new Promise(resolve => source.write('\x1b[2;4r\x1b(0\x1b[4;3H\x1b7', resolve));
    const bytes = remote.context.TrayTerminalSnapshot.encode(source, addon);
    const payload = Buffer.from(bytes).toString('base64');
    await remote.feed({ type: 'syncStart', snapshotSequence: '1', latestSequence: '1', cols: 20, rows: 5, canResize: false });
    await remote.feed({ type: 'snapshot', sequence: '1', data: payload });
    await remote.feed({ type: 'syncComplete', sequence: '1' });
    assert.equal(local.context.trayTerminal.restoreState('1', payload, '1', [], 20, 5, 'restore'), true);
    await waitFor(() => local.messages.some(message => message.type === 'stateRestored'));

    const continuation = Buffer.from('\nq\x1b8q');
    await new Promise(resolve => source.write(continuation, resolve));
    await remote.feed({ type: 'output', sequence: '2', data: continuation.toString('base64') });
    local.context.trayTerminal.writeSequencedBatch([
      { type: 'output', sequence: '2', data: continuation.toString('base64') }
    ], 'continued');
    await waitFor(() => local.messages.some(message => message.type === 'batchApplied'));
    const actualRemote = remote.terminals.at(-1);
    const actualLocal = local.terminals.at(-1);
    for (const replica of [actualRemote, actualLocal]) {
      assert.equal(replica.buffer.active.getLine(3).translateToString(), source.buffer.active.getLine(3).translateToString());
      assert.equal(replica._core._bufferService.buffer.scrollTop, 1);
      assert.equal(replica._core._bufferService.buffer.scrollBottom, 3);
      assert.equal(replica._core._charsetService.charset.q, '─');
    }

    actualRemote._core.coreService.triggerDataEvent('x'.repeat(65537), true);
    actualRemote._core.coreService.triggerDataEvent('next\r', true);
    const inputs = remote.sockets.at(-1).sent.filter(message => message.type === 'input');
    assert.equal(inputs.length, 2);
    await remote.feed({ type: 'inputAck', inputId: inputs[0].inputId, accepted: false });
    const status = remote.elements.get('status-bar');
    const warning = status.textContent;
    assert.match(warning, /可能已有内容写入/);
    await remote.feed({ type: 'inputAck', inputId: inputs[1].inputId, accepted: true });
    assert.equal(status.textContent, warning, 'another ACK replaced the uncertain-input warning');
    assert.notEqual(status.style.display, 'none');
    await remote.feed({ type: 'output', sequence: '3', data: Buffer.from('background log').toString('base64') });
    assert.equal(status.textContent, warning);
    assert.notEqual(status.style.display, 'none', 'background output hid the warning');

    await remote.feed({ type: 'output', sequence: '4', data: Buffer.from('\x1b[6n').toString('base64') });
    const report = remote.sockets.at(-1).sent.filter(message => message.type === 'input').at(-1);
    assert.match(report.data, /^\x1b\[\d+;\d+R$/, 'cursor query did not generate a protocol response');
    assert.equal(status.textContent, warning, 'automatic protocol response replaced the warning');
    assert.notEqual(status.style.display, 'none');
    await remote.feed({ type: 'inputAck', inputId: report.inputId, accepted: true });
    assert.equal(status.textContent, warning);
    assert.notEqual(status.style.display, 'none', 'automatic response ACK hid the warning');

    actualRemote._core.coreService.triggerDataEvent('intentional new input', true);
    const nextInput = remote.sockets.at(-1).sent.filter(message => message.type === 'input').at(-1);
    await remote.feed({ type: 'inputAck', inputId: nextInput.inputId, accepted: true });
    assert.equal(status.style.display, 'none', 'new acknowledged input could not clear the warning');

    await remote.feed({ version: 3, type: 'syncStart' });
    assert.equal(remote.sockets.at(-1).readyState, 3);
    assert.match(status.textContent, /刷新页面/);
    console.log('PASS local/remote snapshot restoration, partial-input warnings, and protocol version rejection');
  } finally {
    local.dispose();
    remote.dispose();
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
