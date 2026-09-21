// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Bilocus.Protocol;

namespace Bilocus.Revit.Net.Tests
{
    // Counter standing in for ExternalEvent. It is the reason BridgeServer
    // takes an IEventRaiser instead of the real ExternalEvent: ExternalEvent
    // is not instantiable outside Revit, and without a substitute this
    // server would only be verifiable by opening Revit by hand.
    public sealed class FakeRaiser : IEventRaiser
    {
        private int _count;

        public int Count { get { return Volatile.Read(ref _count); } }

        public void Raise() { Interlocked.Increment(ref _count); }
    }

    // Server on an ephemeral port: the production 9877 could be taken by an
    // open Revit on the machine running the tests.
    public sealed class Harness : IDisposable
    {
        public readonly MessageQueue Queue = new MessageQueue();
        public readonly FakeRaiser Raiser = new FakeRaiser();
        public readonly BridgeServer Server;

        public Harness()
        {
            Server = new BridgeServer(Queue, Raiser, 0);
            Server.Start();
        }

        public TcpClient Connect()
        {
            TcpClient client = new TcpClient();
            client.Connect(IPAddress.Parse(BridgeConstants.DefaultHost), Server.Port);
            client.NoDelay = true;
            return client;
        }

        // The client is connected as soon as Connect returns, but the socket
        // thread might not have done the accept yet: without this wait, Send
        // would write to a stream that is still null.
        public TcpClient ConnectAndWait()
        {
            TcpClient client = Connect();
            if (!TestWait.Until(delegate { return Server.Status == "connected"; }, 5000))
            {
                client.Close();
                throw new TimeoutException("the server did not accept the connection: " + Server.Status);
            }
            return client;
        }

        public void Dispose() { Server.Stop(); }
    }

    public static class TestWait
    {
        public static bool Until(Func<bool> condition, int timeoutMs)
        {
            int waited = 0;
            while (waited < timeoutMs)
            {
                if (condition()) return true;
                Thread.Sleep(10);
                waited += 10;
            }
            return condition();
        }
    }
}
