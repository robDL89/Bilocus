// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.IO;
using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // Everything about proxy_edges that can be verified without opening
    // Revit.
    //
    // This is the part that matters: proxy_edges is the first message that
    // makes the bridge write inside a project file, and every check that
    // passes through here is a check that does NOT need to be repeated inside
    // the transaction, where failing means rolling back elements already
    // created.
    public class ProxyEdgeRequestTests
    {
        private static byte[] Pack(params float[] values)
        {
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                foreach (float value in values) { w.Write(value); }
                w.Flush();
                return ms.ToArray();
            }
        }

        // One edge from (1,2,3) to (4,5,6), meters.
        private static byte[] OneEdge()
        {
            return Pack(1f, 2f, 3f, 4f, 5f, 6f);
        }

        [Fact]
        public void Parse_OneEdge_ReadsBothEnds()
        {
            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Cube", 1, OneEdge());

            Assert.Equal("obj-1", request.ObjectId);
            Assert.Equal("Cube", request.Name);
            Assert.Equal(1, request.EdgeCount);
            Assert.Equal(new float[] { 1f, 2f, 3f }, request.StartOf(0));
            Assert.Equal(new float[] { 4f, 5f, 6f }, request.EndOf(0));
        }

        [Fact]
        public void Parse_TwoEdges_KeepsThemInOrder()
        {
            byte[] payload = Pack(
                0f, 0f, 0f, 1f, 0f, 0f,
                1f, 0f, 0f, 1f, 1f, 0f);

            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Cube", 2, payload);

            Assert.Equal(2, request.EdgeCount);
            Assert.Equal(new float[] { 0f, 0f, 0f }, request.StartOf(0));
            Assert.Equal(new float[] { 1f, 0f, 0f }, request.EndOf(0));
            Assert.Equal(new float[] { 1f, 0f, 0f }, request.StartOf(1));
            Assert.Equal(new float[] { 1f, 1f, 0f }, request.EndOf(1));
        }

        // The name is a label, not an identity: if it is missing, the obj_id
        // is a more useful fallback than an empty string in the summary.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_WithoutUsableName_FallsBackToObjectId(string name)
        {
            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", name, 1, OneEdge());
            Assert.Equal("obj-1", request.Name);
        }

        // The trim is not cosmetic: if "obj-1 " were written into the mark and
        // "obj-1" were searched for, the replacement would find nothing and
        // resending would pile up overlapping lines instead of replacing them.
        [Fact]
        public void Parse_TrimsObjectId()
        {
            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("  obj-1  ", "Cube", 1, OneEdge());
            Assert.Equal("obj-1", request.ObjectId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("obj\0uno")]
        [InlineData("obj\nuno")]
        public void Parse_RejectsUnusableObjectId(string objectId)
        {
            Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse(objectId, "Cube", 1, OneEdge());
            });
        }

        [Fact]
        public void Parse_RejectsPayloadShorterThanDeclared()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", 2, OneEdge());
            });
            Assert.Contains("24 bytes", ex.Message);
            Assert.Contains("48", ex.Message);
        }

        [Fact]
        public void Parse_RejectsPayloadLongerThanDeclared()
        {
            byte[] payload = Pack(1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f);
            Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", 1, payload);
            });
        }

        [Fact]
        public void Parse_RejectsNullPayload()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", 1, null);
            });
        }

        [Fact]
        public void Parse_RejectsNegativeEdgeCount()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", -1, OneEdge());
            });
        }

        // Zero edges is NOT a removal request: a defect on the Blender side
        // must not be able to silently delete proxies created on purpose.
        [Fact]
        public void Parse_RejectsZeroEdges()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", 0, new byte[0]);
            });
        }

        // The cap exists because every edge costs at least two document
        // elements: without it, a wrong selection in Blender would block
        // Revit inside a transaction with no way to stop it.
        [Fact]
        public void Parse_RejectsMoreEdgesThanTheCap()
        {
            int tooMany = ProxyEdgeRequest.MaxEdges + 1;
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", tooMany, new byte[0]);
            });
            Assert.Contains(ProxyEdgeRequest.MaxEdges.ToString(), ex.Message);
        }

        // The cap is checked BEFORE the payload length: rejecting ten million
        // edges must not require allocating their payload.
        [Fact]
        public void Parse_AtTheCap_IsAccepted()
        {
            int edges = ProxyEdgeRequest.MaxEdges;
            byte[] payload = new byte[edges * ProxyEdgeRequest.BytesPerEdge];

            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Cube", edges, payload);

            Assert.Equal(edges, request.EdgeCount);
        }

        // A NaN in Revit becomes an XYZ that breaks Line.CreateBound halfway
        // through the transaction, after the previous edges have already been
        // created.
        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Parse_RejectsNonFiniteCoordinates(float bad)
        {
            byte[] payload = Pack(0f, 0f, 0f, 1f, bad, 0f);

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", 1, payload);
            });
            Assert.Contains("non-finite", ex.Message);
        }

        [Fact]
        public void Parse_NamesTheEdgeThatCarriesTheBadCoordinate()
        {
            byte[] payload = Pack(
                0f, 0f, 0f, 1f, 0f, 0f,
                0f, 0f, 0f, 0f, 0f, float.NaN);

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                ProxyEdgeRequest.Parse("obj-1", "Cube", 2, payload);
            });
            Assert.Contains("edge 1", ex.Message);
        }

        [Fact]
        public void StartAndEnd_RejectIndexesOutsideTheRequest()
        {
            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Cube", 1, OneEdge());

            Assert.Throws<ArgumentOutOfRangeException>(delegate { request.StartOf(-1); });
            Assert.Throws<ArgumentOutOfRangeException>(delegate { request.StartOf(1); });
            Assert.Throws<ArgumentOutOfRangeException>(delegate { request.EndOf(1); });
        }

        // Golden vector: the payload of a unit edge along X, byte by byte. It
        // pins down together the component order and the endianness, the two
        // things on which the two sides of the bridge could diverge without
        // anything signaling an error.
        [Fact]
        public void Parse_ReadsLittleEndianFloat32()
        {
            byte[] payload = new byte[]
            {
                0x00, 0x00, 0x00, 0x00,  // 0.0
                0x00, 0x00, 0x00, 0x00,  // 0.0
                0x00, 0x00, 0x00, 0x00,  // 0.0
                0x00, 0x00, 0x80, 0x3F,  // 1.0
                0x00, 0x00, 0x00, 0x00,  // 0.0
                0x00, 0x00, 0x00, 0x00   // 0.0
            };

            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Cube", 1, payload);

            Assert.Equal(new float[] { 0f, 0f, 0f }, request.StartOf(0));
            Assert.Equal(new float[] { 1f, 0f, 0f }, request.EndOf(0));
        }

        // ---- arcs of "Arcs and lines" mode ----
        //
        // Arcs travel after the edges, nine floats each: start, end, midpoint
        // (bridge_edges.pack_proxy_payload).

        [Fact]
        public void Parse_EdgeThenArc_ReadsBoth()
        {
            byte[] payload = Pack(
                0f, 0f, 0f, 1f, 0f, 0f,
                1f, 0f, 0f, 0f, 1f, 0f, 0.5f, 0.5f, 0.25f);

            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Curve", 1, 1, payload);

            Assert.Equal(1, request.EdgeCount);
            Assert.Equal(1, request.ArcCount);
            Assert.Equal(2, request.SegmentCount);
            Assert.Equal(new float[] { 1f, 0f, 0f }, request.EndOf(0));
            Assert.Equal(new float[] { 1f, 0f, 0f }, request.ArcStartOf(0));
            Assert.Equal(new float[] { 0f, 1f, 0f }, request.ArcEndOf(0));
            Assert.Equal(new float[] { 0.5f, 0.5f, 0.25f }, request.ArcMidOf(0));
        }

        // A closed circle becomes only arcs: zero edges is legal if there are
        // arcs.
        [Fact]
        public void Parse_OnlyArcs_IsAccepted()
        {
            byte[] payload = Pack(1f, 0f, 0f, -1f, 0f, 0f, 0f, 1f, 0f);

            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Circle", 0, 1, payload);

            Assert.Equal(0, request.EdgeCount);
            Assert.Equal(1, request.ArcCount);
        }

        [Fact]
        public void Parse_WithoutArcs_KeepsZeroArcs()
        {
            ProxyEdgeRequest request = ProxyEdgeRequest.Parse("obj-1", "Cube", 1, OneEdge());
            Assert.Equal(0, request.ArcCount);
            Assert.Equal(1, request.SegmentCount);
        }

        [Fact]
        public void Parse_NegativeArcCount_Throws()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => ProxyEdgeRequest.Parse("obj-1", "Cube", 1, -1, OneEdge()));
            Assert.Contains("arc_count", ex.Message);
        }

        [Fact]
        public void Parse_PayloadMissingTheArc_Throws()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => ProxyEdgeRequest.Parse("obj-1", "Cube", 1, 1, OneEdge()));
            Assert.Contains("1 arcs", ex.Message);
        }

        [Fact]
        public void Parse_ArcsCountTowardsTheMaximum()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => ProxyEdgeRequest.Parse(
                    "obj-1", "Cube", ProxyEdgeRequest.MaxEdges, 1, new byte[0]));
            Assert.Contains(ProxyEdgeRequest.MaxEdges.ToString(), ex.Message);
        }

        [Fact]
        public void Parse_NonFiniteArcCoordinate_Throws()
        {
            byte[] payload = Pack(1f, 0f, 0f, -1f, 0f, 0f, 0f, float.NaN, 0f);
            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => ProxyEdgeRequest.Parse("obj-1", "Circle", 0, 1, payload));
            Assert.Contains("arc 0", ex.Message);
        }
    }
}
