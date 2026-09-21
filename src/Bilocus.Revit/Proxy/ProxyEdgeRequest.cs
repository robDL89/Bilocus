// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Proxy
{
    // The proxy creation request, already read from the wire and validated,
    // waiting to run on Revit's main thread.
    //
    // It exists as its own type for a precise reason. The proxy_edges message
    // arrives on the socket and is interpreted by MessageRouter, which does
    // NOT have the Revit API and must not have it: it is compiled as a linked
    // source from the test project, without RevitAPI available. But
    // proxy_edges is the first message that writes into the document, and
    // writing requires a transaction, which requires the API. So the router
    // does not execute: it packages the request in here and leaves it
    // pending; MessageHandler, which runs on the main thread and does have the
    // API, executes it after draining the queue.
    //
    // The benefit is not just about compiling: all validation of the message
    // (consistent counts, finite coordinates, a writable obj_id) ends up in
    // this file, which the tests reach. What is left in ProxyBuilder is only
    // what truly requires Revit.
    //
    // The coordinates in here are in METERS, world coordinates, as they arrive
    // from Blender. The conversion to feet is done by ProxyBuilder, which is
    // the only point where Revit's units are entered.
    public sealed class ProxyEdgeRequest
    {
        public const int FloatsPerEdge = 6;
        public const int BytesPerEdge = FloatsPerEdge * 4;

        // The arcs of "Arcs and lines" mode travel AFTER the edges, in the
        // same payload: start, end, midpoint. The order matches
        // Arc.Create(end0, end1, pointOnArc). Twin of
        // bridge_edges.FLOATS_PER_ARC.
        public const int FloatsPerArc = 9;
        public const int BytesPerArc = FloatsPerArc * 4;

        // Safety ceiling on the number of segments (edges plus arcs) of a
        // single request.
        //
        // Not an aesthetic preference: every edge becomes at least two
        // document elements, and the protocol's MaxPayloadBytes would let
        // through more than eleven million edges. A wrong selection in
        // Blender (Ctrl+A on a dense mesh) would block Revit for hours inside
        // a transaction, and the user would not even have a way to stop it.
        // This phase's use case is "the three edges I need": ten thousand is
        // already three orders of magnitude beyond that.
        //
        // Exceeding it is a CONTENT error: it is answered with an error frame
        // and the bridge continues, it is not attempted and not truncated.
        // Truncating would create a partial proxy without anyone knowing which
        // half is missing.
        public const int MaxEdges = 10000;

        private readonly float[] _points;
        private readonly float[] _arcs;

        // Already normalized by ProxyNaming: this is the form in which the
        // mark is written and in which proxies to replace are searched for.
        public string ObjectId { get; private set; }

        // Label, not identity. Only used in the readable summary.
        public string Name { get; private set; }

        public int EdgeCount { get { return _points.Length / FloatsPerEdge; } }

        public int ArcCount { get { return _arcs.Length / FloatsPerArc; } }

        // Edges plus arcs: this is the "requested" count of the summary.
        public int SegmentCount { get { return EdgeCount + ArcCount; } }

        private ProxyEdgeRequest(string objectId, string name, float[] points, float[] arcs)
        {
            ObjectId = objectId;
            Name = name;
            _points = points;
            _arcs = arcs;
        }

        // The first end of the edge, in meters, world coordinates.
        public float[] StartOf(int index)
        {
            CheckIndex(index);
            int at = index * FloatsPerEdge;
            return new float[] { _points[at], _points[at + 1], _points[at + 2] };
        }

        public float[] EndOf(int index)
        {
            CheckIndex(index);
            int at = index * FloatsPerEdge + 3;
            return new float[] { _points[at], _points[at + 1], _points[at + 2] };
        }

        // The three points of the arc, in meters, world coordinates.
        public float[] ArcStartOf(int index) { return ArcPoint(index, 0); }
        public float[] ArcEndOf(int index) { return ArcPoint(index, 1); }
        public float[] ArcMidOf(int index) { return ArcPoint(index, 2); }

        private float[] ArcPoint(int index, int which)
        {
            if (index < 0 || index >= ArcCount)
            {
                throw new ArgumentOutOfRangeException("index",
                    "arc " + index + " out of range (" + ArcCount + " arcs)");
            }
            int at = index * FloatsPerArc + which * 3;
            return new float[] { _arcs[at], _arcs[at + 1], _arcs[at + 2] };
        }

        private void CheckIndex(int index)
        {
            if (index < 0 || index >= EdgeCount)
            {
                throw new ArgumentOutOfRangeException("index",
                    "edge " + index + " out of range (" + EdgeCount + " edges)");
            }
        }

        // Reads and validates the proxy_edges message.
        //
        // Throws ArgumentException for any inconsistency: they are all defects
        // of the received message, which the router translates into an error
        // frame without closing the connection (DESIGN.md 5.1).
        public static ProxyEdgeRequest Parse(string objectId, string name, int edgeCount, byte[] payload)
        {
            return Parse(objectId, name, edgeCount, 0, payload);
        }

        // As above, with the arcs of "Arcs and lines" mode. arc_count absent
        // on the wire counts as zero, matching the Phase A3 message.
        public static ProxyEdgeRequest Parse(
            string objectId, string name, int edgeCount, int arcCount, byte[] payload)
        {
            // Normalize first: an empty obj_id or one full of control
            // characters does not identify anything, and without an identity
            // there is no replacement and no removal. Better to reject the
            // message than leave elements in the document that nobody will be
            // able to remove anymore.
            string normalizedId = ProxyNaming.NormalizeObjectId(objectId);

            string label = name;
            if (label == null || label.Trim().Length == 0) { label = normalizedId; }

            if (edgeCount < 0)
            {
                throw new ArgumentException("edge_count negative: " + edgeCount);
            }

            if (arcCount < 0)
            {
                throw new ArgumentException("arc_count negative: " + arcCount);
            }

            // Zero edges is NOT a removal request.
            //
            // It would have been convenient to treat it that way ("I send
            // nothing, nothing is left"), but it would mean a defect on the
            // Blender side, for example a badly read selection, would silently
            // delete proxies the user created on purpose. Removal has its own
            // command, with confirmation and an explicit count: that is the
            // path for taking things away.
            if (edgeCount == 0 && arcCount == 0)
            {
                throw new ArgumentException(
                    "proxy_edges with no edges: nothing to create");
            }

            // In long: two ints close to the maximum summed must not wrap into
            // negative and pass the check.
            if ((long)edgeCount + arcCount > MaxEdges)
            {
                throw new ArgumentException(string.Format(
                    "edge_count {0} beyond the maximum of {1} edges per request",
                    (long)edgeCount + arcCount, MaxEdges));
            }

            if (payload == null)
            {
                throw new ArgumentException("proxy_edges with no payload");
            }

            long expected = (long)edgeCount * BytesPerEdge + (long)arcCount * BytesPerArc;
            if (payload.Length != expected)
            {
                if (arcCount == 0)
                {
                    throw new ArgumentException(string.Format(
                        "payload of {0} bytes but the header declares {1} ({2} edges)",
                        payload.Length, expected, edgeCount));
                }
                throw new ArgumentException(string.Format(
                    "payload of {0} bytes but the header declares {1} ({2} edges, {3} arcs)",
                    payload.Length, expected, edgeCount, arcCount));
            }

            float[] points = new float[edgeCount * FloatsPerEdge];
            int offset = 0;
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = ReadFloat(payload, ref offset);

                // A NaN or an infinity must not be let through: in Revit it
                // would become an XYZ that breaks Line.CreateBound in the
                // middle of the transaction, after the previous edges have
                // already been created. Better to reject the whole message
                // before opening any transaction. Same rule already applied to
                // origin in the Revit -> Blender direction, see DESIGN.md 5.4.
                if (float.IsNaN(points[i]) || float.IsInfinity(points[i]))
                {
                    throw new ArgumentException(string.Format(
                        "non-finite coordinate in edge {0}, component {1}",
                        i / FloatsPerEdge, i % FloatsPerEdge));
                }
            }

            float[] arcs = new float[arcCount * FloatsPerArc];
            for (int i = 0; i < arcs.Length; i++)
            {
                arcs[i] = ReadFloat(payload, ref offset);
                if (float.IsNaN(arcs[i]) || float.IsInfinity(arcs[i]))
                {
                    throw new ArgumentException(string.Format(
                        "non-finite coordinate in arc {0}, component {1}",
                        i / FloatsPerArc, i % FloatsPerArc));
                }
            }

            return new ProxyEdgeRequest(normalizedId, label, points, arcs);
        }

        private static float ReadFloat(byte[] buffer, ref int offset)
        {
            byte[] raw = new byte[4];
            Array.Copy(buffer, offset, raw, 0, 4);
            if (!BitConverter.IsLittleEndian) { Array.Reverse(raw); }
            offset += 4;
            return BitConverter.ToSingle(raw, 0);
        }
    }
}
