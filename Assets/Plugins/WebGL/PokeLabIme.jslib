// A real HTML text field, parked exactly over whichever Unity input field has focus.
//
// WHY THIS EXISTS. Unity's WebGL player reads the keyboard from `keydown`/`keypress` on the
// document and turns key codes into characters itself. An IME does not produce characters that
// way: typing 한글 fires `compositionstart`, a run of `compositionupdate`s while the syllable is
// assembled, and finally `compositionend` with the finished text, and none of that reaches a
// key handler. So Unity sees the raw jamo keys as Latin letters, or -- because the browser
// swallows keydown while a composition is live -- sees nothing at all. That is the user's
// report: 트레이너 이름이랑 답 칸이 한글 입력이 안됨. It is not a bug in the field; there is no
// arrangement of TMP_InputField that can fix it, because the text never arrives.
//
// The fix every Unity WebGL project ends up at is to stop trying: put an honest <input> in the
// DOM, let the browser and the operating system's IME do what they are for, and copy the value
// across each frame. The element is transparent and sits precisely over the Unity field, so the
// caret the player sees is TMP's and the candidate window the IME opens is positioned against
// the place they are actually typing.
//
// The element is INVISIBLE BUT NOT HIDDEN, and that distinction is load-bearing: display:none,
// visibility:hidden, and zero size all make an element unfocusable or move the IME's candidate
// popup to the corner of the page. Transparent text on a transparent caret at the right size and
// position is the only version that behaves.
var PokeLabImeLib = {

  $PLIme: {
    el: null,
    composing: false,

    canvas: function () {
      return document.querySelector('#unity-canvas') || document.querySelector('canvas');
    },

    ensure: function () {
      if (PLIme.el) return PLIme.el;

      var el = document.createElement('input');
      el.type = 'text';
      el.id = 'pokelab-ime';
      el.autocomplete = 'off';
      el.autocapitalize = 'off';
      el.autocorrect = 'off';
      el.spellcheck = false;

      var s = el.style;
      s.position = 'absolute';
      s.zIndex = '10';
      s.padding = '0';
      s.margin = '0';
      s.border = 'none';
      s.outline = 'none';
      s.background = 'transparent';
      s.color = 'transparent';
      s.caretColor = 'transparent';
      // Chrome paints a text-shadow even on transparent text; kill it rather than trust the
      // default, since one stray glyph over TMP's own render reads as a double-drawn field.
      s.textShadow = 'none';

      el.addEventListener('compositionstart', function () { PLIme.composing = true; });
      el.addEventListener('compositionend', function () { PLIme.composing = false; });

      document.body.appendChild(el);
      PLIme.el = el;
      return el;
    },

    // Unity framebuffer pixels -> CSS pixels. Derived from the canvas rather than from
    // devicePixelRatio because the page may letterbox the canvas or scale it by CSS, and the
    // ratio between the canvas's backing store and its box is the only measurement that
    // accounts for both.
    place: function (x, y, w, h) {
      var canvas = PLIme.canvas();
      if (!canvas) return;

      var r = canvas.getBoundingClientRect();
      var sx = canvas.width > 0 ? r.width / canvas.width : 1;
      var sy = canvas.height > 0 ? r.height / canvas.height : 1;

      var s = PLIme.el.style;
      s.left = (r.left + window.scrollX + x * sx) + 'px';
      s.top = (r.top + window.scrollY + y * sy) + 'px';
      s.width = Math.max(1, w * sx) + 'px';
      s.height = Math.max(1, h * sy) + 'px';
      // The IME sizes its candidate window off the field's font. Left at the browser default
      // it opens a popup scaled for 13px text over a field three times that.
      s.fontSize = Math.max(8, h * sy * 0.55) + 'px';
    }
  },

  // Shows the field, moves it over the Unity rect, and gives it focus if it does not have it.
  // Called every frame while a Unity field is selected: placing is idempotent and re-focusing
  // is guarded, so a canvas that resizes or a page that scrolls is tracked for free.
  PokeLabImeOpen: function (x, y, w, h, textPtr, caret) {
    var el = PLIme.ensure();
    var text = UTF8ToString(textPtr);

    PLIme.place(x, y, w, h);

    if (document.activeElement !== el) {
      el.value = text;
      // preventScroll: focusing an element the browser thinks is off-view scrolls the page,
      // and the page here is a full-bleed game canvas that must not move.
      try { el.focus({ preventScroll: true }); } catch (e) { el.focus(); }
      try { el.setSelectionRange(caret, caret); } catch (e) { /* not a text input yet */ }
      return 1;
    }

    // Unity is the owner of the text ONLY while nothing is being composed. Writing during a
    // composition tears the half-built syllable out from under the IME.
    if (!PLIme.composing && el.value !== text) {
      el.value = text;
      try { el.setSelectionRange(caret, caret); } catch (e) { /* ignore */ }
    }
    return 0;
  },

  PokeLabImeClose: function () {
    if (!PLIme.el) return;
    PLIme.composing = false;
    if (document.activeElement === PLIme.el) PLIme.el.blur();
    PLIme.el.value = '';
    PLIme.el.style.left = '-9999px';
  },

  // Current value, as UTF-8, into a buffer Unity owns. Returns the byte length written,
  // or -1 when there is no field -- which is not the same as an empty field.
  PokeLabImeRead: function (buffer, size) {
    if (!PLIme.el) return -1;
    var value = PLIme.el.value || '';
    var length = lengthBytesUTF8(value) + 1;
    if (length > size) return -1;
    stringToUTF8(value, buffer, size);
    return length - 1;
  },

  // Where the browser's caret is, in UTF-16 code units, so TMP can draw its own there.
  PokeLabImeCaret: function () {
    if (!PLIme.el) return 0;
    var at = PLIme.el.selectionStart;
    return (at === null || at === undefined) ? (PLIme.el.value || '').length : at;
  },

  // True while a syllable is half-built. Unity must not write over the value in that window.
  PokeLabImeComposing: function () {
    return (PLIme.el && PLIme.composing) ? 1 : 0;
  },

  PokeLabImeFocused: function () {
    return (PLIme.el && document.activeElement === PLIme.el) ? 1 : 0;
  }
};

autoAddDeps(PokeLabImeLib, '$PLIme');
mergeInto(LibraryManager.library, PokeLabImeLib);
