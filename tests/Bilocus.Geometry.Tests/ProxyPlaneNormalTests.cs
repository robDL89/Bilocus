// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // The sketch-plane normals for proxies: a beam taken with Pick Lines
    // orients its section on the curve's plane, so the plane decides
    // whether the beam rolls.
    public class ProxyPlaneNormalTests
    {
        private static void AssertVector(double x, double y, double z, double[] actual)
        {
            Assert.Equal(x, actual[0], 9);
            Assert.Equal(y, actual[1], 9);
            Assert.Equal(z, actual[2], 9);
        }

        [Fact]
        public void ForLine_Horizontal_IsUp()
        {
            AssertVector(0, 0, 1, ProxyPlaneNormal.ForLine(new double[] { 3, 4, 0 }));
        }

        // The case that used to roll: a steep line. The plane must contain
        // the horizontal perpendicular to the line, not just any axis.
        [Fact]
        public void ForLine_Steep_ContainsTheHorizontalPerpendicular()
        {
            double[] direction = { 1, 0, 2 };
            double[] normal = ProxyPlaneNormal.ForLine(direction);

            // perpendicular to the line
            Assert.Equal(0, direction[0] * normal[0] + direction[1] * normal[1] + direction[2] * normal[2], 9);
            // perpendicular to the horizontal (0,1,0) that lies in the plane
            Assert.Equal(0, normal[1], 9);
            Assert.True(normal[2] > 0);
            Assert.Equal(1, Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]), 9);
        }

        [Fact]
        public void ForLine_Downhill_StillPointsUp()
        {
            double[] normal = ProxyPlaneNormal.ForLine(new double[] { 1, 1, -1 });
            Assert.True(normal[2] > 0);
        }

        [Fact]
        public void ForLine_Vertical_HasNoPlanPlane()
        {
            Assert.Null(ProxyPlaneNormal.ForLine(new double[] { 0, 0, -2 }));
        }

        [Fact]
        public void ForLine_Zero_Throws()
        {
            Assert.Throws<ArgumentException>(() => ProxyPlaneNormal.ForLine(new double[] { 0, 0, 0 }));
        }

        // The arcs of an S traveled in opposite directions: the same
        // convention.
        [Fact]
        public void Orient_Down_IsFlippedUp()
        {
            AssertVector(0.1, -0.2, 0.97, ProxyPlaneNormal.Orient(new double[] { -0.1, 0.2, -0.97 }));
        }

        [Fact]
        public void Orient_Up_IsUnchanged()
        {
            AssertVector(0.43, 0.47, 0.77, ProxyPlaneNormal.Orient(new double[] { 0.43, 0.47, 0.77 }));
        }

        // Vertical plane with float32 noise on Z: X decides, not the sign of
        // the noise.
        [Fact]
        public void Orient_VerticalPlane_UsesPositiveX()
        {
            AssertVector(0.707, 0.707, 1e-5, ProxyPlaneNormal.Orient(new double[] { -0.707, -0.707, -1e-5 }));
            AssertVector(0.707, -0.707, -1e-5, ProxyPlaneNormal.Orient(new double[] { 0.707, -0.707, -1e-5 }));
        }

        [Fact]
        public void Orient_VerticalPlaneAlongX_UsesPositiveY()
        {
            AssertVector(0, 1, 0, ProxyPlaneNormal.Orient(new double[] { 0, -1, 0 }));
        }
    }
}
