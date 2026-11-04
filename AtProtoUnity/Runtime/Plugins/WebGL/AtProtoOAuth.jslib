mergeInto(LibraryManager.library, {
  AtProtoOAuth_OpenPopup: function (redirectUriPtr) {
    var redirect = document.createElement('a');
    redirect.href = UTF8ToString(redirectUriPtr);
    if (redirect.protocol !== window.location.protocol || redirect.host !== window.location.host) {
      return -1;
    }

    Module.atprotoOAuthCallback = null;
    if (!Module.atprotoOAuthListenersInstalled) {
      window.addEventListener('message', function (event) {
        if (event.origin !== window.location.origin
          || event.source !== Module.atprotoOAuthPopup
          || !event.data
          || event.data.type !== 'atproto-oauth-callback') {
          return;
        }
        Module.atprotoOAuthCallback = event.data;
      });
      if (typeof BroadcastChannel !== 'undefined') {
        Module.atprotoOAuthChannel = new BroadcastChannel('atproto-oauth-callback');
        Module.atprotoOAuthChannel.onmessage = function (event) {
          if (event.data && event.data.type === 'atproto-oauth-callback'
            && !Module.atprotoOAuthCallback) {
            Module.atprotoOAuthCallback = event.data;
          }
        };
      }
      Module.atprotoOAuthListenersInstalled = true;
    }

    var popup = Module.atprotoOAuthPopup;
    if (!popup || popup.closed) {
      popup = window.open('about:blank', '_blank', 'popup,width=560,height=760');
      Module.atprotoOAuthPopup = popup;
    }
    if (!popup) {
      return 0;
    }

    popup.document.open();
    popup.document.write('<!doctype html><title>Sign in</title><p>Connecting to your account provider...</p>');
    popup.document.close();
    return 1;
  },

  AtProtoOAuth_NavigatePopup: function (urlPtr) {
    var popup = Module.atprotoOAuthPopup;
    if (!popup || popup.closed) {
      return 0;
    }
    popup.location.replace(UTF8ToString(urlPtr));
    return 1;
  },

  AtProtoOAuth_TakeCallback: function (buffer, capacity) {
    var callback = Module.atprotoOAuthCallback;
    if (!callback) {
      return 0;
    }
    var json = JSON.stringify(callback);
    var length = lengthBytesUTF8(json);
    if (length >= capacity) {
      Module.atprotoOAuthCallback = null;
      return -1;
    }
    stringToUTF8(json, buffer, capacity);
    Module.atprotoOAuthCallback = null;
    return length;
  },

  AtProtoOAuth_ClosePopup: function () {
    var popup = Module.atprotoOAuthPopup;
    Module.atprotoOAuthPopup = null;
    Module.atprotoOAuthCallback = null;
    if (popup && !popup.closed) {
      popup.close();
    }
  }
});