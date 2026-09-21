// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Bilocus.Protocol;

namespace Bilocus.Revit.Net
{
    // ABSOLUTE RULE OF THIS FILE: no calls to the Revit API, ever.
    // Everything in here runs on the socket thread, and the Revit API is only
    // usable from the main thread in a valid API context. Breaking the rule
    // does not produce an error: it produces random, unreproducible crashes.
    //
    // The rule is enforced by the compiler, not by discipline: this file has
    // no using for Autodesk.*, and the test project compiles it without any
    // reference to the Revit API. Notifying the main thread goes through
    // IEventRaiser.
    public sealed class BridgeServer
    {
        private readonly MessageQueue _queue;
        private readonly IEventRaiser _raiser;
        private readonly int _requestedPort;

        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        // volatile: written by the socket thread, read by Send and Stop on the
        // main thread. Without volatile the JIT could keep a copy in a register.
        private volatile TcpClient _client;
        private volatile NetworkStream _stream;

        private readonly object _writeGate = new object();
        private volatile string _status = "stopped";
        private volatile int _boundPort;

        // The setter is used by App.OnStartup to record the reason for a
        // failed startup: with the port taken, the Status button is the only
        // way to know what happened.
        public string Status
        {
            get { return _status; }
            set { _status = value; }
        }

        // Port actually being listened on. Matches the requested one except
        // when 0 is requested, which tests use to grab a free port instead of
        // fighting over 9877 with an open Revit.
        public int Port { get { return _boundPort; } }

        public bool IsRunning { get { return _running; } }

        // True when there is an open stream to a client, i.e. when Send would
        // have someone to write to. Used by callers who want to verify that
        // Blender is connected BEFORE doing expensive work (tessellating a
        // selection), instead of finding out only at send time.
        public bool IsClientConnected { get { return _stream != null; } }

        public BridgeServer(MessageQueue queue, IEventRaiser raiser)
            : this(queue, raiser, BridgeConstants.DefaultPort)
        {
        }

        public BridgeServer(MessageQueue queue, IEventRaiser raiser, int port)
        {
            if (queue == null) throw new ArgumentNullException("queue");
            if (raiser == null) throw new ArgumentNullException("raiser");
            _queue = queue;
            _raiser = raiser;
            _requestedPort = port;
        }

        // Can throw SocketException if the port is taken. The caller must
        // guard against it: from OnStartup an uncaught exception makes Revit
        // disable the whole add-in, ribbon included.
        public void Start()
        {
            if (_running) return;

            TcpListener listener = new TcpListener(
                IPAddress.Parse(BridgeConstants.DefaultHost), _requestedPort);
            try
            {
                listener.Start();
            }
            catch (Exception)
            {
                // The bind failed: the status stays "stopped" and _running
                // stays false, so a subsequent Start can really retry.
                try { listener.Stop(); } catch (Exception) { }
                throw;
            }

            _listener = listener;
            _boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _running = true;
            _status = string.Format(
                "listening on {0}:{1}", BridgeConstants.DefaultHost, _boundPort);

            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "Bilocus listener";
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;

            TcpListener listener = _listener;
            if (listener != null)
            {
                // unblocks AcceptTcpClient and frees the port
                try { listener.Stop(); } catch (Exception) { }
            }

            CloseClientForShutdown();

            Thread thread = _thread;
            bool joined = thread == null || thread.Join(2000);

            _listener = null;
            _thread = null;
            _boundPort = 0;
            _status = joined
                ? "stopped"
                : "stopped (the listener thread did not respond within 2 seconds)";
        }

