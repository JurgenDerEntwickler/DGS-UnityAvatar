// Unity (WebGL) -> web page. The page defines window.SignBridge = { ready, signStarted, queueEmpty }
// (DGSNEW web/app.js). Calls are ignored if the page does not define them.
mergeInto(LibraryManager.library, {
  SignBridge_Ready: function (receiverName) {
    var name = UTF8ToString(receiverName);
    if (window.SignBridge && window.SignBridge.ready) window.SignBridge.ready(name);
  },
  SignBridge_SignStarted: function (id) {
    if (window.SignBridge && window.SignBridge.signStarted) window.SignBridge.signStarted(id);
  },
  SignBridge_QueueEmpty: function () {
    if (window.SignBridge && window.SignBridge.queueEmpty) window.SignBridge.queueEmpty();
  },
});
