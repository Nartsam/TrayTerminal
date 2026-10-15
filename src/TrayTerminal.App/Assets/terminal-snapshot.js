(function(root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.TrayTerminalSnapshot = factory();
  }
})(globalThis, function() {
  'use strict';

  const header = new Uint8Array([0, 84, 84, 83, 1]);
  const encoder = new TextEncoder();
  const decoder = new TextDecoder('utf-8', { fatal: true });
  const prepared = new WeakSet();

  function prepare(terminal) {
    if (prepared.has(terminal)) return;
    const service = inputHandler(terminal)._bufferService;
    const scroll = service.scroll;
    // xterm 5.5 adjusts savedY when resize trims history, but not when ordinary
    // scrolling recycles a full buffer. Correct that omission on every replica:
    // otherwise 256-line authority and 5000-line views restore different rows.
    service.scroll = function(...args) {
      const buffer = this.buffer;
      // The alternate buffer has no history: its saved cursor remains a screen
      // coordinate even when scrolling recycles its lines.
      const trimmed = buffer.hasScrollback && buffer.scrollTop === 0 && buffer.lines.isFull;
      scroll.apply(this, args);
      if (trimmed) buffer.savedY = Math.max(0, buffer.savedY - 1);
    };
    prepared.add(terminal);
  }

  function inputHandler(terminal) {
    const handler = terminal && terminal._core && terminal._core._inputHandler;
    if (!handler || typeof handler.parse !== 'function' || !handler._parser ||
        !Number.isInteger(handler._parser.currentState) ||
        !handler._utf8Decoder || !handler._utf8Decoder.interim ||
        !handler._bufferService || !handler._charsetService) {
      throw new Error('pinnedParserUnavailable');
    }
    return handler;
  }

  function isCheckpointSafe(terminal) {
    const handler = inputHandler(terminal);
    // Use the parser and decoder that actually consumed the bytes. In particular,
    // ESC intermediates, cancellation and CSI restarts must not be guessed by a
    // second parser with a smaller set of states.
    return handler._parser.currentState === 0 &&
      !handler._parseStack.paused &&
      handler._utf8Decoder.interim.every(value => value === 0) &&
      handler._stringDecoder._interim === 0;
  }

  function attributes(value) {
    return { fg: value.fg, bg: value.bg, extended: value.extended._ext };
  }

  function bufferState(buffer) {
    return {
      x: buffer.x,
      y: buffer.y,
      scrollTop: buffer.scrollTop,
      scrollBottom: buffer.scrollBottom,
      tabs: buffer.tabs,
      savedX: buffer.savedX,
      // Saved Y is an absolute scrollback position in the pinned xterm. Store
      // its offset from the viewport so bounded history can be restored too.
      savedY: buffer.savedY - buffer.ybase,
      savedAttributes: attributes(buffer.savedCurAttrData),
      savedCharset: buffer.savedCharset || null
    };
  }

  function encode(terminal, addon) {
    if (!isCheckpointSafe(terminal)) throw new Error('unsafeCheckpoint');
    const handler = inputHandler(terminal);
    const buffers = handler._bufferService.buffers;
    const charset = handler._charsetService;
    const core = handler._coreService;
    // SerializeAddon restores cells, but omits state needed to interpret future
    // output. Keep the supplementary state coupled to the vendored xterm 5.5.0.
    // Modes are restored below, after cells, so origin mode cannot move a cursor
    // that the addon has just restored or emit a spurious focus response.
    const content = encoder.encode(addon.serialize({ scrollback: 256, excludeModes: true }));
    const state = encoder.encode(JSON.stringify({
      normal: bufferState(buffers.normal),
      alternate: bufferState(buffers.alt),
      charset: { sets: charset._charsets, level: charset.glevel, active: charset.charset || null },
      attributes: attributes(handler._curAttrData),
      modes: core.modes,
      decPrivateModes: core.decPrivateModes,
      cursorHidden: core.isCursorHidden,
      cursorInitialized: core.isCursorInitialized,
      cursorStyle: terminal.options.cursorStyle,
      cursorBlink: terminal.options.cursorBlink,
      convertEol: terminal.options.convertEol,
      mouseProtocol: handler._coreMouseService.activeProtocol,
      mouseEncoding: handler._coreMouseService.activeEncoding,
      precedingJoinState: handler._parser.precedingJoinState
    }));
    // Keep VT bytes unescaped; JSON escaping every ESC would inflate the bounded
    // checkpoint and invalidate the authority's incremental memory budget.
    const result = new Uint8Array(header.length + 4 + content.length + state.length);
    result.set(header);
    new DataView(result.buffer).setUint32(header.length, content.length, true);
    result.set(content, header.length + 4);
    result.set(state, header.length + 4 + content.length);
    return result;
  }

  function decode(bytes) {
    // The session's pre-start empty screen and degraded-recovery notice are raw
    // VT, not authority checkpoints. They intentionally have no saved state.
    if (bytes.length === 0 || bytes[0] !== header[0]) {
      return { content: bytes, state: null };
    }
    if (bytes.length < header.length + 4 ||
        !header.every((value, index) => bytes[index] === value)) {
      throw new Error('invalidSnapshot');
    }
    const start = header.length + 4;
    const length = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
      .getUint32(header.length, true);
    if (length > bytes.length - start) throw new Error('invalidSnapshot');
    return {
      content: bytes.subarray(start, start + length),
      state: JSON.parse(decoder.decode(bytes.subarray(start + length)))
    };
  }

  function restoreAttributes(target, saved) {
    target.fg = saved.fg;
    target.bg = saved.bg;
    target.extended = target.extended.clone();
    target.extended._ext = saved.extended;
    // Authority deliberately excludes OSC 8 link metadata from checkpoints.
    target.extended.urlId = 0;
  }

  function restoreBuffer(buffer, saved) {
    buffer.x = saved.x;
    buffer.y = saved.y;
    buffer.scrollTop = saved.scrollTop;
    buffer.scrollBottom = saved.scrollBottom;
    buffer.tabs = saved.tabs;
    buffer.savedX = saved.savedX;
    buffer.savedY = buffer.ybase + saved.savedY;
    restoreAttributes(buffer.savedCurAttrData, saved.savedAttributes);
    buffer.savedCharset = saved.savedCharset;
  }

  function applyState(terminal, state) {
    if (!state) return;
    const handler = inputHandler(terminal);
    const buffers = handler._bufferService.buffers;
    restoreBuffer(buffers.normal, state.normal);
    restoreBuffer(buffers.alt, state.alternate);
    const charset = handler._charsetService;
    charset._charsets = state.charset.sets;
    charset.glevel = state.charset.level;
    charset.charset = state.charset.active;
    restoreAttributes(handler._curAttrData, state.attributes);
    Object.assign(handler._coreService.modes, state.modes);
    Object.assign(handler._coreService.decPrivateModes, state.decPrivateModes);
    handler._coreService.isCursorHidden = state.cursorHidden;
    handler._coreService.isCursorInitialized = state.cursorInitialized;
    handler._coreMouseService.activeProtocol = state.mouseProtocol;
    handler._coreMouseService.activeEncoding = state.mouseEncoding;
    handler._parser.precedingJoinState = state.precedingJoinState;
    terminal.options.cursorStyle = state.cursorStyle;
    terminal.options.cursorBlink = state.cursorBlink;
    terminal.options.convertEol = state.convertEol;
    if (terminal.element) terminal.refresh(0, terminal.rows - 1);
  }

  return { prepare, inputHandler, isCheckpointSafe, encode, decode, applyState };
});
