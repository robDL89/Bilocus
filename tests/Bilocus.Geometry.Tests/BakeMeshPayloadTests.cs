// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // bake_mesh is the payload that turns a Blender object into a real
    // element of the Revit document. Every contract rule has a test here
    // that violates it: a defect that slips past here ends up inside a
    // transaction, where it costs a failed object with no readable reason.
    public class BakeMeshPayloadTests
    {
        private static ArgumentException Rejects(byte[] payload, int vert, int face, int loop, int tri)
        {
            return Assert.Throws<ArgumentException>(delegate
            {
                BakeMeshPayload.Parse(payload, vert, face, loop, tri);
            });
        }

        private static ArgumentException RejectsArrays(
            float[] positions, uint[] faceSizes, uint[] faceVertices, uint[] triVertices, uint[] triFaces)
        {
            return Assert.Throws<ArgumentException>(delegate
            {
                BakeMeshBytes.Parse(positions, faceSizes, faceVertices, triVertices, triFaces);
            });
        }

        // ---- golden vector ----

        [Fact]
        public void Golden_HasTheContractLength()
        {
            byte[] golden = BakeMeshBytes.Golden();

            Assert.Equal(144, golden.Length);
            Assert.Equal(12 * 5 + 4 * 2 + 4 * 7 + 16 * 3, golden.Length);
        }

        [Fact]
        public void Golden_ReadsEveryFieldExactly()
        {
            BakeMeshPayload mesh = BakeMeshBytes.ParseGolden();

            Assert.Equal(new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f, 0f, 1f, 0f, 0.5f, 0.5f, 1f },
                mesh.Positions);
            Assert.Equal(new int[] { 4, 3 }, mesh.FaceSizes);
            Assert.Equal(new int[] { 0, 1, 2, 3, 0, 1, 4 }, mesh.FaceVertices);
            Assert.Equal(new int[] { 0, 1, 2, 0, 2, 3, 0, 1, 4 }, mesh.TriVertices);
            Assert.Equal(new int[] { 0, 0, 1 }, mesh.TriFaces);

            Assert.Equal(5, mesh.VertexCount);
            Assert.Equal(2, mesh.FaceCount);
            Assert.Equal(7, mesh.LoopCount);
            Assert.Equal(3, mesh.TriangleCount);
        }

        // If the test packer did not produce the contract's layout, every
        // test below for a violated rule could be red for the wrong reason -
        // or green for the wrong reason.
        [Fact]
        public void TestPacker_ProducesTheGoldenBytes()
        {
            byte[] packed = BakeMeshBytes.Pack(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(),
                BakeMeshBytes.GoldenFaceVertices(), BakeMeshBytes.GoldenTriVertices(),
                BakeMeshBytes.GoldenTriFaces());

            Assert.Equal(BakeMeshBytes.Golden(), packed);
        }

        // Blender does not guarantee that loop_triangles is ordered by
        // polygon, and the contract does not require it: triangles in
        // scattered order are valid.
        [Fact]
        public void TrianglesNotSortedByPolygon_AreAccepted()
        {
            BakeMeshPayload mesh = BakeMeshBytes.Parse(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(),
                BakeMeshBytes.GoldenFaceVertices(),
                new uint[] { 0, 1, 4, 0, 1, 2, 0, 2, 3 },
                new uint[] { 1, 0, 0 });

            Assert.Equal(new int[] { 1, 0, 0 }, mesh.TriFaces);
        }

        // ---- counts ----

        [Theory]
        [InlineData(2)]
        [InlineData(0)]
        [InlineData(-1)]
        public void VertCountBelowThree_IsRejected(int vertCount)
        {
            ArgumentException ex = Rejects(BakeMeshBytes.Golden(), vertCount, 2, 7, 3);
            Assert.Contains("vert_count", ex.Message);
            Assert.Contains("minimum", ex.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void FaceCountBelowOne_IsRejected(int faceCount)
        {
            ArgumentException ex = Rejects(BakeMeshBytes.Golden(), 5, faceCount, 7, 3);
            Assert.Contains("face_count", ex.Message);
            Assert.Contains("minimum", ex.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void TriCountBelowOne_IsRejected(int triCount)
        {
            ArgumentException ex = Rejects(BakeMeshBytes.Golden(), 5, 2, 7, triCount);
            Assert.Contains("tri_count", ex.Message);
            Assert.Contains("minimum", ex.Message);
        }

        // The contract gives loop_count no explicit minimum, but one follows
        // from it (at least one polygon, at least three vertices). The check
        // keeps a negative loop_count from reaching the allocation, where it
        // would raise an OverflowException instead of a content error.
        [Theory]
        [InlineData(2)]
        [InlineData(0)]
        [InlineData(-1)]
        public void LoopCountBelowThree_IsRejected(int loopCount)
        {
            ArgumentException ex = Rejects(BakeMeshBytes.Golden(), 5, 2, loopCount, 3);
            Assert.Contains("loop_count", ex.Message);
            Assert.Contains("minimum", ex.Message);
        }

        // The cap is checked BEFORE the payload length: rejecting two
        // million and one faces should not require receiving them.
        [Theory]
        [InlineData("vert_count")]
        [InlineData("face_count")]
        [InlineData("tri_count")]
        public void CountOverTheCap_IsRejectedBeforeTheLength(string field)
        {
            int over = BakeMeshPayload.MaxBakeFaces + 1;
            int vert = field == "vert_count" ? over : 5;
            int face = field == "face_count" ? over : 2;
            int tri = field == "tri_count" ? over : 3;

            ArgumentException ex = Rejects(new byte[0], vert, face, 7, tri);

            Assert.Contains(field, ex.Message);
            Assert.Contains(BakeMeshPayload.MaxBakeFaces.ToString(), ex.Message);
        }

        // Every polygon has exactly size-2 triangles, so summed:
        // tri_count = loop_count - 2 * face_count. It is a consequence of
        // the rules, not a new rule: checking it in the header rejects
        // before reading the payload, and with the same criterion as the
        // Blender side.
        [Fact]
        public void TriCountInconsistentWithLoopsAndFaces_IsRejectedBeforeThePayload()
        {
            ArgumentException ex = Rejects(new byte[0], 5, 2, 7, 4);

            Assert.Contains("tri_count 4", ex.Message);
            Assert.Contains("loop_count - 2 * face_count", ex.Message);
        }

        // ---- length ----

        [Fact]
        public void PayloadShorterThanDeclared_IsRejected()
        {
            byte[] golden = BakeMeshBytes.Golden();
            byte[] shorter = new byte[golden.Length - 4];
            Array.Copy(golden, shorter, shorter.Length);

            ArgumentException ex = Rejects(shorter, 5, 2, 7, 3);
            Assert.Contains("140 bytes", ex.Message);
            Assert.Contains("144", ex.Message);
        }

        [Fact]
        public void PayloadLongerThanDeclared_IsRejected()
        {
            byte[] golden = BakeMeshBytes.Golden();
            byte[] longer = new byte[golden.Length + 4];
            Array.Copy(golden, longer, golden.Length);

            ArgumentException ex = Rejects(longer, 5, 2, 7, 3);
            Assert.Contains("148 bytes", ex.Message);
        }

        // A huge loop_count must give a content error, not an
        // OverflowException from a 32-bit computation: whatever check stops
        // it, the exception must be the one the router turns into a frame
        // error.
        [Fact]
        public void HugeLoopCount_IsRejectedWithoutOverflow()
        {
            ArgumentException ex = Rejects(BakeMeshBytes.Golden(), 5, 2, int.MaxValue, 3);
            Assert.Contains("loop_count", ex.Message);
        }

        [Fact]
        public void NullPayload_IsRejected()
        {
            Rejects(null, 5, 2, 7, 3);
        }

        // ---- values ----

        // A NaN in Revit becomes an XYZ that breaks the face halfway
        // through the builder.
        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void NonFinitePosition_IsRejectedNamingTheVertex(float bad)
        {
            float[] positions = BakeMeshBytes.GoldenPositions();
            positions[3 * 3 + 1] = bad;

            ArgumentException ex = RejectsArrays(
                positions, BakeMeshBytes.GoldenFaceSizes(), BakeMeshBytes.GoldenFaceVertices(),
                BakeMeshBytes.GoldenTriVertices(), BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("non-finite", ex.Message);
            Assert.Contains("vertex 3", ex.Message);
        }

        [Fact]
        public void PolygonWithFewerThanThreeVertices_IsRejected()
        {
            // Sums and triangles consistent on purpose: the only defect is
            // the two-vertex polygon.
            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(),
                new uint[] { 2, 5 },
                new uint[] { 0, 1, 0, 1, 2, 3, 4 },
                new uint[] { 0, 1, 2, 0, 2, 3, 0, 3, 4 },
                new uint[] { 1, 1, 1 });

            Assert.Contains("polygon 0", ex.Message);
            Assert.Contains("at least 3", ex.Message);
        }

        [Fact]
        public void FaceSizesSummingOverLoopCount_IsRejected()
        {
            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(),
                new uint[] { 4, 4 },
                BakeMeshBytes.GoldenFaceVertices(),
                BakeMeshBytes.GoldenTriVertices(),
                BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("face_sizes", ex.Message);
            Assert.Contains("loop_count", ex.Message);
        }

        [Fact]
        public void FaceSizesSummingUnderLoopCount_IsRejected()
        {
            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(),
                new uint[] { 3, 3 },
                BakeMeshBytes.GoldenFaceVertices(),
                BakeMeshBytes.GoldenTriVertices(),
                BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("face_sizes", ex.Message);
            Assert.Contains("loop_count", ex.Message);
        }

        // A uint32 that does not fit in an int must not become a negative
        // size from a cast wraparound, nor overflow the sum.
        [Fact]
        public void FaceSizeThatDoesNotFitAnInt_IsRejected()
        {
            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(),
                new uint[] { 4, 0xFFFFFFFFu },
                BakeMeshBytes.GoldenFaceVertices(),
                BakeMeshBytes.GoldenTriVertices(),
                BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("loop_count", ex.Message);
        }

        [Fact]
        public void FaceVertexOutOfRange_IsRejected()
        {
            uint[] faceVertices = BakeMeshBytes.GoldenFaceVertices();
            faceVertices[6] = 5;

            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(), faceVertices,
                BakeMeshBytes.GoldenTriVertices(), BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("face_vertices", ex.Message);
        }

        [Fact]
        public void FaceVertexThatDoesNotFitAnInt_IsRejected()
        {
            uint[] faceVertices = BakeMeshBytes.GoldenFaceVertices();
            faceVertices[0] = 0x80000000u;

            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(), faceVertices,
                BakeMeshBytes.GoldenTriVertices(), BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("face_vertices", ex.Message);
        }

        [Fact]
        public void TriangleVertexOutOfRange_IsRejected()
        {
            uint[] triVertices = BakeMeshBytes.GoldenTriVertices();
            triVertices[8] = 5;

            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(),
                BakeMeshBytes.GoldenFaceVertices(), triVertices, BakeMeshBytes.GoldenTriFaces());

            Assert.Contains("tri_vertices", ex.Message);
        }

        [Fact]
        public void TriangleFaceOutOfRange_IsRejected()
        {
            uint[] triFaces = BakeMeshBytes.GoldenTriFaces();
            triFaces[2] = 2;

            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(),
                BakeMeshBytes.GoldenFaceVertices(), BakeMeshBytes.GoldenTriVertices(), triFaces);

            Assert.Contains("tri_faces", ex.Message);
        }

        // All the triangles on the quad, none on the triangle: the total
        // does not add up for either polygon.
        [Fact]
        public void TrianglesAllOnOnePolygon_AreRejected()
        {
            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(),
                BakeMeshBytes.GoldenFaceVertices(), BakeMeshBytes.GoldenTriVertices(),
                new uint[] { 0, 0, 0 });

            Assert.Contains("polygon 0", ex.Message);
            Assert.Contains("instead of 2", ex.Message);
        }

        // The sneaky case: the TOTAL number of triangles is right (3 = 2 + 1)
        // but the split is not. It is exactly what happens when face_sizes
        // and tri_faces arrive misaligned, and a check on the total alone
        // would let it through.
        [Fact]
        public void TriangleTotalRightButDistributionWrong_IsRejected()
        {
            ArgumentException ex = RejectsArrays(
                BakeMeshBytes.GoldenPositions(), BakeMeshBytes.GoldenFaceSizes(),
                BakeMeshBytes.GoldenFaceVertices(), BakeMeshBytes.GoldenTriVertices(),
                new uint[] { 0, 1, 1 });

            Assert.Contains("polygon 0", ex.Message);
            Assert.Contains("misaligned", ex.Message);
        }
    }
}
