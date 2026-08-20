using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
#if !UNITY_WEBGL || UNITY_EDITOR
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
#endif

namespace PokeLab.Online
{
    /// <summary>
    /// One WebSocket, over the two entirely different transports this game ships on.
    ///
    /// <b>Why there are two implementations.</b> The browser build runs on Emscripten, which has
    /// no threads and no sockets — <c>ClientWebSocket</c> compiles there and throws at runtime.
    /// The editor and desktop players have no browser to borrow one from. So WebGL calls into
    /// <c>Assets/Plugins/WebGL/PokeLabSocket.jslib</c>, everything else uses
    /// <c>ClientWebSocket</c>, and both are hidden behind the same five members below. This is
    /// the shape every Unity multiplayer package that supports WebGL ends up with; it is not
    /// worth being clever about.
    ///
    /// <b>Nothing here is delivered by callback.</b> Frames land in a queue and the owner pulls
    /// them with <see cref="TryReceive"/> from its own Update. On WebGL that keeps browser event
    /// handlers from reentering managed code; on desktop it keeps the socket's background task
    /// from touching Unity objects off the main thread, which is not merely bad practice but a
    /// crash. The cost is one frame of latency on a turn-based battle, which is nothing.
    /// </summary>
    public sealed class PvpSocket : IDisposable
    {
        public enum SocketState { Connecting = 0, Open = 1, Closed = 2, Error = 3 }

        private readonly Queue<string> _inbox = new Queue<string>();

        /// <summary>Why it ended, when it ended badly. Empty while healthy.</summary>
        public string Error { get; private set; } = "";

        public SocketState State { get; private set; } = SocketState.Connecting;

        public bool IsOpen => State == SocketState.Open;

        /// <summary>Opens a socket to <paramref name="url"/>. Returns immediately; poll <see cref="Pump"/>.</summary>
        public static PvpSocket Connect(string url)
        {
            var socket = new PvpSocket();
            socket.Open(url);
            return socket;
        }

        /// <summary>
        /// Called once per frame by the owner. Moves whatever the transport has produced into
        /// the inbox and refreshes <see cref="State"/>.
        /// </summary>
        public void Pump()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            PumpWeb();
#else
            PumpNative();
#endif
        }

        /// <summary>Takes the next frame, or false when there is nothing waiting.</summary>
        public bool TryReceive(out string message)
        {
            if (_inbox.Count == 0) { message = null; return false; }
            message = _inbox.Dequeue();
            return true;
        }

        public bool Send(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
#if UNITY_WEBGL && !UNITY_EDITOR
            return PokeLabSocketSend(_handle, message) == 1;
#else
            return SendNative(message);
#endif
        }

        public void Dispose()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            if (_handle != 0) { PokeLabSocketClose(_handle); _handle = 0; }
#else
            DisposeNative();
#endif
            State = SocketState.Closed;
        }

        private void Open(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                State = SocketState.Error;
                Error = "empty_url";
                return;
            }

#if UNITY_WEBGL && !UNITY_EDITOR
            _handle = PokeLabSocketOpen(url);
            if (_handle == 0) { State = SocketState.Error; Error = "socket_refused"; }
#else
            OpenNative(url);
#endif
        }

        // --- WebGL ---------------------------------------------------------------------------

