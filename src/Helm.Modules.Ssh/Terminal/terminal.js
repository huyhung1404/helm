// The SSH terminal page, shared by Windows (WebView2) and Android (WebView). It only draws: every byte goes to and from
// Helm, and Helm alone talks to the server.
// Messages from Helm: out (base64 output), reset (redraw from base64), font, paste, focus, key (a key from the phone's
// key bar: esc, tab, up, down, left, right, home, end).
// Messages to Helm: ready, in (typed text), bin (binary input), size, copy, paste (asks for the clipboard).
// Windows talks through chrome.webview. Android hands the page a MessagePort ("helm-port") from its own origin; until
// it arrives, messages wait in a queue.
(function () {
  'use strict';
  var webview2 = window.chrome && window.chrome.webview;
  var port = null;
  var queue = [];

  var term = new Terminal({
    // No "Courier New": Android maps it to a typewriter face. "monospace" there is Droid Sans Mono.
    fontFamily: '"Cascadia Mono", "Cascadia Code", Consolas, monospace',
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

  function post(message) {
    if (webview2) webview2.postMessage(message);
    else if (port) port.postMessage(JSON.stringify(message));
    else queue.push(message);
  }

  function bytes(base64) {
    var text = atob(base64), out = new Uint8Array(text.length);
    for (var i = 0; i < text.length; i++) out[i] = text.charCodeAt(i);
    return out;
  }

  function refit() {
    try { fit.fit(); } catch (e) { /* not laid out yet */ }
  }

  // Keys from the phone's key bar, as the terminal itself would send them (cursor keys follow the application mode
  // that programs such as vim and less turn on).
  function keySequence(k) {
    var app = term.modes.applicationCursorKeysMode;
    switch (k) {
      case 'esc': return '\u001b';
      case 'tab': return '\t';
      case 'up': return app ? '\u001bOA' : '\u001b[A';
      case 'down': return app ? '\u001bOB' : '\u001b[B';
      case 'right': return app ? '\u001bOC' : '\u001b[C';
      case 'left': return app ? '\u001bOD' : '\u001b[D';
      case 'home': return app ? '\u001bOH' : '\u001b[H';
      case 'end': return app ? '\u001bOF' : '\u001b[F';
      default: return '';
    }
  }

  function receive(m) {
    if (!m || typeof m !== 'object') return;
    switch (m.t) {
      case 'out': term.write(bytes(m.d)); break;
      case 'reset': term.reset(); if (m.d) term.write(bytes(m.d)); break;
      case 'font': term.options.fontSize = m.s; refit(); break;
      // paste() wraps the text for bracketed paste when the shell asked for it, then sends it as typed text.
      case 'paste': term.paste(m.d); break;
      case 'focus': term.focus(); break;
      case 'key': var s = keySequence(m.k); if (s) post({ t: 'in', d: s }); break;
    }
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

  if (webview2) {
    webview2.addEventListener('message', function (e) { receive(e.data); });
  } else {
    // Android: only a port posted by Helm to this page's own origin is accepted, and only once.
    window.addEventListener('message', function (e) {
      if (port || e.data !== 'helm-port' || !e.ports || !e.ports[0]) return;
      port = e.ports[0];
      port.onmessage = function (ev) {
        try { receive(JSON.parse(ev.data)); } catch (x) { /* not ours */ }
      };
      var waiting = queue; queue = [];
      waiting.forEach(post);
    });
  }

  new ResizeObserver(refit).observe(el);
  refit();
  post({ t: 'ready', c: term.cols, r: term.rows });
})();
