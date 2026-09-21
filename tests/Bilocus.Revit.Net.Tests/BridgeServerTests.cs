// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Bilocus.Protocol;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // Real integration tests: they open sockets on loopback and make the
    // server talk to a fake client. They cover what inside Revit could only
    // be checked by hand with netstat, i.e. whether the port is listening
    // and whether shutdown really frees it.
    public class BridgeServerTests
    {
        private const string Hello =
            "{\"type\": \"hello\", \"protocol_version\": 1, \"client\": \"blender\"}";

        [Fact]
        public void StartListensAndShowsInStatus()
        {
            using (Harness h = new Harness())
            {
                Assert.True(h.Server.IsRunning);
                Assert.True(h.Server.Port > 0);
                Assert.Contains("listening on 127.0.0.1:", h.Server.Status);

                // proof that the bind is real: a second listener on the same
                // port must fail
                TcpListener intruder = new TcpListener(IPAddress.Loopback, h.Server.Port);
                Assert.Throws<SocketException>(delegate { intruder.Start(); });
            }
        }

        [Fact]
        public void ConnectionAndHelloAckHandshake()
        {
            using (Harness h = new Harness())
            using (TcpClient client = h.ConnectAndWait())
            {
                NetworkStream stream = client.GetStream();
                FrameCodec.Write(stream, new Frame(Hello, null));
                stream.Flush();

                Assert.True(TestWait.Until(delegate { return h.Raiser.Count >= 1; }, 5000),
                    "the server did not notify the main thread");

                List<Frame> drained = h.Queue.DrainAll();
                Assert.Single(drained);
                Assert.Equal(Hello, drained[0].Header);
                Assert.Empty(drained[0].Payload);

                // what MessageHandler would do on the main thread
                string ack = "{\"type\":\"hello_ack\",\"protocol_version\":1,\"revit_version\":\"2025\",\"doc_title\":\"\"}";
                Assert.True(h.Server.Send(new Frame(ack, null)));

                Frame reply = FrameCodec.Read(stream);
                Assert.NotNull(reply);
                Assert.Equal(ack, reply.Header);
            }
        }

        [Fact]
        public void MultipleFramesArriveInSequenceAndInOrder()
        {
            using (Harness h = new Harness())
            using (TcpClient client = h.ConnectAndWait())
            {
                NetworkStream stream = client.GetStream();
                for (int i = 0; i < 5; i++)
                {
                    byte[] payload = new byte[] { (byte)i, (byte)(i + 100) };
                    FrameCodec.Write(stream, new Frame("{\"type\":\"n\",\"i\":" + i + "}", payload));
                }
                stream.Flush();

                List<Frame> all = new List<Frame>();
                Assert.True(TestWait.Until(delegate
                {
                    all.AddRange(h.Queue.DrainAll());
                    return all.Count >= 5;
                }, 5000), "only " + all.Count + " frames out of 5 arrived");

                for (int i = 0; i < 5; i++)
                {
                    Assert.Equal("{\"type\":\"n\",\"i\":" + i + "}", all[i].Header);
                    Assert.Equal(2, all[i].Payload.Length);
                    Assert.Equal((byte)i, all[i].Payload[0]);
                    Assert.Equal((byte)(i + 100), all[i].Payload[1]);
                }
            }
        }

        [Fact]
        public void CleanClientCloseIsNotAnError()
        {
            using (Harness h = new Harness())
            {
                TcpClient client = h.ConnectAndWait();
                NetworkStream stream = client.GetStream();
                FrameCodec.Write(stream, new Frame(Hello, null));
                stream.Flush();
                Assert.True(TestWait.Until(delegate { return h.Raiser.Count >= 1; }, 5000));

                // close exactly at the frame boundary: the EXPECTED case
                client.Close();

                Assert.True(TestWait.Until(
                    delegate { return h.Server.Status == "client disconnected"; }, 5000),
                    "status after the clean close: " + h.Server.Status);
                Assert.True(h.Server.IsRunning);
            }
        }

        [Fact]
        public void ReconnectionAfterDisconnection()
        {
            using (Harness h = new Harness())
            {
                // Blender gets restarted N times during a Revit session:
                // three rounds of connect and close on the same server.
                for (int round = 0; round < 3; round++)
                {
                    using (TcpClient client = h.ConnectAndWait())
                    {
                        NetworkStream stream = client.GetStream();
                        string header = "{\"type\":\"hello\",\"round\":" + round + "}";
                        FrameCodec.Write(stream, new Frame(header, null));
                        stream.Flush();

                        List<Frame> got = new List<Frame>();
                        Assert.True(TestWait.Until(delegate
                        {
                            got.AddRange(h.Queue.DrainAll());
                            return got.Count >= 1;
                        }, 5000), "no frame on round " + round);
                        Assert.Equal(header, got[0].Header);
                    }

                    Assert.True(TestWait.Until(
                        delegate { return h.Server.Status == "client disconnected"; }, 5000),
                        "the server did not go back to listening after round " + round);
                }
            }
        }

        [Fact]
        public void InvalidLengthClosesTheConnectionButNotTheServer()
        {
            using (Harness h = new Harness())
            {
                TcpClient bad = h.ConnectAndWait();
                NetworkStream stream = bad.GetStream();

                // header length prefix of 0xFFFFFFFF: well beyond
                // MaxHeaderBytes. Terminal error, the stream can no longer
                // be realigned and the socket must be closed (DESIGN 5.1).
                stream.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 4);
                stream.Flush();

                Assert.True(TestWait.Until(
                    delegate { return h.Server.Status.StartsWith("stream desynchronized"); }, 5000),
                    "status after the invalid length: " + h.Server.Status);
                Assert.True(SocketIsClosed(stream), "the server did not close the socket");
                bad.Close();

                // the listener must still be alive for the next client
                using (TcpClient good = h.ConnectAndWait())
                {
                    NetworkStream ok = good.GetStream();
                    FrameCodec.Write(ok, new Frame(Hello, null));
                    ok.Flush();
                    Assert.True(TestWait.Until(
                        delegate { return h.Queue.DrainAll().Count >= 1; }, 5000),
                        "the server no longer accepts connections after the terminal error");
                }
            }
        }

        [Fact]
        public void StopFreesThePortEvenWithAClientConnected()
        {
            int port;
            using (Harness h = new Harness())
            {
                port = h.Server.Port;
                using (TcpClient client = h.ConnectAndWait())
                {
                    NetworkStream stream = client.GetStream();
                    FrameCodec.Write(stream, new Frame(Hello, null));
                    stream.Flush();
                    Assert.True(TestWait.Until(delegate { return h.Raiser.Count >= 1; }, 5000));

                    // Stop with the connection still open: this is the real
                    // case, Revit closes with Blender connected.
                    h.Server.Stop();
                    Assert.False(h.Server.IsRunning);
                    Assert.Equal("stopped", h.Server.Status);
                }
            }

            // The check that in the plan is done by hand with netstat: if the
            // port stays occupied, the listener does not start on Revit's restart.
            TcpListener rebind = new TcpListener(IPAddress.Loopback, port);
            rebind.Start();
            rebind.Stop();
        }

        [Fact]
        public void FailedStartLeavesTheServerRestartable()
        {
            TcpListener blocker = new TcpListener(IPAddress.Loopback, 0);
            blocker.Start();
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;

            BridgeServer server = new BridgeServer(new MessageQueue(), new FakeRaiser(), port);
            try
            {
                Assert.Throws<SocketException>(delegate { server.Start(); });

                // The point of the test: after a failed bind the status must
                // not remain "running", otherwise every subsequent Start
                // would be a silent no-op and the bridge would stay dead.
                Assert.False(server.IsRunning);
                Assert.Equal("stopped", server.Status);

                blocker.Stop();
                blocker = null;

                server.Start();
                Assert.True(server.IsRunning);
                Assert.Equal(port, server.Port);
            }
            finally
            {
                server.Stop();
                if (blocker != null) { blocker.Stop(); }
            }
        }

        [Fact]
        public void SendWithoutClientDoesNotBlowUp()
        {
            using (Harness h = new Harness())
            {
                Assert.False(h.Server.Send(new Frame("{\"type\":\"hello_ack\"}", null)));
                Assert.Contains("no client connected", h.Server.Status);
                Assert.True(h.Server.IsRunning);
            }
        }

        [Fact]
        public void SendingAFrameOverTheLimitDoesNotCloseTheConnection()
        {
            using (Harness h = new Harness())
            using (TcpClient client = h.ConnectAndWait())
            {
                // FrameCodec validates the lengths before touching the
                // stream: not a byte goes out here, so the case is
                // recoverable and the connection must stay up.
                string tooBig = new string('x', BridgeConstants.MaxHeaderBytes + 1);
                Assert.False(h.Server.Send(new Frame(tooBig, null)));
                Assert.Contains("invalid frame", h.Server.Status);

                string ack = "{\"type\":\"hello_ack\"}";
                Assert.True(h.Server.Send(new Frame(ack, null)));

                Frame reply = FrameCodec.Read(client.GetStream());
                Assert.NotNull(reply);
                Assert.Equal(ack, reply.Header);
            }
        }

        [Fact]
        public void StopIsIdempotentAndStartingTwiceDoesNotOpenTwoListeners()
        {
            Harness h = new Harness();
            try
            {
                int port = h.Server.Port;
                h.Server.Start();
                Assert.Equal(port, h.Server.Port);
            }
            finally
            {
                h.Server.Stop();
                h.Server.Stop();
            }
            Assert.False(h.Server.IsRunning);
        }

        // A single timed read: ReadByte stays blocked until the peer closes,
        // so it makes no sense to put it in a polling loop.
        private static bool SocketIsClosed(NetworkStream stream)
        {
            stream.ReadTimeout = 5000;
            try
            {
                return stream.ReadByte() < 0;
            }
            catch (IOException ex)
            {
                SocketException inner = ex.InnerException as SocketException;
                if (inner != null && inner.SocketErrorCode == SocketError.TimedOut)
                {
                    return false;
                }
                // connection reset: closed either way
                return true;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