#if UNITY_WEBGL && !UNITY_EDITOR
        private int _handle;

        [DllImport("__Internal")] private static extern int PokeLabSocketOpen(string url);
        [DllImport("__Internal")] private static extern int PokeLabSocketGetState(int id);
        [DllImport("__Internal")] private static extern int PokeLabSocketSend(int id, string text);
        [DllImport("__Internal")] private static extern int PokeLabSocketPeekLength(int id);
        [DllImport("__Internal")] private static extern int PokeLabSocketDequeue(int id, byte[] buffer, int size);
        [DllImport("__Internal")] private static extern int PokeLabSocketGetCloseCode(int id);
        [DllImport("__Internal")] private static extern void PokeLabSocketClose(int id);

        private void PumpWeb()
        {
            if (_handle == 0) return;

            var reported = PokeLabSocketGetState(_handle);
            if (reported >= 0) State = (SocketState)reported;

            // Drained fully each frame rather than one per frame: a turn and its follow-up can
            // arrive in the same browser tick, and holding the second until the next frame would
            // show the battle one beat behind for the rest of the match.
            while (true)
            {
                var length = PokeLabSocketPeekLength(_handle);
                if (length <= 0) break;

                var buffer = new byte[length];
                if (PokeLabSocketDequeue(_handle, buffer, length) != 1) break;

                // length includes the null terminator the shim wrote; the string stops before it.
                _inbox.Enqueue(Encoding.UTF8.GetString(buffer, 0, length - 1));
            }

            if (State == SocketState.Error && string.IsNullOrEmpty(Error)) Error = "socket_error";
            if (State == SocketState.Closed && string.IsNullOrEmpty(Error))
            {
                var code = PokeLabSocketGetCloseCode(_handle);
                // 1000 is a clean close, which is not an error — the lobby closes the socket
                // deliberately the moment it has paired you.
                if (code != 0 && code != 1000) Error = "closed_" + code;
            }
        }
#else
        // --- Editor and desktop --------------------------------------------------------------

        private ClientWebSocket _client;
        private CancellationTokenSource _cancel;

        /// <summary>
        /// Written by the receive task, read by Pump on the main thread. Locked rather than made
        /// a ConcurrentQueue so the state flags beside it move under the same lock — a frame
        /// enqueued and a socket marked closed must not be observed out of order.
        /// </summary>
        private readonly Queue<string> _incoming = new Queue<string>();
        private readonly object _gate = new object();
        private volatile bool _closedByTask;
        private string _taskError = "";

        private void OpenNative(string url)
        {
            _client = new ClientWebSocket();
            _cancel = new CancellationTokenSource();
            _ = RunAsync(url, _cancel.Token);
        }

        private async Task RunAsync(string url, CancellationToken token)
        {
            try
            {
                await _client.ConnectAsync(new Uri(url), token);
                State = SocketState.Open;

                var buffer = new byte[8192];
                var builder = new StringBuilder();

                while (!token.IsCancellationRequested && _client.State == WebSocketState.Open)
                {
                    var result = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        lock (_gate) _closedByTask = true;
                        break;
                    }

                    builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    // A frame can arrive in several reads; only a complete one is a message.
                    if (!result.EndOfMessage) continue;

                    lock (_gate) _incoming.Enqueue(builder.ToString());
                    builder.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                // Dispose during a read. Not a failure.
            }
            catch (Exception e)
            {
                lock (_gate)
                {
                    _taskError = e.Message;
                    _closedByTask = true;
                }
            }
            finally
            {
                lock (_gate) _closedByTask = true;
            }
        }

        private void PumpNative()
        {
            lock (_gate)
            {
                while (_incoming.Count > 0) _inbox.Enqueue(_incoming.Dequeue());

                if (_closedByTask && State != SocketState.Closed)
                {
                    State = string.IsNullOrEmpty(_taskError) ? SocketState.Closed : SocketState.Error;
                    if (!string.IsNullOrEmpty(_taskError) && string.IsNullOrEmpty(Error)) Error = _taskError;
                }
            }
        }

        private bool SendNative(string message)
        {
            if (_client == null || _client.State != WebSocketState.Open) return false;
            var bytes = Encoding.UTF8.GetBytes(message);
            // Fire and forget: the protocol is one small JSON object per turn and the send
            // cannot meaningfully fail in a way the caller could act on that a closed socket
            // would not also report on the next Pump.
            _ = _client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true,
                _cancel.Token);
            return true;
        }

        private void DisposeNative()
        {
            try { _cancel?.Cancel(); } catch { /* already gone */ }
            try { _client?.Dispose(); } catch { /* already gone */ }
            _client = null;
            _cancel = null;
        }
#endif
    }
}
