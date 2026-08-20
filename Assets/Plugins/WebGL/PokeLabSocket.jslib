// WebSockets for the browser build.
//
// WHY THIS FILE HAS TO EXIST. Unity's WebGL player runs on Emscripten, where there are no
// threads and no sockets: System.Net.WebSockets.ClientWebSocket compiles and then throws at
// runtime, and System.Net.Http does not work either. The browser's own WebSocket is right
// there in the page, so the only route is a small JS shim the C# side calls into. Every Unity
// multiplayer package that supports WebGL ships some version of this.
//
// The C# side is Assets/Game/Scripts/Online/PvpSocket.cs; the two are one component split
// across a language boundary. Anything renamed here must be renamed there.
//
// SHAPE. Sockets are held in a table and addressed by an integer handle rather than a pointer,
// so a socket that has already been closed and freed is a stale id that resolves to nothing
// instead of a use-after-free inside the browser. Incoming frames are queued rather than
// delivered by callback: Unity's main loop pulls them on its own frame, which keeps every
// message arriving on the Unity thread, in order, with no reentrancy into managed code from a
// browser event handler.

var PokeLabSocketLib = {

  $PokeLabSocketState: {
    sockets: {},
    next: 1,
  },

  /**
   * Opens a socket. Returns a handle, or 0 if the browser refused the URL outright.
   * State: 0 connecting, 1 open, 2 closed, 3 error.
   */
  PokeLabSocketOpen: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var id = PokeLabSocketState.next++;

    var entry = {
      ws: null,
      state: 0,
      queue: [],
      closeCode: 0,
      closeReason: "",
    };

    try {
      entry.ws = new WebSocket(url);
    } catch (e) {
      // A malformed URL, or a mixed-content block (ws:// from an https:// page). Recorded as
      // an error state rather than thrown, so the C# side reports it like any other failure.
      entry.state = 3;
      entry.closeReason = (e && e.message) ? e.message : "WebSocket constructor refused the URL";
      PokeLabSocketState.sockets[id] = entry;
      return id;
    }

    entry.ws.onopen = function () { entry.state = 1; };

    entry.ws.onmessage = function (event) {
      // Text frames only. The protocol is JSON lines; a binary frame is something else's
      // traffic and is dropped rather than half-decoded.
      if (typeof event.data === "string") entry.queue.push(event.data);
    };

    entry.ws.onerror = function () {
      // The browser deliberately withholds the reason for a socket error, so there is nothing
      // more specific to record here than that it happened.
      if (entry.state !== 2) entry.state = 3;
    };

    entry.ws.onclose = function (event) {
      entry.state = 2;
      entry.closeCode = event.code;
      entry.closeReason = event.reason || "";
    };

    PokeLabSocketState.sockets[id] = entry;
    return id;
  },

  PokeLabSocketState_: function () {},

  /** 0 connecting, 1 open, 2 closed, 3 error, -1 unknown handle. */
  PokeLabSocketGetState: function (id) {
    var entry = PokeLabSocketState.sockets[id];
    if (!entry) return -1;
    return entry.state;
  },

  /** 1 if the frame was handed to the browser, 0 if the socket was not open. */
  PokeLabSocketSend: function (id, textPtr) {
    var entry = PokeLabSocketState.sockets[id];
    if (!entry || !entry.ws || entry.ws.readyState !== 1) return 0;
    try {
      entry.ws.send(UTF8ToString(textPtr));
      return 1;
    } catch (e) {
      return 0;
    }
  },

  /** Bytes the next queued frame needs, including the null terminator. 0 when the queue is empty. */
  PokeLabSocketPeekLength: function (id) {
    var entry = PokeLabSocketState.sockets[id];
    if (!entry || entry.queue.length === 0) return 0;
    return lengthBytesUTF8(entry.queue[0]) + 1;
  },

  /**
   * Copies the next queued frame into a buffer the caller allocated from PeekLength, and
   * removes it from the queue. Two calls rather than one because managed code has to know the
   * size before it can hand over somewhere to write — and UTF-8 length is not string length,
   * which for Korean payloads is a factor of three.
   */
  PokeLabSocketDequeue: function (id, buffer, size) {
    var entry = PokeLabSocketState.sockets[id];
    if (!entry || entry.queue.length === 0) return 0;
    var text = entry.queue.shift();
    stringToUTF8(text, buffer, size);
    return 1;
  },

  PokeLabSocketGetCloseCode: function (id) {
    var entry = PokeLabSocketState.sockets[id];
    return entry ? entry.closeCode : 0;
  },

  /** Closes and forgets the socket. Safe on a handle that is already gone. */
  PokeLabSocketClose: function (id) {
    var entry = PokeLabSocketState.sockets[id];
    if (!entry) return;
    try {
      if (entry.ws && (entry.ws.readyState === 0 || entry.ws.readyState === 1)) entry.ws.close(1000, "client");
    } catch (e) {
      /* already closing */
    }
    // Handlers are dropped with the entry, so a close event arriving after this cannot push
    // onto a queue nobody will ever read.
    delete PokeLabSocketState.sockets[id];
  },
};

autoAddDeps(PokeLabSocketLib, '$PokeLabSocketState');
mergeInto(LibraryManager.library, PokeLabSocketLib);
