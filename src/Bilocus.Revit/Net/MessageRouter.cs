// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Bilocus.Geometry;
using Bilocus.Protocol;
using Bilocus.Revit.Bake;
using Bilocus.Revit.Preview;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Net
{
    // How the router replies. The return value is BridgeServer.Send's: false
    // if the frame did not go out. The router ignores it on purpose, see Reply.
    public delegate bool SendFrame(Frame frame);

    // The two hello_ack fields that only Revit knows. The router cannot work
    // them out on its own: this file does not touch the Revit API and must
    // not, otherwise it stops compiling in the test project.
    //
    // They are passed to every Handle instead of to the constructor because
    // the document title CHANGES: the user opens a different model, and the
    // router lives for the whole Revit session. A value frozen at startup
    // would reply with the wrong title, and it would be a bug discovered
    // late and badly.
    public sealed class HostInfo
    {
        public string RevitVersion { get; private set; }
        public string DocumentTitle { get; private set; }

        public HostInfo(string revitVersion, string documentTitle)
        {
            RevitVersion = revitVersion == null ? "" : revitVersion;
            DocumentTitle = documentTitle == null ? "" : documentTitle;
        }
    }

    // The Blender -> Revit message dispatch (DESIGN.md 5.3).
    //
    // This file must NOT contain any reference to the Revit API: it is
    // included as a linked source by the test project, which cannot load it.
    // The constraint is enforced by the compiler, not by discipline.
    //
    // It is not thread-safe and must not be: it runs on Revit's main thread,
    // inside MessageHandler.Execute, exactly like the GeometryStore it modifies.
    public sealed class MessageRouter
    {
        // A CONTENT error, not a framing one. DESIGN.md 5.1, last line: a
        // malformed header or inconsistent fields do not desynchronize
        // anything, the stream is still aligned to the frame boundary. The
        // correct response is an error frame and moving on. Closing the
        // socket here would force a reconnect for a single malformed object.
        private sealed class ContentException : Exception
        {
            public ContentException(string message) : base(message) { }
        }

        private readonly GeometryStore _store;

        // The proxy creation requests waiting to be executed.
        //
        // The router does NOT execute them, on purpose: creating elements
        // requires a transaction, the transaction requires the Revit API, and
        // this file must not touch it - it is compiled by the test project
        // without RevitAPI available, so the constraint is enforced by the
        // compiler. The router gets as far as it can on its own (reading the
        // header, verifying the payload, building a valid request) and stops
        // there. MessageHandler, which runs on the main thread and has the
        // API, takes them with TakePendingProxies after draining the queue.
        //
        // The separation also traces the boundary between the two kinds of
        // error: a malformed message is a CONTENT error and is exhausted
        // here, with an error frame and the bridge moving on (DESIGN.md 5.1);
        // a transaction failure is a Revit problem and arises on the other
        // side of the boundary, where a transaction exists to fail.
        private readonly List<ProxyEdgeRequest> _pendingProxies = new List<ProxyEdgeRequest>();

        // Last content error, for the Status button: without this, a Blender
        // sending bad headers only produces error frames that nobody sees
        // from the Revit side.
        public string LastContentError = "";
        public int ContentErrorCount;

        // ---- bake state, Phase B plan ----
        //
        // bake_begin / bake_mesh* / bake_end are a batch: objects are
        // collected into _openBakeBatch as they arrive, and only bake_end
        // passes it into _pendingBakes, from which MessageHandler takes it
        // and runs it in a TransactionGroup - a single Ctrl+Z for the whole
        // batch. bake_remove has no batch: it immediately becomes a pending
        // removal. The router writes nothing, for the same reason as
        // _pendingProxies.
        //
        // Bakes and removals sit in two separate lists, so the relative order
        // between a bake and a removal arrived in the SAME drain is not
        // preserved: MessageHandler decides it. These are two Blender buttons
        // pressed seconds apart, a drain lasts milliseconds.
        private BakeBatch _openBakeBatch;
        private readonly List<BakeBatch> _pendingBakes = new List<BakeBatch>();
        private readonly List<List<string>> _pendingBakeRemovals = new List<List<string>>();

        // Batches opened and never closed, discarded by a subsequent
        // bake_begin. For the Status button: discarding them costs nothing,
        // nothing had been written, but a rising counter is the only visible
        // sign of a Blender side that loses the connection mid-bake or does
        // not close its batches.
        public int AbandonedBakeBatches { get; private set; }

        public MessageRouter(GeometryStore store)
        {
            if (store == null) throw new ArgumentNullException("store");
            _store = store;
        }

        public void Handle(Frame frame, SendFrame send, HostInfo host)
        {
            if (frame == null) throw new ArgumentNullException("frame");
            if (host == null) throw new ArgumentNullException("host");

            try
            {
                Dispatch(frame, send, host);
            }
            catch (ContentException ex)
            {
                Fail(send, ex.Message);
            }
            catch (JsonException ex)
            {
                Fail(send, "invalid JSON header: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                // From MeshPayload.Parse (header counts inconsistent with the
                // payload bytes) and from MeshChunker.Split (index out of
                // range, arrays not multiples of 3). These are all defects of
                // the received message, not of this process's state.
                Fail(send, "invalid geometry: " + ex.Message);
            }
        }

        private void Dispatch(Frame frame, SendFrame send, HostInfo host)
        {
            using (JsonDocument doc = JsonDocument.Parse(frame.Header))
            {
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new ContentException("the header is not a JSON object");
                }

                string type = RequireString(root, "type");
                switch (type)
                {
                    case "hello":
                        Reply(send, BuildHelloAck(host));
                        break;

                    case "geometry":
                        HandleGeometry(root, frame.Payload);
                        break;

                    case "transform":
                        _store.SetTransform(
                            RequireString(root, "obj_id"),
                            RequireFloats(root, "matrix", 16));
                        break;

                    case "remove":
                        _store.Remove(RequireString(root, "obj_id"));
                        break;

                    case "clear":
                        _store.Clear();
                        break;

                    case "sync_begin":
                        _store.BeginSync(RequireStrings(root, "obj_ids"));
                        break;

                    case "sync_end":
                        _store.EndSync();
                        break;

                    case "proxy_edges":
                        HandleProxyEdges(root, frame.Payload);
                        break;

                    case "bake_begin":
                        HandleBakeBegin(root);
                        break;

                    case "bake_mesh":
                        HandleBakeMesh(root, frame.Payload);
                        break;

                    case "bake_end":
                        HandleBakeEnd();
                        break;

                    case "bake_remove":
                        HandleBakeRemove(root);
                        break;

                    default:
                        // Unknown type: silently ignored, no error frame.
                        // Echoes of our own messages (hello_ack, error) also
                        // end up here, and replying to an error with an error
                        // is the fastest way to build an infinite ping-pong.
                        break;
                }
            }
        }

        private void HandleGeometry(JsonElement root, byte[] payload)
        {
            string objId = RequireString(root, "obj_id");

            // The name is a label, not an identity: if missing, the obj_id
            // is a more useful fallback than an empty string in the Status
            // button.
            string name = OptionalString(root, "name", objId);

            int vertCount = RequireInt(root, "vert_count");
            int triCount = RequireInt(root, "tri_count");

            // matrix and color are optional: if absent, Upsert leaves the
            // values already present in the store untouched. Needed for the
            // path where geometry resyncs while the live transform is
            // already ahead: an absent matrix must not reset the object back
            // to the origin.
            float[] matrix = OptionalFloats(root, "matrix", 16);
            float[] color = OptionalFloats(root, "color", 4);

            MeshPayload mesh = MeshPayload.Parse(payload, vertCount, triCount);
            List<MeshChunk> chunks = MeshChunker.Split(mesh.Positions, mesh.Normals, mesh.Indices);

            _store.Upsert(objId, name, chunks, matrix, color);
        }

        // proxy_edges: DESIGN.md 5.3. edge_count * 6 float32, two endpoints
        // per edge, world coordinates in meters. No matrix and no origin,
        // unlike geometry: proxies are one-off creations that must end up
        // where they are seen, not objects the user will reposition.
        private void HandleProxyEdges(JsonElement root, byte[] payload)
        {
            string objId = RequireString(root, "obj_id");
            string name = OptionalString(root, "name", objId);
            int edgeCount = RequireInt(root, "edge_count");

            // Absent in Polyline mode: the message stays the one from Phase A3.
            int arcCount = OptionalInt(root, "arc_count", 0);

            ProxyEdgeRequest request;
            try
            {
                request = ProxyEdgeRequest.Parse(objId, name, edgeCount, arcCount, payload);
            }
            catch (ArgumentException ex)
            {
                // Re-encoded instead of letting it bubble up: the generic
                // branch of Handle would prefix "invalid geometry", which
                // would say the wrong thing here. Parse's message is already
                // precise.
                throw new ContentException("proxy_edges: " + ex.Message);
            }

            // Two proxy_edges for the same object in the same drain: the
            // second would replace the first anyway, because replacement is
            // per obj_id. Executing both would mean creating elements only to
            // delete them an instant later, and two entries in the undo
            // history where the final result is only one. The last one
            // arrived wins, which is the same rule already applied by
            // GeometryStore to duplicate obj_ids (DESIGN.md 5.3).
            for (int i = _pendingProxies.Count - 1; i >= 0; i--)
            {
                if (ProxyNaming.MatchesObjectId(_pendingProxies[i].ObjectId, request.ObjectId))
                {
                    _pendingProxies.RemoveAt(i);
                }
            }

            _pendingProxies.Add(request);
        }

        public bool HasPendingProxies
        {
            get { return _pendingProxies.Count > 0; }
        }

        // Returns the pending requests and empties the list: calling it twice
        // must not create the proxies twice.
        public List<ProxyEdgeRequest> TakePendingProxies()
        {
            List<ProxyEdgeRequest> taken = new List<ProxyEdgeRequest>(_pendingProxies);
            _pendingProxies.Clear();
            return taken;
        }

        // ---- bake ----
        //
        // All errors from the four branches come out with the message name
        // in front ("bake_mesh: ..."), missing fields included. For
        // proxy_edges it is enough to re-encode Parse; not here, because
        // there are four messages and they arrive in sequence: on the
        // Blender panel "field obj_ids missing" would not say whether
        // bake_begin or bake_remove rejected it.

        private void HandleBakeBegin(JsonElement root)
        {
            // The still-open batch is discarded BEFORE reading the new one,
            // even if the new one will be rejected. Whoever sends a
            // bake_begin has already stopped caring about the previous batch
            // (typically Blender lost the connection halfway and redid the
            // bake), and keeping it open would mix the bake_mesh messages of
            // the two attempts. Nothing of that batch was written: discarding
            // it costs nothing.
            if (_openBakeBatch != null)
            {
                _openBakeBatch = null;
                AbandonedBakeBatches++;
            }

            try
            {
                // Phase B2: target absent = directshape, so a Phase B Blender
                // keeps doing the bake it used to do. The target is read
                // before the ids because it decides their cap.
                string target = OptionalTarget(root);
                _openBakeBatch = new BakeBatch(RequireStrings(root, "obj_ids"), target);
            }
            catch (ContentException ex)
            {
                throw new ContentException("bake_begin: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new ContentException("bake_begin: " + ex.Message);
            }
        }

        private void HandleBakeMesh(JsonElement root, byte[] payload)
        {
            try
            {
                // Batch first: a bake_mesh outside a batch is rejected
                // without reading megabytes of payload.
                if (_openBakeBatch == null)
                {
                    throw new ContentException("no batch open, bake_begin is missing");
                }

                string objId = RequireString(root, "obj_id");
                string name = OptionalString(root, "name", objId);

                // category and matrix are mandatory, unlike geometry: here
                // there is no value already present to fall back to, and an
                // inferred default would write the element in the wrong
                // category or place without any error. The Generic Model
                // default is a property of the Blender object, not of the wire.
                string category = RequireString(root, "category");
                float[] matrix = RequireFloats(root, "matrix", RowMajorMatrix.Length);

                int vertCount = RequireInt(root, "vert_count");
                int faceCount = RequireInt(root, "face_count");
                int loopCount = RequireInt(root, "loop_count");
                int triCount = RequireInt(root, "tri_count");

                // Phase B2: absent = false. Read and validated even in a
                // DirectShape batch, where it is then ignored: a wrong type
                // is a defect of the sender in any batch.
                bool acceptOpen = OptionalBool(root, "accept_open", false);

                BakeMeshRequest request = BakeMeshRequest.Parse(
                    objId, name, category, matrix, vertCount, faceCount, loopCount, triCount, payload,
                    acceptOpen);

                // Rejects an id not announced; a second arrival of the same
                // id replaces the first. A rejected bake_mesh, here or above,
                // leaves the batch open: the object will end up among the
                // missing ones.
                _openBakeBatch.Add(request);
            }
            catch (ContentException ex)
            {
                throw new ContentException("bake_mesh: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                // Re-encoded instead of letting it bubble up, as in
                // HandleProxyEdges: the generic branch of Handle would prefix
                // "invalid geometry", which would say the wrong thing here.
                throw new ContentException("bake_mesh: " + ex.Message);
            }
        }

        private void HandleBakeEnd()
        {
            if (_openBakeBatch == null)
            {
                throw new ContentException("bake_end: no batch open, bake_begin is missing");
            }

            // Even a batch with nothing arrived must be executed: it is the
            // only way for Blender to receive a bake_result saying "all
            // missing", instead of waiting for an outcome that never comes.
            _pendingBakes.Add(_openBakeBatch);
            _openBakeBatch = null;
        }

        private void HandleBakeRemove(JsonElement root)
        {
            try
            {
                _pendingBakeRemovals.Add(BakeBatch.NormalizeObjectIds(RequireStrings(root, "obj_ids")));
            }
            catch (ContentException ex)
            {
                throw new ContentException("bake_remove: " + ex.Message);
            }
            catch (ArgumentException ex)
            {
                throw new ContentException("bake_remove: " + ex.Message);
            }
        }

        // A bake_begin that arrived and has not yet been closed by bake_end.
        public bool HasOpenBakeBatch
        {
            get { return _openBakeBatch != null; }
        }

        public bool HasPendingBakes
        {
            get { return _pendingBakes.Count > 0; }
        }

        // The batches closed by bake_end, in closing order, and empties the
        // list: like TakePendingProxies, calling it twice must not bake
        // twice.
        public List<BakeBatch> TakePendingBakes()
        {
            List<BakeBatch> taken = new List<BakeBatch>(_pendingBakes);
            _pendingBakes.Clear();
            return taken;
        }

        public bool HasPendingBakeRemovals
        {
            get { return _pendingBakeRemovals.Count > 0; }
        }

        // A list of already-normalized obj_ids for every bake_remove
        // received, in arrival order. Empties the list like TakePendingBakes.
        public List<List<string>> TakePendingBakeRemovals()
        {
            List<List<string>> taken = new List<List<string>>(_pendingBakeRemovals);
            _pendingBakeRemovals.Clear();
            return taken;
        }

        // ---- replies ----

        private static string BuildHelloAck(HostInfo host)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "hello_ack");
                    writer.WriteNumber("protocol_version", BridgeConstants.ProtocolVersion);
                    writer.WriteString("revit_version", host.RevitVersion);
                    writer.WriteString("doc_title", host.DocumentTitle);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // Public because MessageHandler uses it too: a transaction failure is
        // not a content error and does not originate in here, but whoever
        // pressed the button is on the other side of the wire and must know
        // about it. The header wire format still stays in a single file,
        // like already for the three pull messages.
        public static string BuildError(string message)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "error");
                    writer.WriteString("message", message);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // ---- Revit -> Blender, DESIGN.md 5.4 ----
        //
        // The three selection pull messages (Phase A2, Task 3). Public and
        // static because the caller is SendSelectionCommand, which lives in
        // Bilocus.Revit.Pull and touches the Revit API: the headers stay
        // here, where hello_ack and error are already built, so that
        // building the Revit -> Blender wire format lives in a single place.
        //
        // Element names come from the user's model and can contain quotes,
        // backslashes, non-ASCII characters: Utf8JsonWriter writes them
        // correctly, a hand-rolled string.Format does not.

        public static string BuildBatchBegin(int count)
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "revit_batch_begin");
                    writer.WriteNumber("count", count);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // category and type_name can be absent (system elements, symbols
        // without a type): written as an empty string, never null, for the
        // same reason as HostInfo further up in this file.
        //
        // origin is three floats in meters, world coordinates: it is the
        // position where the Blender side places the object, because the
        // payload's vertices travel in LOCAL coordinates. It is ALWAYS
        // written, even when zero: the Blender side requires it, an absent
        // origin is a content error. Omitting it when null would mean two
        // formats.
        public static string BuildGeometryHeader(
            long elementId, string name, string category, string typeName,
            int vertCount, int triCount, float[] origin, float[] color)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (origin == null) throw new ArgumentNullException("origin");
            if (color == null) throw new ArgumentNullException("color");
            if (origin.Length != 3)
            {
                throw new ArgumentException("origin must have 3 components, it has " + origin.Length);
            }
            if (color.Length != 4)
            {
                throw new ArgumentException("color must have 4 components, it has " + color.Length);
            }

            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "revit_geometry");
                    writer.WriteNumber("element_id", elementId);
                    writer.WriteString("name", name);
                    writer.WriteString("category", category == null ? "" : category);
                    writer.WriteString("type_name", typeName == null ? "" : typeName);
                    writer.WriteNumber("vert_count", vertCount);
                    writer.WriteNumber("tri_count", triCount);
                    writer.WriteStartArray("origin");
                    for (int i = 0; i < origin.Length; i++) { writer.WriteNumberValue(origin[i]); }
                    writer.WriteEndArray();
                    writer.WriteStartArray("color");
                    for (int i = 0; i < color.Length; i++) { writer.WriteNumberValue(color[i]); }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // proxy_result: how proxy creation went (DESIGN.md 5.4).
        //
        // Exists to close a hole left open on purpose. The creation runs on
        // an ExternalEvent triggered by the network and does NOT open any
        // TaskDialog: a modal popping up while the user is in the middle of
        // a Revit command is worse than the problem it would report. The
        // consequence, though, is that on SUCCESS whoever pressed the button
        // - who is in Blender - received nothing, and the only message that
        // reached them was the error frame from a failure. A button that
        // only speaks when things go wrong is a button nobody trusts.
        //
        // The COUNTS travel, not BuildSummaryText: that text is multi-line,
        // talks about sketch planes and milliseconds, and is written for the
        // Status button inside Revit. The Blender side reformats its own
        // panel line from the numbers, as it already does for the pull.
        // Only FailureReason goes over the wire, since the other side could
        // not reconstruct it.
        public static string BuildProxyResult(ProxyBuildResult result)
        {
            if (result == null) throw new ArgumentNullException("result");

            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "proxy_result");
                    writer.WriteString("obj_id", result.ObjectId == null ? "" : result.ObjectId);
                    writer.WriteString("name", result.Name == null ? "" : result.Name);
                    writer.WriteBoolean("ok", result.Succeeded);
                    writer.WriteNumber("requested", result.RequestedCount);
                    writer.WriteNumber("created", result.CreatedCount);
                    writer.WriteNumber("replaced", result.ReplacedCount);
                    writer.WriteNumber("skipped", result.SkippedDegenerateCount);

                    // How many of the created curves are arcs. Always present
                    // from this side; the Blender side treats it as optional
                    // so it can still talk to an earlier add-in.
                    writer.WriteNumber("arcs", result.CreatedArcCount);
                    writer.WriteNumber("failed", result.FailedCount);

                    // The sketch planes of the REPLACEMENT. They travel
                    // because replacement is the path the user uses most and
                    // it opens no dialog in Revit: without these two numbers,
                    // the only place to see whether the plane cleanup is
                    // working would be the Status button, i.e. on the other
                    // side from whoever pressed the button. Zero and zero
                    // when there was no replacement.
                    writer.WriteNumber("planes_deleted", result.ReplacedSketchPlanes.DeletedCount);
                    writer.WriteNumber("planes_kept", result.ReplacedSketchPlanes.KeptCount);
                    writer.WriteString("message",
                        result.FailureReason == null ? "" : result.FailureReason);
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        // bake_result: how a bake or a bake removal went (Phase B plan,
        // "Wire contract").
        //
        // Same reason as proxy_result: the bake runs on an ExternalEvent
        // triggered by the network, with no TaskDialog, and whoever pressed
        // the button is in Blender. Without this frame success would be
        // silence.
        //
        // Fields in the contract's order, counts ALWAYS written even at
        // zero: the Blender side reads them strictly, and a missing field is
        // a content error, not a zero. message is BakeResult.BuildMessage,
        // not BuildSummaryText: that is the multi-line text for the Status
        // button, and the Blender panel reformats its own line from the
        // numbers.
        public static string BuildBakeResult(BakeResult result)
        {
            if (result == null) throw new ArgumentNullException("result");

            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "bake_result");
                    writer.WriteString("action", result.Action);
                    writer.WriteBoolean("ok", result.Succeeded);
                    writer.WriteNumber("requested", result.RequestedCount);
                    writer.WriteNumber("created", result.CreatedCount);
                    writer.WriteNumber("replaced", result.ReplacedCount);
                    writer.WriteNumber("recreated", result.RecreatedCount);
                    writer.WriteNumber("removed", result.RemovedCount);
                    writer.WriteNumber("failed", result.FailedCount);
                    writer.WriteNumber("missing", result.MissingCount);
                    writer.WriteNumber("as_mesh", result.AsMeshCount);
                    writer.WriteNumber("faces_planar", result.FacesPlanarCount);
                    writer.WriteNumber("faces_triangulated", result.FacesTriangulatedCount);

                    // Phase B2: after the Phase B fields, before message,
                    // which stays last. Always written, even at zero, for
                    // the same strict reading.
                    writer.WriteString("target", result.Target);
                    writer.WriteNumber("switched", result.SwitchedCount);
                    writer.WriteNumber("not_moved", result.NotMovedCount);
                    writer.WriteString("message", result.BuildMessage());
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        public static string BuildBatchEnd()
        {
            using (MemoryStream buffer = new MemoryStream())
            {
                using (Utf8JsonWriter writer = new Utf8JsonWriter(buffer))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "revit_batch_end");
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(buffer.ToArray());
            }
        }

        private void Fail(SendFrame send, string message)
        {
            LastContentError = message;
            ContentErrorCount++;
            Reply(send, BuildError(message));
        }

        // Send's outcome is deliberately discarded: false means "socket
        // already down", which is not a reason to interrupt the queue drain.
        // Whoever needs to know reads it from BridgeServer.Status.
        private static void Reply(SendFrame send, string header)
        {
            if (send == null) { return; }
            send(new Frame(header, null));
        }

        // ---- field reading ----
        //
        // All reads are strict: they check the ValueKind before converting,
        // so the only exception that can come out of here is ContentException,
        // with a message saying which field is wrong. Relying on JsonElement's
        // own exceptions would give messages like "The requested operation
        // requires an element of type Number", useless to whoever reads the
        // error frame from the other side of the wire.

        private static string RequireString(JsonElement root, string name)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field) || field.ValueKind != JsonValueKind.String)
            {
                throw new ContentException("field " + name + " missing or not a string");
            }
            return field.GetString();
        }

        private static string OptionalString(JsonElement root, string name, string fallback)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field) || field.ValueKind != JsonValueKind.String)
            {
                return fallback;
            }
            return field.GetString();
        }

        // The fallback only applies if the field is MISSING. Present with a
        // type other than true/false (a string "true", a 1, a null) is an
        // error: reading it as some boolean would decide on the user's
        // behalf, and ignoring it would make it invisible.
        private static bool OptionalBool(JsonElement root, string name, bool fallback)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field)) { return fallback; }

            if (field.ValueKind == JsonValueKind.True) { return true; }
            if (field.ValueKind == JsonValueKind.False) { return false; }

            throw new ContentException("field " + name + " is not a boolean");
        }

        // bake_begin's target: absent = BakeTarget.DirectShape. If present it
        // must be a string accepted by BakeTarget.TryParse; any other value
        // or type, null included, is a content error.
        private static string OptionalTarget(JsonElement root)
        {
            JsonElement field;
            if (!root.TryGetProperty("target", out field)) { return BakeTarget.DirectShape; }

            string target;
            if (field.ValueKind != JsonValueKind.String || !BakeTarget.TryParse(field.GetString(), out target))
            {
                throw new ContentException(string.Format(
                    "invalid target field: allowed values are \"{0}\" and \"{1}\"",
                    BakeTarget.DirectShape, BakeTarget.Family));
            }
            return target;
        }

        private static int RequireInt(JsonElement root, string name)
        {
            JsonElement field;
            int value;
            if (!root.TryGetProperty(name, out field)
                || field.ValueKind != JsonValueKind.Number
                || !field.TryGetInt32(out value))
            {
                throw new ContentException("field " + name + " missing or not an integer");
            }
            return value;
        }

        // The fallback if the field is missing entirely. Present but wrong
        // is an error, for the same reason as OptionalFloats.
        private static int OptionalInt(JsonElement root, string name, int fallback)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field)) { return fallback; }
            return RequireInt(root, name);
        }

        private static float[] RequireFloats(JsonElement root, string name, int count)
        {
            float[] values = ReadFloats(root, name, count);
            if (values == null)
            {
                throw new ContentException(string.Format(
                    "field {0} missing or not an array of {1} numbers", name, count));
            }
            return values;
        }

        // null if the field is missing entirely. If present but wrong, it is
        // an error: a matrix with 3 elements is a bug of the sender, not an
        // absence, and silently ignoring it would make it invisible.
        private static float[] OptionalFloats(JsonElement root, string name, int count)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field)) { return null; }
            return RequireFloats(root, name, count);
        }

        private static float[] ReadFloats(JsonElement root, string name, int count)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field)
                || field.ValueKind != JsonValueKind.Array
                || field.GetArrayLength() != count)
            {
                return null;
            }

            float[] values = new float[count];
            int i = 0;
            foreach (JsonElement item in field.EnumerateArray())
            {
                double value;
                if (item.ValueKind != JsonValueKind.Number || !item.TryGetDouble(out value))
                {
                    return null;
                }
                values[i] = (float)value;
                i++;
            }
            return values;
        }

        private static List<string> RequireStrings(JsonElement root, string name)
        {
            JsonElement field;
            if (!root.TryGetProperty(name, out field) || field.ValueKind != JsonValueKind.Array)
            {
                throw new ContentException("field " + name + " missing or not an array");
            }

            List<string> values = new List<string>();
            foreach (JsonElement item in field.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ContentException("field " + name + " contains a non-string element");
                }
                values.Add(item.GetString());
            }
            return values;
        }
    }
}
