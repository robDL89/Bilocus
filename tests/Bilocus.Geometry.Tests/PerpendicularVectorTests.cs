// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // A Revit ModelCurve needs a SketchPlane, and a plane needs a normal
    // perpendicular to the line's direction. The naive cross product
    // between direction and the Z axis produces the null vector when the
    // direction is parallel to Z, i.e. for every vertical edge. These tests
    // verify that the normal stays valid (non-null, perpendicular, unit
    // length) in every case, the vertical one included.
    public class PerpendicularVectorTests
    {
        private const float Tolerance = 1e-4f;

        // Verifies the two properties that must hold for any computed
        // normal: it is perpendicular to the direction (null dot product,
        // on the normalized direction) and has unit length.
        private static void AssertIsValidPerpendicular(float[] direction, float[] normal)
        {
            Assert.Equal(3, normal.Length);

            double dirLength = Length(direction);
            double dx = direction[0] / dirLength;
            double dy = direction[1] / dirLength;
            double dz = direction[2] / dirLength;

            double dot = dx * normal[0] + dy * normal[1] + dz * normal[2];
            Assert.True(Math.Abs(dot) < Tolerance, "dot product = " + dot);

            double normalLength = Length(normal);
            Assert.True(Math.Abs(normalLength - 1.0) < Tolerance, "normal length = " + normalLength);
        }

        private static double Length(float[] v)
        {
            return Math.Sqrt((double)v[0] * v[0] + (double)v[1] * v[1] + (double)v[2] * v[2]);
        }

        [Fact]
        public void DirectionAlongX_NormalIsPerpendicularAndUnit()
        {
            float[] direction = new float[] { 1f, 0f, 0f };
            float[] normal = PerpendicularVector.Compute(direction);
            AssertIsValidPerpendicular(direction, normal);
        }

        [Fact]
        public void DirectionAlongY_NormalIsPerpendicularAndUnit()
        {
            float[] direction = new float[] { 0f, 1f, 0f };
            float[] normal = PerpendicularVector.Compute(direction);
            AssertIsValidPerpendicular(direction, normal);
        }

        // The case that breaks the naive cross(direction, Z) implementation:
        // with a direction parallel to Z the cross product is the null
        // vector. Half of all edges, in architecture, are vertical.
        [Fact]
        public void DirectionAlongZ_NormalIsPerpendicularAndUnit()
        {
            float[] direction = new float[] { 0f, 0f, 1f };
            float[] normal = PerpendicularVector.Compute(direction);
            AssertIsValidPerpendicular(direction, normal);
        }

        [Fact]
        public void DiagonalDirection_NormalIsPerpendicularAndUnit()
        {
            float v = 1f / (float)Math.Sqrt(3.0);
            float[] direction = new float[] { v, v, v };
            float[] normal = PerpendicularVector.Compute(direction);
            AssertIsValidPerpendicular(direction, normal);
        }

        // Direction nearly parallel to Z: the normal must stay stable, not
        // degenerate into something tiny or unstable.
        [Fact]
        public void DirectionNearlyParallelToZ_NormalStaysStable()
        {
            float[] direction = new float[] { 0.001f, 0f, 1f };
            float[] normal = PerpendicularVector.Compute(direction);
            AssertIsValidPerpendicular(direction, normal);
        }

        [Fact]
        public void NullDirection_Throws()
        {
            float[] direction = new float[] { 0f, 0f, 0f };
            Assert.Throws<ArgumentException>(() => PerpendicularVector.Compute(direction));
        }

        // The input direction does not have to be normalized: the
        // computation must work all the same.
        [Fact]
        public void UnnormalizedDirection_Works()
        {
            float[] direction = new float[] { 3f, 0f, 4f };
            float[] normal = PerpendicularVector.Compute(direction);
            AssertIsValidPerpendicular(direction, normal);
        }

        [Fact]
        public void NullArgument_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => PerpendicularVector.Compute(null));
        }

        // Systematically scans many directions over a sphere, at regular
        // angle steps (axial directions and ones nearly parallel to Z
        // included), and verifies the two properties on all of them. It is
        // the best way to uncover an unforeseen degenerate case.
        [Fact]
        public void SystematicSphereScan_EveryDirectionProducesAValidNormal()
        {
            int checkedCount = 0;
            for (int thetaStep = 0; thetaStep <= 18; thetaStep++)
            {
                double theta = thetaStep * (Math.PI / 18.0); // 0..pi, 10-degree step
                for (int phiStep = 0; phiStep < 36; phiStep++)
                {
                    double phi = phiStep * (2.0 * Math.PI / 36.0); // 0..2pi, 10-degree step

                    double x = Math.Sin(theta) * Math.Cos(phi);
                    double y = Math.Sin(theta) * Math.Sin(phi);
                    double z = Math.Cos(theta);

                    float[] direction = new float[] { (float)x, (float)y, (float)z };

                    // The pole (theta = 0 or pi) collapses every phi onto
                    // the same point: still a valid, non-null direction, and
                    // it must be tested like the others.
                    float[] normal = PerpendicularVector.Compute(direction);
                    AssertIsValidPerpendicular(direction, normal);
                    checkedCount++;
                }
            }

            Assert.True(checkedCount > 300, "samples scanned = " + checkedCount);
        }
    }
}
