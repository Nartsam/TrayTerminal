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

  function parseSyncStart(snapshotSequence, latestSequence) {
    if (typeof snapshotSequence !== 'string' ||
        typeof latestSequence !== 'string' ||
        !/^\d+$/.test(snapshotSequence) ||
        !/^\d+$/.test(latestSequence)) {
      throw new Error('Invalid synchronization header');
    }

    const snapshot = BigInt(snapshotSequence);
    const latest = BigInt(latestSequence);
    if (snapshot > latest) {
      throw new Error('Snapshot is newer than advertised terminal state');
    }
    return { snapshot, latest };
  }

  function isSyncComplete(sequence, advertisedLatest, expectedLast) {
    return typeof sequence === 'string' &&
      /^\d+$/.test(sequence) &&
      BigInt(sequence) === advertisedLatest &&
      BigInt(sequence) === expectedLast;
  }

  function shouldHideInputStatus(statusKind, unresolvedInputResults) {
    return statusKind === 'input' && unresolvedInputResults !== true;
  }

  return {
    createSerialProcessor,
    parseSyncStart,
    isSyncComplete,
    shouldHideInputStatus
  };
});
