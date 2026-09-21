// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // Planarity decides, polygon by polygon, whether a Blender quad stays a
    // quad in Revit or becomes two triangles with a visible diagonal. Revit
    // decides it with its OWN tolerance: only the measurement lives here.
    public class PolygonPlanarityTests
    {
        private static double Measure(double[] points, out bool degenerate)
        {
            int count = points.Length / 3;
            int[] indices = new int[count];
            for (int i = 0; i < count; i++) { indices[i] = i; }
            return PolygonPlanarity.MaxDeviation(points, indices, 0, count, out degenerate);
        }

        [Fact]
        public void FlatSquare_HasZeroDeviation()
        {
            bool degenerate;
            double deviation = Measure(new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 }, out degenerate);

            Assert.False(degenerate);
            Assert.Equal(0.0, deviation, 12);
        }

        // The known skewed quad: the unit square with vertex (1,1) raised
        // by h.
        //
        // Computed by hand. Vertices A(0,0,0) B(1,0,0) C(1,1,h) D(0,1,0).
        // Newell's normal, summed over (i, i+1):
        //   nx = sum (yi - yj)(zi + zj) = (0-1)(0+h)            = -h
        //   ny = sum (zi - zj)(xi + xj) = (0-h)(1+1) + (h-0)(1+0) = -h
        //   nz = sum (xi - xj)(yi + yj) = (1-0)(1+1)              =  2
        // Centroid (0.5, 0.5, h/4). The vertices minus the centroid, scaled
        // onto the normal (-h, -h, 2), give +h/2, -h/2, +h/2, -h/2: all at
        // equal distance, as symmetry requires.
        // Deviation = (h/2) / |N| = h / (2 * sqrt(2 h^2 + 4)).
        // With h = 1: 1 / (2 * sqrt(6)) = 0.2041241452...
        [Fact]
        public void SkewedQuad_HasTheDeviationComputedByHand()
        {
            bool degenerate;
            double deviation = Measure(new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 1, 0, 1, 0 }, out degenerate);

            Assert.False(degenerate);
            Assert.Equal(0.20412414523193150, deviation, 12);
        }

        // The same with the vertex raised by one centimeter, the case from
        // the BakeFaceSet test: 0.01 / (2 * sqrt(4.0002)) = 0.0024999375...
        [Fact]
        public void QuadRaisedByOneCentimeter_HasTheDeviationComputedByHand()
        {
            bool degenerate;
            double deviation = Measure(new double[] { 0, 0, 0, 1, 0, 0, 1, 1, 0.01, 0, 1, 0 }, out degenerate);

            Assert.False(degenerate);
            Assert.Equal(0.01 / (2.0 * Math.Sqrt(4.0002)), deviation, 12);
        }

        // A planar hexagon rotated about two axes and moved a kilometer
        // away from the origin: the measurement must not depend on where or
        // how the plane sits. The kilometer is deliberate, that is where a
        // formula on absolute coordinates starts losing digits.
        [Fact]
        public void PlanarHexagonRotatedAndFarAway_HasZeroDeviation()
        {
            double ax = 30.0 * Math.PI / 180.0;
            double az = 45.0 * Math.PI / 180.0;
            double[] points = new double[18];

            for (int i = 0; i < 6; i++)
            {
                double angle = i * Math.PI / 3.0;
                double x = 2.0 * Math.Cos(angle);
                double y = 2.0 * Math.Sin(angle);
                double z = 0.0;

                // rotation about X
                double y1 = y * Math.Cos(ax) - z * Math.Sin(ax);
                double z1 = y * Math.Sin(ax) + z * Math.Cos(ax);
                // rotation about Z
                double x2 = x * Math.Cos(az) - y1 * Math.Sin(az);
                double y2 = x * Math.Sin(az) + y1 * Math.Cos(az);

                points[i * 3] = x2 + 1000.0;
                points[i * 3 + 1] = y2 - 1000.0;
                points[i * 3 + 2] = z1 + 1000.0;
            }

            bool degenerate;
            double deviation = Measure(points, out degenerate);

            Assert.False(degenerate);
            Assert.True(deviation < 1e-9, "deviation = " + deviation);
        }

        [Fact]
        public void CollinearVertices_AreDegenerate()
        {
            bool degenerate;
            Measure(new double[] { 0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0 }, out degenerate);

            Assert.True(degenerate);
        }

        // A degenerate polygon has no plane, so it has no sensible
        // deviation. The returned value is infinite on purpose: whoever
        // compares against the tolerance and forgets to check degenerate
        // still gets "not planar", i.e. the fallback to triangles, rather
        // than a whole face with no area.
        [Fact]
        public void Degenerate_ReportsAnInfiniteDeviation()
        {
            bool degenerate;
            double deviation = Measure(new double[] { 0, 0, 0, 0, 0, 0, 0, 0, 0 }, out degenerate);

            Assert.True(degenerate);
            Assert.True(double.IsPositiveInfinity(deviation));
        }

        // Concavity is not non-planarity: a flat arrow stays flat.
        [Fact]
        public void ConcavePlanarQuad_HasZeroDeviation()
        {
            bool degenerate;
            double deviation = Measure(new double[] { 0, 0, 5, 2, 1, 5, 0, 2, 5, 0.5, 1, 5 }, out degenerate);

            Assert.False(degenerate);
            Assert.Equal(0.0, deviation, 12);
        }

        // The polygon is read inside face_vertices starting at start: here
        // it is the triangle from the golden vector, after the quad.
        [Fact]
        public void ReadsThePolygonAtTheGivenStart()
        {
            double[] points = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0.5, 0.5, 1 };
            int[] faceVertices = { 0, 1, 2, 3, 0, 1, 4 };

            bool degenerate;
            double deviation = PolygonPlanarity.MaxDeviation(points, faceVertices, 4, 3, out degenerate);

            Assert.False(degenerate);
            Assert.Equal(0.0, deviation, 12);
        }

        [Fact]
        public void InvalidArguments_AreRejected()
        {
            double[] points = { 0, 0, 0, 1, 0, 0, 1, 1, 0 };
            int[] faceVertices = { 0, 1, 2 };
            bool degenerate;

            Assert.Throws<ArgumentNullException>(delegate
            {
                PolygonPlanarity.MaxDeviation(null, faceVertices, 0, 3, out degenerate);
            });
            Assert.Throws<ArgumentNullException>(delegate
            {
                PolygonPlanarity.MaxDeviation(points, null, 0, 3, out degenerate);
            });
            Assert.Throws<ArgumentException>(delegate
            {
                PolygonPlanarity.MaxDeviation(points, faceVertices, 0, 2, out degenerate);
            });
            Assert.Throws<ArgumentException>(delegate
            {
                PolygonPlanarity.MaxDeviation(points, faceVertices, 1, 3, out degenerate);
            });
            Assert.Throws<ArgumentException>(delegate
            {
                PolygonPlanarity.MaxDeviation(points, faceVertices, -1, 3, out degenerate);
            });
            Assert.Throws<ArgumentException>(delegate
            {
                PolygonPlanarity.MaxDeviation(points, new int[] { 0, 1, 3 }, 0, 3, out degenerate);
            });
        }
    }
}
