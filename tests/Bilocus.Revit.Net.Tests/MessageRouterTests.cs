// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Bilocus.Protocol;
using Bilocus.Revit.Bake;
using Bilocus.Revit.Net;
using Bilocus.Revit.Preview;
using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    public class MessageRouterTests
    {
        private static readonly HostInfo Host = new HostInfo("2025", "Project1");

        // Collects the router's replies instead of writing them to a socket:
        // this is exactly why Handle takes a SendFrame instead of knowing
        // about the BridgeServer.
        private sealed class Sink
        {
            public readonly List<Frame> Sent = new List<Frame>();
            public bool Result = true;

            public bool Send(Frame frame)
            {
                Sent.Add(frame);
                return Result;
            }

            public JsonElement HeaderOf(int index)
            {
                return JsonDocument.Parse(Sent[index].Header).RootElement;
            }

            public string TypeOf(int index)
            {
                return HeaderOf(index).GetProperty("type").GetString();
            }
        }

        private static byte[] MeshPayloadBytes(float[] positions, float[] normals, uint[] indices)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                foreach (float value in positions) { w.Write(value); }
                foreach (float value in normals) { w.Write(value); }
                foreach (uint value in indices) { w.Write(value); }
                w.Flush();
                return ms.ToArray();
            }
        }

        // A triangle: 3 vertices, 3 indices.
        private static byte[] OneTrianglePayload()
        {
            return MeshPayloadBytes(
                new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                new uint[] { 0, 1, 2 });
        }

        private static string GeometryHeader(string objId)
        {
            return "{\"type\":\"geometry\",\"obj_id\":\"" + objId + "\",\"name\":\"Cube\","
                + "\"vert_count\":3,\"tri_count\":1,"
                + "\"color\":[1,0.5,0,1],"
                + "\"matrix\":[1,0,0,2, 0,1,0,0, 0,0,1,0, 0,0,0,1]}";
        }

        private static void SendGeometry(MessageRouter router, Sink sink, string objId)
        {
            router.Handle(new Frame(GeometryHeader(objId), OneTrianglePayload()), sink.Send, Host);
        }

        // ---- hello ----

        [Fact]
        public void Hello_RepliesHelloAck()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("{\"type\":\"hello\",\"protocol_version\":1}", null), sink.Send, Host);

            Assert.Single(sink.Sent);
            JsonElement header = sink.HeaderOf(0);
            Assert.Equal("hello_ack", header.GetProperty("type").GetString());
            Assert.Equal(BridgeConstants.ProtocolVersion, header.GetProperty("protocol_version").GetInt32());
            Assert.Equal("2025", header.GetProperty("revit_version").GetString());
            Assert.Equal("Project1", header.GetProperty("doc_title").GetString());
            Assert.Empty(sink.Sent[0].Payload);
        }

        // Regression on the Phase 0 shortcut: Python's json.dumps emits
        // {"type": "hello", ...} WITH a space after the colon. A dispatch
        // done by searching for the substring "\"type\":\"hello\"" would have
        // broken the handshake without any test noticing. This header is
        // copied verbatim from the output of json.dumps.
        [Fact]
        public void Hello_WithPythonJsonSpacing_StillRepliesHelloAck()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame("{\"type\": \"hello\", \"protocol_version\": 1, \"client\": \"blender\"}", null),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("hello_ack", sink.TypeOf(0));
        }

        // The echo of a hello_ack must not trigger a second handshake: it
        // was already true with the raw match of Phase 0, it must stay true.
        [Fact]
        public void HelloAck_IsNotTreatedAsHello()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("{\"type\":\"hello_ack\",\"protocol_version\":1}", null), sink.Send, Host);

            Assert.Empty(sink.Sent);
        }

        // The document title comes from the filesystem: it is not under our
        // control and can contain quotes or non-ASCII characters.
        [Fact]
        public void HelloAck_EscapesDocumentTitle()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();
            HostInfo awkward = new HostInfo("2024", "Cas\"a \u00e0 Mare\\rev");

            router.Handle(new Frame("{\"type\":\"hello\"}", null), sink.Send, awkward);

            Assert.Single(sink.Sent);
            Assert.Equal("Cas\"a \u00e0 Mare\\rev", sink.HeaderOf(0).GetProperty("doc_title").GetString());
        }

        // A Blender add-on from another release: the handshake is refused
        // with a readable reason instead of acked, otherwise the mismatch
        // would surface later as puzzling content errors.
        [Fact]
        public void Hello_WithDifferentProtocolVersion_RepliesErrorNotAck()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            int other = BridgeConstants.ProtocolVersion + 1;
            router.Handle(
                new Frame("{\"type\":\"hello\",\"protocol_version\":" + other + "}", null), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Contains("protocol version", sink.HeaderOf(0).GetProperty("message").GetString());
            Assert.Equal(1, router.ContentErrorCount);
        }

        [Fact]
        public void Hello_WithNonIntegerProtocolVersion_RepliesError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("{\"type\":\"hello\",\"protocol_version\":\"1\"}", null), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
        }

        // ---- geometry ----

        [Fact]
        public void Geometry_PopulatesStoreWithChunks()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");

            Assert.Empty(sink.Sent);
            Assert.Single(store.Objects);
            StoredObject item = store.Objects[0];
            Assert.Equal("obj-1", item.ObjectId);
            Assert.Equal("Cube", item.Name);
            Assert.Single(item.Chunks);
            Assert.Equal(3, item.Chunks[0].VertexCount);
            Assert.Equal(1, item.Chunks[0].TriangleCount);
            Assert.Equal(1f, item.Chunks[0].Positions[3]);
            Assert.Equal(2f, item.Matrix[3]);
            Assert.Equal(0.5f, item.Color[1]);
        }

        [Fact]
        public void Geometry_WithoutMatrixOrColor_KeepsTheStoredOnes()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            router.Handle(
                new Frame("{\"type\":\"geometry\",\"obj_id\":\"obj-1\",\"name\":\"Cube\","
                    + "\"vert_count\":3,\"tri_count\":1}", OneTrianglePayload()),
                sink.Send, Host);

            Assert.Empty(sink.Sent);
            Assert.Equal(2f, store.Objects[0].Matrix[3]);
            Assert.Equal(0.5f, store.Objects[0].Color[1]);
        }

        // ---- transform ----

        [Fact]
        public void Transform_ChangesMatrixWithoutTouchingChunks()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            List<Bilocus.Geometry.MeshChunk> before = store.Objects[0].Chunks;

            router.Handle(
                new Frame("{\"type\":\"transform\",\"obj_id\":\"obj-1\","
                    + "\"matrix\":[1,0,0,7, 0,1,0,0, 0,0,1,0, 0,0,0,1]}", null),
                sink.Send, Host);

            Assert.Empty(sink.Sent);
            Assert.Same(before, store.Objects[0].Chunks);
            Assert.Equal(7f, store.Objects[0].Matrix[3]);
        }

        [Fact]
        public void Transform_OnUnknownObject_IsIgnoredWithoutError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame("{\"type\":\"transform\",\"obj_id\":\"ghost\","
                    + "\"matrix\":[1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1]}", null),
                sink.Send, Host);

            Assert.Empty(sink.Sent);
            Assert.Empty(store.Objects);
        }

        // ---- remove / clear ----

        [Fact]
        public void Remove_TakesTheObjectOut()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            SendGeometry(router, sink, "obj-2");
            router.Handle(new Frame("{\"type\":\"remove\",\"obj_id\":\"obj-1\"}", null), sink.Send, Host);

            Assert.Single(store.Objects);
            Assert.Equal("obj-2", store.Objects[0].ObjectId);
        }

        [Fact]
        public void Clear_EmptiesTheStore()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            SendGeometry(router, sink, "obj-2");
            router.Handle(new Frame("{\"type\":\"clear\"}", null), sink.Send, Host);

            Assert.Empty(store.Objects);
        }

        // ---- preview_style ----

        [Fact]
        public void PreviewStyle_SetsTheColorsAndBumpsTheRevision()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();
            long before = store.Revision;

            router.Handle(new Frame(
                "{\"type\":\"preview_style\",\"face\":[0.2,0.3,0.4],\"edge\":[0.9,0.8,0.7]}", null),
                sink.Send, Host);

            Assert.Empty(sink.Sent);
            Assert.Equal(new float[] { 0.2f, 0.3f, 0.4f }, store.FaceColor);
            Assert.Equal(new float[] { 0.9f, 0.8f, 0.7f }, store.EdgeColor);
            Assert.True(store.Revision > before);
            Assert.Equal(store.Revision, store.StyleRevision);
        }

        // Blender resends the style at every Connect and Sync: the same
        // colors must not invalidate the GPU buffers again.
        [Fact]
        public void PreviewStyle_Unchanged_DoesNotBumpTheRevision()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();
            string header = "{\"type\":\"preview_style\",\"face\":[0.2,0.3,0.4],\"edge\":[0.9,0.8,0.7]}";

            router.Handle(new Frame(header, null), sink.Send, Host);
            long after = store.Revision;
            router.Handle(new Frame(header, null), sink.Send, Host);

            Assert.Equal(after, store.Revision);
        }

        [Theory]
        [InlineData("{\"type\":\"preview_style\",\"face\":[0.2,0.3],\"edge\":[0.9,0.8,0.7]}")]
        [InlineData("{\"type\":\"preview_style\",\"face\":[0.2,0.3,0.4]}")]
        [InlineData("{\"type\":\"preview_style\",\"face\":[0.2,0.3,1.5],\"edge\":[0.9,0.8,0.7]}")]
        [InlineData("{\"type\":\"preview_style\",\"face\":[0.2,0.3,0.4],\"edge\":[-0.1,0.8,0.7]}")]
        public void PreviewStyle_Invalid_IsRejectedAndKeepsTheDefaults(string header)
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame(header, null), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Equal(GeometryStore.DefaultFaceColor, store.FaceColor);
            Assert.Equal(GeometryStore.DefaultEdgeColor, store.EdgeColor);
            Assert.Equal(0, store.StyleRevision);
        }

        // ---- sync ----

        [Fact]
        public void SyncBeginThenEnd_DropsTheObjectsNotAnnounced()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            SendGeometry(router, sink, "obj-2");

            router.Handle(
                new Frame("{\"type\":\"sync_begin\",\"obj_ids\":[\"obj-2\",\"obj-3\"]}", null),
                sink.Send, Host);
            SendGeometry(router, sink, "obj-3");
            router.Handle(new Frame("{\"type\":\"sync_end\"}", null), sink.Send, Host);

            Assert.Empty(sink.Sent);
            List<string> ids = new List<string>();
            foreach (StoredObject item in store.Objects) { ids.Add(item.ObjectId); }
            ids.Sort();
            Assert.Equal(new List<string> { "obj-2", "obj-3" }, ids);
        }

        [Fact]
        public void SyncBegin_WithEmptyList_ThenEnd_EmptiesTheStore()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            router.Handle(new Frame("{\"type\":\"sync_begin\",\"obj_ids\":[]}", null), sink.Send, Host);
            router.Handle(new Frame("{\"type\":\"sync_end\"}", null), sink.Send, Host);

            Assert.Empty(store.Objects);
        }

        // ---- unknown type ----

        [Fact]
        public void UnknownType_IsIgnoredWithoutThrowingAndWithoutError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("{\"type\":\"teleport\",\"obj_id\":\"x\"}", null), sink.Send, Host);

            Assert.Empty(sink.Sent);
            Assert.Empty(store.Objects);
        }

        // ---- content errors: error frame and moving on ----
        // DESIGN 5.1, last line: framing intact, the stream is still
        // aligned. Closing the socket here would force a reconnect for a
        // single malformed object.

        [Fact]
        public void PayloadInconsistentWithHeaderCounts_SendsErrorAndKeepsGoing()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            // header declares 3 vertices, the payload carries 2
            byte[] tooShort = MeshPayloadBytes(
                new float[] { 0, 0, 0, 1, 0, 0 },
                new float[] { 0, 0, 1, 0, 0, 1 },
                new uint[] { 0, 1, 2 });

            router.Handle(new Frame(GeometryHeader("obj-1"), tooShort), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.False(string.IsNullOrEmpty(sink.HeaderOf(0).GetProperty("message").GetString()));
            Assert.Empty(store.Objects);

            // the router is still alive: the next message goes through
            SendGeometry(router, sink, "obj-2");
            Assert.Single(store.Objects);
            Assert.Single(sink.Sent);
        }

        [Fact]
        public void MalformedJsonHeader_SendsErrorAndKeepsGoing()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("{\"type\":\"geometry\", this is not json", null), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));

            router.Handle(new Frame("{\"type\":\"hello\"}", null), sink.Send, Host);
            Assert.Equal(2, sink.Sent.Count);
            Assert.Equal("hello_ack", sink.TypeOf(1));
        }

        [Fact]
        public void GeometryWithoutObjId_SendsError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame("{\"type\":\"geometry\",\"vert_count\":3,\"tri_count\":1}", OneTrianglePayload()),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Empty(store.Objects);
        }

        [Fact]
        public void HeaderWithoutType_SendsError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("{\"obj_id\":\"x\"}", null), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
        }

        [Fact]
        public void HeaderThatIsNotAnObject_SendsError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame("[1,2,3]", null), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
        }

        [Fact]
        public void TransformWithMatrixOfWrongLength_SendsError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            SendGeometry(router, sink, "obj-1");
            router.Handle(
                new Frame("{\"type\":\"transform\",\"obj_id\":\"obj-1\",\"matrix\":[1,0,0]}", null),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Equal(2f, store.Objects[0].Matrix[3]);
        }

        [Fact]
        public void GeometryWithIndexOutOfRange_SendsError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            byte[] payload = MeshPayloadBytes(
                new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                new uint[] { 0, 1, 99 });

            router.Handle(new Frame(GeometryHeader("obj-1"), payload), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Empty(store.Objects);
        }

        // If the socket has already dropped, Send returns false: that is no
        // reason to abort draining the queue.
        [Fact]
        public void SendThatFails_DoesNotThrow()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();
            sink.Result = false;

            router.Handle(new Frame("{\"type\":\"hello\"}", null), sink.Send, Host);
            router.Handle(new Frame("{\"type\":\"not json", null), sink.Send, Host);

            Assert.Equal(2, sink.Sent.Count);
        }

        [Fact]
        public void NullSend_IsTolerated()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);

            router.Handle(new Frame("{\"type\":\"hello\"}", null), null, Host);
            router.Handle(new Frame("{\"type\":\"not json", null), null, Host);
        }

        // ---- proxy_edges ----
        //
        // The first message that makes the bridge write into the Revit
        // document. The router does NOT execute it: opening a transaction
        // requires the Revit API, which this file does not have and must
        // not have. What the router must do is get as far as a valid
        // request and stop there, and what follows is exactly what can be
        // verified without opening Revit.

        private static byte[] EdgePayload(params float[] values)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                foreach (float value in values) { w.Write(value); }
                w.Flush();
                return ms.ToArray();
            }
        }

        private static string ProxyHeader(string objId, int edgeCount)
        {
            return "{\"type\":\"proxy_edges\",\"obj_id\":\"" + objId + "\",\"name\":\"Cube\","
                + "\"edge_count\":" + edgeCount + "}";
        }

        [Fact]
        public void ProxyEdges_QueuesTheRequestWithoutReplying()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 1), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            // No reply: the creation has not happened yet, and replying
            // "done" before actually doing it would be a lie.
            Assert.Empty(sink.Sent);
            Assert.True(router.HasPendingProxies);

            List<ProxyEdgeRequest> pending = router.TakePendingProxies();
            Assert.Single(pending);
            Assert.Equal("obj-1", pending[0].ObjectId);
            Assert.Equal("Cube", pending[0].Name);
            Assert.Equal(1, pending[0].EdgeCount);
            Assert.Equal(new float[] { 1f, 0f, 0f }, pending[0].EndOf(0));
        }

        // Taking the requests consumes them: a second drain must not create
        // the same proxies a second time.
        [Fact]
        public void TakePendingProxies_EmptiesTheList()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 1), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Single(router.TakePendingProxies());
            Assert.False(router.HasPendingProxies);
            Assert.Empty(router.TakePendingProxies());
        }

        [Fact]
        public void ProxyEdges_ForDifferentObjects_QueuesBoth()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 1), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);
            router.Handle(
                new Frame(ProxyHeader("obj-2", 1), EdgePayload(0, 0, 0, 0, 1, 0)),
                sink.Send, Host);

            List<ProxyEdgeRequest> pending = router.TakePendingProxies();
            Assert.Equal(2, pending.Count);
            Assert.Equal("obj-1", pending[0].ObjectId);
            Assert.Equal("obj-2", pending[1].ObjectId);
        }

        // Two requests for the same object in the same drain: the second
        // would replace the first anyway, because replacement is per
        // obj_id. Executing both would mean creating elements only to
        // delete them an instant later, and two entries in the undo
        // history for a single result.
        [Fact]
        public void ProxyEdges_ForTheSameObject_KeepsOnlyTheLast()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 1), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);
            router.Handle(
                new Frame(ProxyHeader("obj-1", 2),
                    EdgePayload(0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 1)),
                sink.Send, Host);

            List<ProxyEdgeRequest> pending = router.TakePendingProxies();
            Assert.Single(pending);
            Assert.Equal(2, pending[0].EdgeCount);
        }

        [Fact]
        public void ProxyEdges_WithPayloadThatDoesNotMatchTheHeader_RepliesError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 2), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Contains("proxy_edges", sink.HeaderOf(0).GetProperty("message").GetString());

            // Nothing pending: a rejected message must not leave a half
            // request that someone executes later.
            Assert.False(router.HasPendingProxies);
        }

        [Fact]
        public void ProxyEdges_WithNonFiniteCoordinate_RepliesError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 1),
                    EdgePayload(0, 0, 0, float.NaN, 0, 0)),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.False(router.HasPendingProxies);
        }

        [Fact]
        public void ProxyEdges_WithoutEdges_RepliesError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame(ProxyHeader("obj-1", 0), new byte[0]), sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.False(router.HasPendingProxies);
        }

        [Fact]
        public void ProxyEdges_WithoutEdgeCount_RepliesError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame("{\"type\":\"proxy_edges\",\"obj_id\":\"obj-1\"}",
                    EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Contains("edge_count", sink.HeaderOf(0).GetProperty("message").GetString());
            Assert.False(router.HasPendingProxies);
        }

        [Fact]
        public void ProxyEdges_WithoutObjectId_RepliesError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame("{\"type\":\"proxy_edges\",\"edge_count\":1}",
                    EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.False(router.HasPendingProxies);
        }

        // The name is a label: without it, the obj_id remains, which still
        // says in the summary which object is being talked about.
        [Fact]
        public void ProxyEdges_WithoutName_FallsBackToObjectId()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame("{\"type\":\"proxy_edges\",\"obj_id\":\"obj-1\",\"edge_count\":1}",
                    EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Empty(sink.Sent);
            Assert.Equal("obj-1", router.TakePendingProxies()[0].Name);
        }

        // A rejected message must not interrupt the bridge: the next one
        // must be queued normally.
        [Fact]
        public void ProxyEdges_AfterARejectedOne_StillQueuesTheNext()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(new Frame(ProxyHeader("obj-1", 0), new byte[0]), sink.Send, Host);
            router.Handle(
                new Frame(ProxyHeader("obj-2", 1), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Single(sink.Sent);
            Assert.Equal("error", sink.TypeOf(0));
            Assert.Single(router.TakePendingProxies());
            Assert.Equal(1, router.ContentErrorCount);
        }

        // The message with the payload is not the only one that goes
        // through: a geometry frame and a proxy frame in the same drain
        // must not get mixed up, they are two separate paths.
        [Fact]
        public void ProxyEdges_DoesNotTouchTheGeometryStore()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            router.Handle(
                new Frame(ProxyHeader("obj-1", 1), EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.Empty(store.Objects);
        }

        // ---- bake (Phase B) ----
        //
        // The bake is a batch: bake_begin announces the objects, one
        // bake_mesh per object, bake_end closes it and puts it up for
        // execution. As with proxy_edges the router writes nothing into the
        // document: it gets as far as a verified batch and leaves it pending
        // for MessageHandler. What follows is everything about the bake
        // that can be verified without opening Revit.

        private static string JsonIds(params string[] ids)
        {
            string json = "[";
            for (int i = 0; i < ids.Length; i++)
            {
                if (i > 0) { json = json + ", "; }
                json = json + "\"" + ids[i] + "\"";
            }
            return json + "]";
        }

        private static void Send(MessageRouter router, Sink sink, string header, byte[] payload)
        {
            router.Handle(new Frame(header, payload), sink.Send, Host);
        }

        private static void BakeBegin(MessageRouter router, Sink sink, params string[] ids)
        {
            Send(router, sink, "{\"type\": \"bake_begin\", \"obj_ids\": " + JsonIds(ids) + "}", null);
        }

        private static void BakeMesh(MessageRouter router, Sink sink, string objId)
        {
            Send(router, sink, BakeGolden.MeshHeader(objId), BakeGolden.Payload());
        }

        private static void BakeEnd(MessageRouter router, Sink sink)
        {
            Send(router, sink, "{\"type\": \"bake_end\"}", null);
        }

        private static string ErrorMessageOf(Sink sink, int index)
        {
            Assert.Equal("error", sink.TypeOf(index));
            return sink.HeaderOf(index).GetProperty("message").GetString();
        }

        [Fact]
        public void Bake_CompleteBatch_QueuesOneBatchWithTheGoldenData()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-1");
            BakeEnd(router, sink);

            // No reply: the bake has not happened yet, and saying "done"
            // before actually doing it would be a lie. bake_result is sent
            // by MessageHandler, after the transaction.
            Assert.Empty(sink.Sent);
            Assert.False(router.HasOpenBakeBatch);
            Assert.True(router.HasPendingBakes);

            List<BakeBatch> batches = router.TakePendingBakes();
            Assert.Single(batches);
            Assert.Empty(batches[0].MissingIds);

            List<BakeMeshRequest> requests = batches[0].Requests;
            Assert.Single(requests);
            BakeMeshRequest request = requests[0];
            Assert.Equal("obj-1", request.ObjectId);
            Assert.Equal("Cube", request.Name);
            Assert.Equal("OST_GenericModel", request.Category);
            Assert.Equal(new float[] { 1, 0, 0, 2, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 }, request.Matrix);
            Assert.Equal(new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f, 0.5f, 0.5f, 1f },
                request.Mesh.Positions);
            Assert.Equal(new int[] { 4, 3 }, request.Mesh.FaceSizes);
            Assert.Equal(new int[] { 0, 1, 2, 3, 0, 1, 4 }, request.Mesh.FaceVertices);
            Assert.Equal(new int[] { 0, 1, 2, 0, 2, 3, 0, 1, 4 }, request.Mesh.TriVertices);
            Assert.Equal(new int[] { 0, 0, 1 }, request.Mesh.TriFaces);
        }

        // Taking the batches consumes them: a second drain must not bake
        // the same objects a second time.
        [Fact]
        public void TakePendingBakes_EmptiesTheList()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-1");
            BakeEnd(router, sink);

            Assert.Single(router.TakePendingBakes());
            Assert.False(router.HasPendingBakes);
            Assert.Empty(router.TakePendingBakes());
        }

        [Fact]
        public void BakeMesh_WithoutBakeBegin_RepliesErrorAndQueuesNothing()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeMesh(router, sink, "obj-1");

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_mesh:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasOpenBakeBatch);
            Assert.False(router.HasPendingBakes);
        }

        // An object not announced gets rejected, but the batch stays open:
        // the announced objects can still arrive, and whatever does not
        // arrive counts as missing at closing time.
        [Fact]
        public void BakeMesh_ForAnIdNotAnnounced_RepliesErrorAndKeepsTheBatchOpen()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-2");

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.Contains("not announced", message);
            Assert.True(router.HasOpenBakeBatch);

            BakeEnd(router, sink);
            BakeBatch batch = router.TakePendingBakes()[0];
            Assert.Empty(batch.Requests);
            Assert.Equal(new List<string> { "obj-1" }, batch.MissingIds);
        }

        [Fact]
        public void BakeEnd_WithoutBakeBegin_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeEnd(router, sink);

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_end:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasPendingBakes);
        }

        // The batch left open by a disconnection: nothing has been written
        // yet, so the next bake_begin discards it without harm. But it
        // counts it, because a rising counter in the Status button is the
        // only sign of a Blender side that does not close its batches.
        [Fact]
        public void SecondBakeBegin_DiscardsTheOpenBatchAndCountsIt()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-1");
            BakeBegin(router, sink, "obj-2");
            BakeMesh(router, sink, "obj-2");
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            Assert.Equal(1, router.AbandonedBakeBatches);

            List<BakeBatch> batches = router.TakePendingBakes();
            Assert.Single(batches);
            Assert.Equal(new List<string> { "obj-2" }, batches[0].AnnouncedIds);
            Assert.Single(batches[0].Requests);
            Assert.Equal("obj-2", batches[0].Requests[0].ObjectId);
        }

        // Even a rejected bake_begin closes the previous batch: whoever
        // sent it has already stopped caring about that one, and leaving
        // it open would mix the bake_mesh messages of the two attempts.
        [Fact]
        public void RejectedBakeBegin_StillDiscardsTheOpenBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeBegin(router, sink);

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_begin:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasOpenBakeBatch);
            Assert.Equal(1, router.AbandonedBakeBatches);
        }

        // ---- bake together: host ----

        [Fact]
        public void BakeBegin_WithHost_QueuesATogetherBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink,
                "{\"type\": \"bake_begin\", \"obj_ids\": [\"obj-1\", \"obj-2\"], \"target\": \"family\", \"host\": \"obj-2\"}",
                null);
            BakeMesh(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-2");
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            List<BakeBatch> pending = router.TakePendingBakes();
            Assert.Single(pending);
            Assert.Equal("obj-2", pending[0].Host);
            Assert.True(pending[0].Together);
        }

        [Theory]
        [InlineData("{\"type\": \"bake_begin\", \"obj_ids\": [\"obj-1\", \"obj-2\"], \"host\": \"obj-2\"}")]
        [InlineData("{\"type\": \"bake_begin\", \"obj_ids\": [\"obj-1\", \"obj-2\"], \"target\": \"family\", \"host\": \"obj-3\"}")]
        [InlineData("{\"type\": \"bake_begin\", \"obj_ids\": [\"obj-1\", \"obj-2\"], \"target\": \"family\", \"host\": 2}")]
        public void BakeBegin_WithAnInvalidHost_RepliesError(string header)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink, header, null);

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_begin:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasOpenBakeBatch);
        }

        [Fact]
        public void BakeBegin_WithAnEmptyList_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink);

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_begin:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasOpenBakeBatch);
            Assert.Equal(0, router.AbandonedBakeBatches);
        }

        [Fact]
        public void BakeBegin_WithDuplicateIds_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1", "obj-1");

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_begin:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasOpenBakeBatch);
        }

        [Fact]
        public void BakeBegin_OverTheCap_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();
            string[] ids = new string[BakeBatch.MaxBakeObjects + 1];
            for (int i = 0; i < ids.Length; i++) { ids[i] = "obj-" + i; }

            BakeBegin(router, sink, ids);

            Assert.Single(sink.Sent);
            Assert.Contains("500", ErrorMessageOf(sink, 0));
            Assert.False(router.HasOpenBakeBatch);
        }

        [Fact]
        public void BakeBegin_WithoutObjIds_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink, "{\"type\": \"bake_begin\"}", null);

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_begin:", message);
            Assert.Contains("obj_ids", message);
        }

        [Fact]
        public void BakeMesh_WithAMalformedCategory_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeader("obj-1", "Walls", BakeGolden.MatrixJson), BakeGolden.Payload());

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.Contains("category", message);
        }

        [Fact]
        public void BakeMesh_WithoutCategory_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink,
                "{\"type\": \"bake_mesh\", \"obj_id\": \"obj-1\", \"name\": \"Cube\", "
                + "\"matrix\": " + BakeGolden.MatrixJson + ", "
                + "\"vert_count\": 5, \"face_count\": 2, \"loop_count\": 7, \"tri_count\": 3}",
                BakeGolden.Payload());

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.Contains("category", message);
        }

        // matrix is mandatory in the bake, unlike geometry: there is no
        // value already present to fall back to, and an inferred identity
        // would put the element in the wrong place without any error.
        [Fact]
        public void BakeMesh_WithoutMatrix_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink,
                "{\"type\": \"bake_mesh\", \"obj_id\": \"obj-1\", \"name\": \"Cube\", "
                + "\"category\": \"OST_Walls\", "
                + "\"vert_count\": 5, \"face_count\": 2, \"loop_count\": 7, \"tri_count\": 3}",
                BakeGolden.Payload());

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.Contains("matrix", message);
        }

        // JSON does not carry NaN, but 1e39 does not fit a float32 and
        // becomes infinite on cast: this is the real path by which a
        // non-finite value arrives.
        [Fact]
        public void BakeMesh_WithAMatrixOutOfFloatRange_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink,
                BakeGolden.MeshHeader("obj-1", "OST_Walls", "[1, 0, 0, 1e39, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]"),
                BakeGolden.Payload());

            Assert.Single(sink.Sent);
            Assert.Contains("matrix", ErrorMessageOf(sink, 0));
        }

        // Re-encoded, as for proxy_edges: the generic branch of Handle would
        // prefix "invalid geometry", which does not say which message.
        [Fact]
        public void BakeMesh_WithAPayloadThatDoesNotMatchTheHeader_SaysBakeMesh()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeader("obj-1"), new byte[140]);

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.DoesNotContain("invalid geometry", message);
            Assert.Equal(1, router.ContentErrorCount);
        }

        // A rejected bake_mesh does not close the batch and does not leave
        // a half request: the object ends up among the missing ones, and
        // the rest of the batch reaches Revit normally.
        [Fact]
        public void BakeMesh_Rejected_EndsUpMissing_AndTheOthersStillGetBaked()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1", "obj-2");
            Send(router, sink, BakeGolden.MeshHeader("obj-1"), new byte[3]);
            BakeMesh(router, sink, "obj-2");
            BakeEnd(router, sink);

            Assert.Single(sink.Sent);
            BakeBatch batch = router.TakePendingBakes()[0];
            Assert.Single(batch.Requests);
            Assert.Equal("obj-2", batch.Requests[0].ObjectId);
            Assert.Equal(new List<string> { "obj-1" }, batch.MissingIds);
        }

        [Fact]
        public void BakeMesh_SameIdTwiceInABatch_KeepsTheLast()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeader("obj-1", "OST_Walls", BakeGolden.MatrixJson),
                BakeGolden.Payload());
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            BakeBatch batch = router.TakePendingBakes()[0];
            Assert.Single(batch.Requests);
            Assert.Equal("OST_Walls", batch.Requests[0].Category);
        }

        // A batch closed with no object arrived must be executed all the
        // same: it is the only way for Blender to receive a bake_result
        // saying "all missing", instead of waiting for an outcome that
        // never comes.
        [Fact]
        public void BakeEnd_WithNothingArrived_StillQueuesTheBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1", "obj-2");
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            BakeBatch batch = router.TakePendingBakes()[0];
            Assert.Empty(batch.Requests);
            Assert.Equal(new List<string> { "obj-1", "obj-2" }, batch.MissingIds);
        }

        [Fact]
        public void BakeRemove_WithAnEmptyList_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink, "{\"type\": \"bake_remove\", \"obj_ids\": []}", null);

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_remove:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasPendingBakeRemovals);
        }

        [Fact]
        public void BakeRemove_WithoutObjIds_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink, "{\"type\": \"bake_remove\"}", null);

            Assert.Single(sink.Sent);
            Assert.StartsWith("bake_remove:", ErrorMessageOf(sink, 0));
            Assert.False(router.HasPendingBakeRemovals);
        }

        [Fact]
        public void BakeRemove_Valid_QueuesTheNormalizedIds()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink, "{\"type\": \"bake_remove\", \"obj_ids\": [\" obj-1 \", \"obj-2\"]}", null);

            Assert.Empty(sink.Sent);
            Assert.True(router.HasPendingBakeRemovals);
            List<List<string>> removals = router.TakePendingBakeRemovals();
            Assert.Single(removals);
            Assert.Equal(new List<string> { "obj-1", "obj-2" }, removals[0]);
        }

        [Fact]
        public void TakePendingBakeRemovals_EmptiesTheList()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            Send(router, sink, "{\"type\": \"bake_remove\", \"obj_ids\": [\"obj-1\"]}", null);

            Assert.Single(router.TakePendingBakeRemovals());
            Assert.False(router.HasPendingBakeRemovals);
            Assert.Empty(router.TakePendingBakeRemovals());
        }

        // bake_remove does not use the batch: it immediately becomes a
        // pending removal, and a bake open in the meantime stays as is.
        [Fact]
        public void BakeRemove_DoesNotTouchTheOpenBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink, "{\"type\": \"bake_remove\", \"obj_ids\": [\"obj-9\"]}", null);
            BakeMesh(router, sink, "obj-1");
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            Assert.Single(router.TakePendingBakeRemovals());
            Assert.Single(router.TakePendingBakes()[0].Requests);
            Assert.Equal(0, router.AbandonedBakeBatches);
        }

        // ---- bake as a family (Phase B2) ----
        //
        // The contract only changes the headers: target on bake_begin
        // (absent = directshape), accept_open on bake_mesh (absent = false).
        // A wrong type or value is a content error, like every other field.

        private static void BakeBeginWith(MessageRouter router, Sink sink, string targetJson, params string[] ids)
        {
            Send(router, sink,
                "{\"type\": \"bake_begin\", \"obj_ids\": " + JsonIds(ids) + ", \"target\": " + targetJson + "}",
                null);
        }

        // A Phase B Blender does not send target: its bake stays a
        // DirectShape bake, and no object ends up in a family by mistake.
        [Fact]
        public void BakeBegin_WithoutTarget_IsADirectShapeBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-1");
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            BakeBatch batch = router.TakePendingBakes()[0];
            Assert.Equal(BakeTarget.DirectShape, batch.Target);
            Assert.False(batch.Requests[0].AcceptOpen);
        }

        [Theory]
        [InlineData("\"directshape\"", "directshape")]
        [InlineData("\"family\"", "family")]
        public void BakeBegin_WithTarget_KeepsIt(string targetJson, string expected)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBeginWith(router, sink, targetJson, "obj-1");
            BakeMesh(router, sink, "obj-1");
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            Assert.Equal(expected, router.TakePendingBakes()[0].Target);
        }

        [Theory]
        [InlineData("\"all\"")]
        [InlineData("\"Family\"")]
        [InlineData("\"mass\"")]
        [InlineData("\"\"")]
        [InlineData("1")]
        [InlineData("true")]
        [InlineData("null")]
        [InlineData("[\"family\"]")]
        public void BakeBegin_WithABadTarget_RepliesErrorAndOpensNothing(string targetJson)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBeginWith(router, sink, targetJson, "obj-1");

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_begin:", message);
            Assert.Contains("target", message);
            Assert.False(router.HasOpenBakeBatch);
        }

        [Fact]
        public void BakeBegin_Family_OverTheFamilyCap_RepliesError()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();
            string[] ids = new string[BakeBatch.MaxFamilyBakeObjects + 1];
            for (int i = 0; i < ids.Length; i++) { ids[i] = "obj-" + i; }

            BakeBeginWith(router, sink, "\"family\"", ids);

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_begin:", message);
            Assert.Contains("50", message);
            Assert.False(router.HasOpenBakeBatch);
        }

        [Fact]
        public void BakeBegin_Family_AtTheFamilyCap_OpensTheBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();
            string[] ids = new string[BakeBatch.MaxFamilyBakeObjects];
            for (int i = 0; i < ids.Length; i++) { ids[i] = "obj-" + i; }

            BakeBeginWith(router, sink, "\"family\"", ids);

            Assert.Empty(sink.Sent);
            Assert.True(router.HasOpenBakeBatch);
        }

        [Theory]
        [InlineData("\"accept_open\": true", true)]
        [InlineData("\"accept_open\": false", false)]
        public void BakeMesh_WithAcceptOpen_KeepsIt(string field, bool expected)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBeginWith(router, sink, "\"family\"", "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1", field), BakeGolden.Payload());
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            Assert.Equal(expected, router.TakePendingBakes()[0].Requests[0].AcceptOpen);
        }

        // In a DirectShape batch accept_open is read (and validated) but
        // changes nothing: BakeBuilder ignores it. Sending it is not an error.
        [Fact]
        public void BakeMesh_WithAcceptOpen_IsAcceptedInADirectShapeBatch()
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1", "\"accept_open\": true"), BakeGolden.Payload());
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            Assert.Single(router.TakePendingBakes()[0].Requests);
        }

        // A string "true" or a 1 are not a boolean: reading them as true
        // would accept an open shell that nobody asked for.
        [Theory]
        [InlineData("\"accept_open\": \"true\"")]
        [InlineData("\"accept_open\": 1")]
        [InlineData("\"accept_open\": null")]
        public void BakeMesh_WithAcceptOpenOfTheWrongType_RepliesErrorAndEndsUpMissing(string field)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBeginWith(router, sink, "\"family\"", "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1", field), BakeGolden.Payload());

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.Contains("accept_open", message);

            BakeEnd(router, sink);
            Assert.Equal(new List<string> { "obj-1" }, router.TakePendingBakes()[0].MissingIds);
        }

        // smooth_mesh: same rules as accept_open, for the DirectShape bake.
        // Absent is false, which is also what a Blender add-on older than
        // the checkbox sends.
        [Theory]
        [InlineData("\"smooth_mesh\": true", true)]
        [InlineData("\"smooth_mesh\": false", false)]
        [InlineData("\"accept_open\": false", false)]
        public void BakeMesh_WithSmoothMesh_KeepsIt(string field, bool expected)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1", field), BakeGolden.Payload());
            BakeEnd(router, sink);

            Assert.Empty(sink.Sent);
            Assert.Equal(expected, router.TakePendingBakes()[0].Requests[0].SmoothMesh);
        }

        // A 1 read as true would throw away the volume of an object that
        // was meant to stay a solid.
        [Theory]
        [InlineData("\"smooth_mesh\": \"true\"")]
        [InlineData("\"smooth_mesh\": 1")]
        [InlineData("\"smooth_mesh\": null")]
        public void BakeMesh_WithSmoothMeshOfTheWrongType_RepliesErrorAndEndsUpMissing(string field)
        {
            MessageRouter router = new MessageRouter(new GeometryStore());
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            Send(router, sink, BakeGolden.MeshHeaderWith("obj-1", field), BakeGolden.Payload());

            Assert.Single(sink.Sent);
            string message = ErrorMessageOf(sink, 0);
            Assert.StartsWith("bake_mesh:", message);
            Assert.Contains("smooth_mesh", message);

            BakeEnd(router, sink);
            Assert.Equal(new List<string> { "obj-1" }, router.TakePendingBakes()[0].MissingIds);
        }

        // The bake and the preview are two separate paths: a baked object
        // does not enter the GeometryStore, and does not become a proxy
        // request.
        [Fact]
        public void Bake_DoesNotTouchTheGeometryStoreOrTheProxies()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            BakeBegin(router, sink, "obj-1");
            BakeMesh(router, sink, "obj-1");
            BakeEnd(router, sink);

            Assert.Empty(store.Objects);
            Assert.False(router.HasPendingProxies);
        }

        // ---- proxy_edges with arcs ----

        [Fact]
        public void ProxyEdges_WithArcCount_QueuesEdgesAndArcs()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            string header = "{\"type\":\"proxy_edges\",\"obj_id\":\"obj-1\",\"name\":\"Curve\","
                + "\"edge_count\":1,\"arc_count\":1}";
            router.Handle(
                new Frame(header, EdgePayload(
                    0, 0, 0, 1, 0, 0,
                    1, 0, 0, 0, 1, 0, 0.7f, 0.7f, 0)),
                sink.Send, Host);

            Assert.Empty(sink.Sent);
            List<ProxyEdgeRequest> pending = router.TakePendingProxies();
            Assert.Single(pending);
            Assert.Equal(1, pending[0].EdgeCount);
            Assert.Equal(1, pending[0].ArcCount);
        }

        [Fact]
        public void ProxyEdges_WithANonIntegerArcCount_IsAContentError()
        {
            GeometryStore store = new GeometryStore();
            MessageRouter router = new MessageRouter(store);
            Sink sink = new Sink();

            string header = "{\"type\":\"proxy_edges\",\"obj_id\":\"obj-1\","
                + "\"edge_count\":1,\"arc_count\":\"many\"}";
            router.Handle(
                new Frame(header, EdgePayload(0, 0, 0, 1, 0, 0)),
                sink.Send, Host);

            Assert.False(router.HasPendingProxies);
            Assert.Single(sink.Sent);
            Assert.Contains("arc_count", sink.HeaderOf(0).GetProperty("message").GetString());
        }
    }
}