        // Returns true if the frame went out in full.
        public bool Send(Frame frame)
        {
            NetworkStream stream = _stream;
            if (stream == null)
            {
                _status = "send skipped: no client connected";
                return false;
            }

            lock (_writeGate)
            {
                try
                {
                    FrameCodec.Write(stream, frame);
                    stream.Flush();
                    return true;
                }
                catch (ArgumentException ex)
                {
                    // FrameCodec validates the lengths BEFORE touching the
                    // stream: not a single byte has gone out here, the
                    // connection is intact and the case is recoverable
                    // (DESIGN 5.1, "recoverable" line).
                    _status = "invalid frame, not sent: " + ex.Message;
                    return false;
                }
                catch (Exception ex)
                {
                    // Here instead the write was interrupted mid-frame: the
                    // peer has read a length prefix and will not receive the
                    // promised bytes. The protocol has no delimiters, there is
                    // no realignment, so this is terminal for the connection:
                    // the socket is closed and listening resumes.
                    _status = "write error, connection closed: " + ex.Message;
                    CloseClient();
                    return false;
                }
            }
        }

        private void Loop()
        {
            while (_running)
            {
                TcpClient client = null;
                bool accepted = false;
                try
                {
                    client = _listener.AcceptTcpClient();

                    // Nagle disabled: with Nagle on, an 84-byte transform frame
                    // split into multiple segments at 30 Hz can run into an
                    // interaction with the peer's delayed ACK and pick up tens
                    // of milliseconds of delay. This is the live path, the one
                    // that shows on screen.
                    client.NoDelay = true;

                    accepted = true;
                    _client = client;

                    // Stop() could have run between the accept and the line
                    // above, without then finding the client to close: the
                    // check avoids being stuck forever in Read.
                    if (!_running) break;

                    NetworkStream stream = client.GetStream();
                    _stream = stream;
                    _status = "connected";

                    Serve(stream);
                }
                catch (Exception ex)
                {
                    // Every error outcome ends up here and goes through the
                    // finally, which closes the socket: that is the correct
                    // response for all three error cases in the contract
                    // (DESIGN 5.1). Only the diagnosis written to Status changes.
                    if (_running) { _status = Describe(ex); }
                }
                finally
                {
                    _stream = null;
                    _client = null;
                    if (client != null) { try { client.Close(); } catch (Exception) { } }
                }

                // A repeated accept failure, with the listener still alive,
                // would spin a core inside Revit for nothing.
                if (!accepted && _running) { Thread.Sleep(100); }
            }
        }

        private void Serve(NetworkStream stream)
        {
            // No TcpClient.Connected in the condition: that flag reflects the
            // state of the LAST I/O operation, not the current state of the
            // connection. Disconnection is detected by FrameCodec.Read, which
            // is the only reliable source.
            while (_running)
            {
                Frame frame = FrameCodec.Read(stream);
                if (frame == null)
                {
                    // Clean close at a frame boundary: this is the EXPECTED
                    // case, Blender gets restarted N times during a Revit
                    // session. It is not an error (DESIGN 5.1, first line).
                    _status = "client disconnected";
                    return;
                }

                _queue.Enqueue(frame);
                _raiser.Raise();
            }
        }

        private static string Describe(Exception ex)
        {
            if (ex is InvalidDataException || ex is DecoderFallbackException)
            {
                // Length out of bounds or header not valid UTF-8. If the
                // rejected length was the payload's, the payload bytes are
                // still in the socket: the stream is desynchronized for good.
                // A catch/log/continue would read garbage forever, the only
                // correct response is to close.
                return "stream desynchronized, connection closed: " + ex.Message;
            }
            if (ex is EndOfStreamException)
            {
                return "truncated frame, connection closed: " + ex.Message;
            }
            return string.Format("disconnected: {0}: {1}", ex.GetType().Name, ex.Message);
        }

        private void CloseClient()
        {
            TcpClient client = _client;
            _stream = null;
            _client = null;
            if (client != null) { try { client.Close(); } catch (Exception) { } }
        }

        // Shutdown also touches the stream, so it goes through writeGate too:
        // closing the socket while a Send is writing would leave a truncated
        // frame on the wire. The lock is taken with a timeout because a write
        // blocked on a slow peer must not be able to freeze Revit's shutdown:
        // once the timeout expires the socket closes anyway, and the Send in
        // progress fails with ObjectDisposedException, which Send already
        // handles.
        private void CloseClientForShutdown()
        {
            bool locked = Monitor.TryEnter(_writeGate, 500);
            try { CloseClient(); }
            finally { if (locked) { Monitor.Exit(_writeGate); } }
        }
    }
}
