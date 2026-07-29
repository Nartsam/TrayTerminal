(function(root, factory) {
  if (typeof module === 'object' && module.exports) {
    module.exports = factory();
  } else {
    root.TrayTerminalRemoteProtocol = factory();
  }
})(globalThis, function() {
  'use strict';

  function createSerialProcessor(handler, onError) {
    let chain = Promise.resolve();
    return {
      push(message) {
        chain = chain
          .then(() => handler(message))
          .catch(error => onError(error));
        return chain;
      },
      drain() {
        return chain;
      }
    };
  }

  return { createSerialProcessor };
});
