// The SSH terminal page (WebView2). It only draws: every byte goes to and from Helm through chrome.webview messages,
// and Helm alone talks to the server. Messages from Helm: out (base64 output), reset (redraw from base64), font, paste,
// focus. Messages to Helm: ready, in (typed text), bin (binary input), size, copy, paste (asks for the clipboard).
(function () {
  'use strict';
  var host = window.chrome && window.chrome.webview;
  if (!host) return;

  var term = new Terminal({
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, "Courier New", monospace',
    fontSize: 14,
    cursorBlink: true,
    scrollback: 5000,
    // Links in the output are plain text: nothing a server prints can open anything.
    linkHandler: { activate: function () { } },
    theme: {
      background: '#0c0c0c', foreground: '#cccccc', cursor: '#ffffff', selectionBackground: '#3a5f8a',
      black: '#0c0c0c', red: '#c50f1f', green: '#13a10e', yellow: '#c19c00', blue: '#0037da', magenta: '#881798', cyan: '#3a96dd', white: '#cccccc',
      brightBlack: '#767676', brightRed: '#e74856', brightGreen: '#16c60c', brightYellow: '#f9f1a5', brightBlue: '#3b78ff', brightMagenta: '#b4009e',
      brightCyan: '#61d6d6', brightWhite: '#f2f2f2'
    }
  });
  var fit = new FitAddon.FitAddon();
  term.loadAddon(fit);
  var el = document.getElementById('term');
  term.open(el);

  function post(message) { host.postMessage(message); }

  function bytes(base64) {
    var text = atob(base64), out = new Uint8Array(text.length);
    for (var i = 0; i < text.length; i++) out[i] = text.charCodeAt(i);
    return out;
  }

  function refit() {
    try { fit.fit(); } catch (e) { /* not laid out yet */ }
  }

  term.onData(function (d) { post({ t: 'in', d: d }); });
  term.onBinary(function (d) { post({ t: 'bin', d: d }); });
  term.onResize(function (s) { post({ t: 'size', c: s.cols, r: s.rows }); });

  // Like Windows Terminal: Ctrl+C copies when text is selected (otherwise it interrupts), Ctrl+V and Ctrl+Shift+V
  // paste, Ctrl+Shift+C copies.
  term.attachCustomKeyEventHandler(function (e) {
    if (e.type !== 'keydown' || !e.ctrlKey || e.altKey) return true;
    if (e.code === 'KeyC' && (e.shiftKey || term.hasSelection())) {
      if (term.hasSelection()) { post({ t: 'copy', d: term.getSelection() }); term.clearSelection(); }
      e.preventDefault();
      return false;
    }
    if (e.code === 'KeyV') { post({ t: 'paste', b: term.modes.bracketedPasteMode }); e.preventDefault(); return false; }
    return true;
  });

  // Right click: copy the selection, or paste when nothing is selected.
  el.addEventListener('contextmenu', function (e) {
    e.preventDefault();
    if (term.hasSelection()) { post({ t: 'copy', d: term.getSelection() }); term.clearSelection(); }
    else post({ t: 'paste', b: term.modes.bracketedPasteMode });
  });

  host.addEventListener('message', function (e) {
    var m = e.data;
    if (!m || typeof m !== 'object') return;
    switch (m.t) {
      case 'out': term.write(bytes(m.d)); break;
      case 'reset': term.reset(); if (m.d) term.write(bytes(m.d)); break;
      case 'font': term.options.fontSize = m.s; refit(); break;
      // paste() wraps the text for bracketed paste when the shell asked for it, then sends it as typed text.
      case 'paste': term.paste(m.d); break;
      case 'focus': term.focus(); break;
    }
  });

  new ResizeObserver(refit).observe(el);
  refit();
  post({ t: 'ready', c: term.cols, r: term.rows });
})();
