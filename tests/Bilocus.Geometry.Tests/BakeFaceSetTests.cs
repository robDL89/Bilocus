// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // The Phase B success criterion in miniature: Blender's flat square
    // faces stay square faces in Revit, and triangulation is the fallback
    // for the SINGLE polygon that is not planar, with the triangles Blender
    // chose.
    public class BakeFaceSetTests
    {
        private const double Tolerance = 1e-5;

        private static double[] ToDouble(float[] positions)
        {
            double[] points = new double[positions.Length];
            for (int i = 0; i < positions.Length; i++) { points[i] = positions[i]; }
            return points;
        }

        private static void AssertLoops(int[][] expected, List<int[]> actual)
        {
            Assert.Equal(expected.Length, actual.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], actual[i]);
            }
        }

        // The golden with vertex (1,1,0) raised by one centimeter: the quad
        // becomes skewed, the triangle (0,1,4) does not use that vertex.
        private static BakeMeshPayload RaisedGolden(out double[] points)
        {
            float[] positions = BakeMeshBytes.GoldenPositions();
            positions[2 * 3 + 2] = 0.01f;
            points = ToDouble(positions);
            return BakeMeshBytes.Parse(
                positions, BakeMeshBytes.GoldenFaceSizes(), BakeMeshBytes.GoldenFaceVertices(),
                BakeMeshBytes.GoldenTriVertices(), BakeMeshBytes.GoldenTriFaces());
        }

        [Fact]
        public void Golden_KeepsTheQuadAndTheTriangleWhole()
        {
            BakeMeshPayload mesh = BakeMeshBytes.ParseGolden();

            BakeFaceSet faces = BakeFaceSet.Build(ToDouble(mesh.Positions), mesh, Tolerance, false);

            AssertLoops(new int[][] { new int[] { 0, 1, 2, 3 }, new int[] { 0, 1, 4 } }, faces.Loops);
            Assert.Equal(2, faces.PlanarCount);
            Assert.Equal(0, faces.TriangulatedCount);
        }

        [Fact]
        public void QuadRaisedByOneCentimeter_FallsBackOnBlenderTriangles()
        {
            double[] points;
            BakeMeshPayload mesh = RaisedGolden(out points);

            BakeFaceSet faces = BakeFaceSet.Build(points, mesh, Tolerance, false);

            AssertLoops(new int[][]
            {
                new int[] { 0, 1, 2 },
                new int[] { 0, 2, 3 },
                new int[] { 0, 1, 4 }
            }, faces.Loops);
            Assert.Equal(1, faces.PlanarCount);
            Assert.Equal(1, faces.TriangulatedCount);
        }

        // The tolerance is a real parameter, not a disguised constant: the
        // same quad, with a tolerance wider than its deviation (2.5 mm),
        // stays whole.
        [Fact]
        public void QuadRaisedByOneCentimeter_WithinTolerance_StaysWhole()
        {
            double[] points;
            BakeMeshPayload mesh = RaisedGolden(out points);

            BakeFaceSet faces = BakeFaceSet.Build(points, mesh, 0.003, false);

            AssertLoops(new int[][] { new int[] { 0, 1, 2, 3 }, new int[] { 0, 1, 4 } }, faces.Loops);
            Assert.Equal(2, faces.PlanarCount);
        }

        // Tolerance zero is the bake's retry (BakeBuilder.BuildShape): a quad
        // off its plane by a micron, whole with the usual tolerance, goes to
        // triangles; the exactly planar golden quad stays whole.
        [Fact]
        public void ToleranceZero_TriangulatesTheNearlyPlanarQuadOnly()
        {
            float[] positions = BakeMeshBytes.GoldenPositions();
            positions[2 * 3 + 2] = 1e-6f;
            double[] points = ToDouble(positions);
            BakeMeshPayload nearlyPlanar = BakeMeshBytes.Parse(
                positions, BakeMeshBytes.GoldenFaceSizes(), BakeMeshBytes.GoldenFaceVertices(),
                BakeMeshBytes.GoldenTriVertices(), BakeMeshBytes.GoldenTriFaces());
            BakeMeshPayload planar = BakeMeshBytes.ParseGolden();

            Assert.Equal(0, BakeFaceSet.Build(points, nearlyPlanar, Tolerance, false).TriangulatedCount);
            Assert.Equal(1, BakeFaceSet.Build(points, nearlyPlanar, 0.0, false).TriangulatedCount);
            Assert.Equal(0, BakeFaceSet.Build(ToDouble(planar.Positions), planar, 0.0, false).TriangulatedCount);
        }

        // Planarity is measured on the WORLD points passed in, not on the
        // payload's local positions: that is where a non-uniform scale can
        // change the deviation.
        [Fact]
        public void Planarity_IsMeasuredOnTheWorldPoints()
        {
            double[] raised;
            RaisedGolden(out raised);
            BakeMeshPayload flat = BakeMeshBytes.ParseGolden();

            BakeFaceSet faces = BakeFaceSet.Build(raised, flat, Tolerance, false);

            Assert.Equal(1, faces.TriangulatedCount);
        }

        [Fact]
        public void FlipWinding_ReversesEveryWholeLoop()
        {
            BakeMeshPayload mesh = BakeMeshBytes.ParseGolden();

            BakeFaceSet faces = BakeFaceSet.Build(ToDouble(mesh.Positions), mesh, Tolerance, true);

            AssertLoops(new int[][] { new int[] { 3, 2, 1, 0 }, new int[] { 4, 1, 0 } }, faces.Loops);
            Assert.Equal(2, faces.PlanarCount);
        }

        [Fact]
        public void FlipWinding_ReversesTheFallbackTrianglesToo()
        {
            double[] points;
            BakeMeshPayload mesh = RaisedGolden(out points);

            BakeFaceSet faces = BakeFaceSet.Build(points, mesh, Tolerance, true);

            AssertLoops(new int[][]
            {
                new int[] { 2, 1, 0 },
                new int[] { 3, 2, 0 },
                new int[] { 4, 1, 0 }
            }, faces.Loops);
        }

        // Blender does not promise that loop_triangles is ordered by
        // polygon. Two skewed quads and a triangle, with the triangles
        // shuffled: the output is still polygon by polygon, and within each
        // polygon the triangles keep the order they arrived in.
        [Fact]
        public void ShuffledTriangles_AreGroupedByPolygonInStableOrder()
        {
            float[] positions =
            {
                0f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0.5f, 0f, 1f, 0f,
                2f, 0f, 0f, 3f, 0f, 0f, 3f, 1f, 0.5f, 2f, 1f, 0f,
                0.5f, -1f, 0f
            };
            uint[] faceSizes = { 4, 4, 3 };
            uint[] faceVertices = { 0, 1, 2, 3, 4, 5, 6, 7, 0, 8, 1 };
            uint[] triVertices = { 4, 6, 7, 0, 2, 3, 0, 8, 1, 4, 5, 6, 0, 1, 2 };
            uint[] triFaces = { 1, 0, 2, 1, 0 };

            BakeMeshPayload mesh = BakeMeshBytes.Parse(positions, faceSizes, faceVertices, triVertices, triFaces);
            BakeFaceSet faces = BakeFaceSet.Build(ToDouble(positions), mesh, Tolerance, false);

            AssertLoops(new int[][]
            {
                new int[] { 0, 2, 3 },
                new int[] { 0, 1, 2 },
                new int[] { 4, 6, 7 },
                new int[] { 4, 5, 6 },
                new int[] { 0, 8, 1 }
            }, faces.Loops);
            Assert.Equal(1, faces.PlanarCount);
            Assert.Equal(2, faces.TriangulatedCount);
        }

        // A degenerate quad has no plane: no whole face, fallback to
        // triangles just like a skewed polygon.
        [Fact]
        public void DegenerateQuad_FallsBackOnTriangles()
        {
            float[] positions = { 0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f, 0f, 3f, 0f, 0f };
            BakeMeshPayload mesh = BakeMeshBytes.Parse(
                positions, new uint[] { 4 }, new uint[] { 0, 1, 2, 3 },
                new uint[] { 0, 1, 2, 0, 2, 3 }, new uint[] { 0, 0 });

            BakeFaceSet faces = BakeFaceSet.Build(ToDouble(positions), mesh, Tolerance, false);

            Assert.Equal(2, faces.Loops.Count);
            Assert.Equal(0, faces.PlanarCount);
            Assert.Equal(1, faces.TriangulatedCount);
        }

        // A triangle is planar by definition and its fallback would be
        // itself: it stays whole and counts as planar even if it has no
        // area. If Revit rejects it, the builder skips it and counts it.
        [Fact]
        public void TrianglePolygon_CountsAsPlanarEvenWhenDegenerate()
        {
            float[] positions = { 0f, 0f, 0f, 1f, 0f, 0f, 2f, 0f, 0f };
            BakeMeshPayload mesh = BakeMeshBytes.Parse(
                positions, new uint[] { 3 }, new uint[] { 0, 1, 2 }, new uint[] { 0, 1, 2 }, new uint[] { 0 });

            BakeFaceSet faces = BakeFaceSet.Build(ToDouble(positions), mesh, Tolerance, false);

            AssertLoops(new int[][] { new int[] { 0, 1, 2 } }, faces.Loops);
            Assert.Equal(1, faces.PlanarCount);
            Assert.Equal(0, faces.TriangulatedCount);
        }

        // The loops are copies: the builder can keep them without anyone
        // changing the data underneath, and reversing them does not touch
        // the payload.
        [Fact]
        public void Loops_DoNotShareMemoryWithThePayload()
        {
            BakeMeshPayload mesh = BakeMeshBytes.ParseGolden();

            BakeFaceSet faces = BakeFaceSet.Build(ToDouble(mesh.Positions), mesh, Tolerance, true);
            faces.Loops[0][0] = 99;

            Assert.Equal(new int[] { 0, 1, 2, 3, 0, 1, 4 }, mesh.FaceVertices);
        }

        [Fact]
        public void InvalidArguments_AreRejected()
        {
            BakeMeshPayload mesh = BakeMeshBytes.ParseGolden();
            double[] points = ToDouble(mesh.Positions);

            Assert.Throws<ArgumentNullException>(delegate { BakeFaceSet.Build(null, mesh, Tolerance, false); });
            Assert.Throws<ArgumentNullException>(delegate { BakeFaceSet.Build(points, null, Tolerance, false); });
            Assert.Throws<ArgumentException>(delegate
            {
                BakeFaceSet.Build(new double[points.Length - 3], mesh, Tolerance, false);
            });
            Assert.Throws<ArgumentException>(delegate { BakeFaceSet.Build(points, mesh, -1e-6, false); });
            Assert.Throws<ArgumentException>(delegate { BakeFaceSet.Build(points, mesh, double.NaN, false); });
            Assert.Throws<ArgumentException>(delegate
            {
                BakeFaceSet.Build(points, mesh, double.PositiveInfinity, false);
            });
        }
    }
}
