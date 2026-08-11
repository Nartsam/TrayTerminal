(function(root, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) {
    module.exports = api;
  }
  root.TrayTerminalInputPolicy = api;
})(typeof globalThis !== 'undefined' ? globalThis : this, function() {
  'use strict';

  function classifyCtrlC(state) {
    if (state.hasSelection) return 'copy';
    if (!state.isTrusted || !state.focused || state.isComposing ||
        state.repeat || state.held) {
      return 'consume';
    }
    return 'interrupt';
  }

  return { classifyCtrlC };
});
