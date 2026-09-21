// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Revit.Pull;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The element's origin and the translation of vertices into LOCAL
    // coordinates. This is the pure part of tessellation: no Element, no
    // Revit API, only float arrays. It lives on TessellatedMesh precisely
    // because TessellatedMesh is already compiled here without RevitAPI
    // available.
    //
    // The defect this logic fixes: with vertices in world coordinates and the
    // Blender object at an identity matrix, the object's origin ends up at
    // (0,0,0) and a rotation in Blender rotates the element around the
    // scene's origin instead of on itself.
    public class TessellatedMeshTests
    {
        private const float Tol = 1e-5f;

        private static float[] Normals(int vertexCount)
        {
            float[] normals = new float[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++) { normals[i * 3 + 2] = 1f; }
            return normals;
        }

        private static int[] Sequential(int vertexCount)
        {
            int[] indices = new int[vertexCount];
            for (int i = 0; i < vertexCount; i++) { indices[i] = i; }
            return indices;
        }

        // --- ComputeOrigin ---------------------------------------------------

        [Fact]
        public void ComputeOrigin_IsTheCenterOfTheBoundingBox()
        {
            float[] positions = new float[]
            {
                10f, 20f, 30f,
                12f, 20f, 30f,
                10f, 22f, 30f
            };

            float[] origin = TessellatedMesh.ComputeOrigin(positions);

            Assert.Equal(11f, origin[0], 5);
            Assert.Equal(21f, origin[1], 5);
            Assert.Equal(30f, origin[2], 5);
        }

        // Bounding box center, NOT centroid of the vertices: a tessellated
        // wall has far more vertices around a door than over the rest of its
        // length, and a centroid would follow the density of the
        // tessellation instead of the bounding volume.
        [Fact]
        public void ComputeOrigin_IsTheBoundingBoxCenterNotTheCentroidOfTheVertices()
        {
            float[] positions = new float[]
            {
                0f, 0f, 0f,
                0f, 0f, 0f,
                0f, 0f, 0f,
                10f, 0f, 0f
            };

            float[] origin = TessellatedMesh.ComputeOrigin(positions);

            Assert.Equal(5f, origin[0], 5);
        }

        [Fact]
        public void ComputeOrigin_HandlesNegativeCoordinates()
        {
            float[] positions = new float[]
            {
                -4f, -10f, -1f,
                2f, 6f, 5f
            };

            float[] origin = TessellatedMesh.ComputeOrigin(positions);

            Assert.Equal(-1f, origin[0], 5);
            Assert.Equal(-2f, origin[1], 5);
            Assert.Equal(2f, origin[2], 5);
        }

        // An element with no vertices has no bounding box. DECISION: origin
        // (0,0,0). The mesh is empty regardless and is skipped downstream by
        // SendSelectionCommand, so that origin never reaches the wire: it is
        // declared so it does not stay implicit, not because it is needed.
        [Fact]
        public void ComputeOrigin_OfNoVerticesIsZero()
        {
            float[] origin = TessellatedMesh.ComputeOrigin(new float[0]);

            Assert.Equal(3, origin.Length);
            Assert.Equal(0f, origin[0]);
            Assert.Equal(0f, origin[1]);
            Assert.Equal(0f, origin[2]);
        }

        [Fact]
        public void ComputeOrigin_OfASinglePointIsThatPoint()
        {
            float[] origin = TessellatedMesh.ComputeOrigin(new float[] { 7f, -3f, 0.5f });

            Assert.Equal(7f, origin[0], 5);
            Assert.Equal(-3f, origin[1], 5);
            Assert.Equal(0.5f, origin[2], 5);
        }

        [Fact]
        public void ComputeOrigin_NullPositions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => TessellatedMesh.ComputeOrigin(null));
        }

        [Fact]
        public void ComputeOrigin_PositionsNotMultipleOfThree_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TessellatedMesh.ComputeOrigin(new float[] { 1f, 2f }));
        }

        // --- FromWorldSpace --------------------------------------------------

        [Fact]
        public void FromWorldSpace_MovesPositionsIntoLocalCoordinates()
        {
            float[] positions = new float[]
            {
                10f, 20f, 30f,
                12f, 20f, 30f,
                10f, 22f, 30f
            };

            TessellatedMesh mesh = TessellatedMesh.FromWorldSpace(
                positions, Normals(3), Sequential(3));

            Assert.Equal(11f, mesh.Origin[0], 5);
            Assert.Equal(21f, mesh.Origin[1], 5);
            Assert.Equal(30f, mesh.Origin[2], 5);

            Assert.Equal(-1f, mesh.Positions[0], 5);
            Assert.Equal(-1f, mesh.Positions[1], 5);
            Assert.Equal(0f, mesh.Positions[2], 5);

            Assert.Equal(1f, mesh.Positions[3], 5);
            Assert.Equal(-1f, mesh.Positions[4], 5);

            Assert.Equal(-1f, mesh.Positions[6], 5);
            Assert.Equal(1f, mesh.Positions[7], 5);
        }

        // The round trip is the guarantee that matters to the user: adding
        // the origin back to the local vertices returns exactly where Revit
        // placed the element, i.e. obj.location = origin puts the geometry
        // back in its place in the world.
        [Fact]
        public void FromWorldSpace_AddingTheOriginBackRecoversTheWorldPositions()
        {
            float[] world = new float[]
            {
                123.5f, -47.25f, 3.125f,
                125.5f, -47.25f, 3.125f,
                123.5f, -45.25f, 6.125f
            };
            float[] copy = (float[])world.Clone();

            TessellatedMesh mesh = TessellatedMesh.FromWorldSpace(
                copy, Normals(3), Sequential(3));

            for (int i = 0; i < world.Length; i++)
            {
                float restored = mesh.Positions[i] + mesh.Origin[i % 3];
                Assert.True(Math.Abs(restored - world[i]) < Tol,
                    string.Format("vertex {0}: expected {1}, got {2}", i, world[i], restored));
            }
        }

        // A translation does not touch directions. If the normals changed,
        // the imported object's shading would change with the mere distance
        // of the element from the project's origin.
        [Fact]
        public void FromWorldSpace_LeavesNormalsUntouched()
        {
            float[] normals = new float[] { 0f, 0f, 1f, 0.6f, 0f, 0.8f, 0f, -1f, 0f };

            TessellatedMesh mesh = TessellatedMesh.FromWorldSpace(
                new float[] { 5f, 5f, 5f, 7f, 5f, 5f, 5f, 7f, 5f },
                (float[])normals.Clone(),
                Sequential(3));

            for (int i = 0; i < normals.Length; i++)
            {
                Assert.Equal(normals[i], mesh.Normals[i], 6);
            }
        }

        [Fact]
        public void FromWorldSpace_LeavesIndicesUntouched()
        {
            TessellatedMesh mesh = TessellatedMesh.FromWorldSpace(
                new float[] { 1f, 1f, 1f, 2f, 1f, 1f, 1f, 2f, 1f },
                Normals(3),
                new int[] { 0, 1, 2 });

            Assert.Equal(new int[] { 0, 1, 2 }, mesh.Indices);
        }

        // An element already centered on the project's origin must not move:
        // origin (0,0,0) and vertices identical to the world ones.
        [Fact]
        public void FromWorldSpace_MeshAlreadyCenteredKeepsItsPositions()
        {
            float[] positions = new float[] { -1f, -1f, 0f, 1f, -1f, 0f, -1f, 1f, 0f };

            TessellatedMesh mesh = TessellatedMesh.FromWorldSpace(
                (float[])positions.Clone(), Normals(3), Sequential(3));

            Assert.Equal(0f, mesh.Origin[0], 5);
            Assert.Equal(0f, mesh.Origin[1], 5);
            Assert.Equal(0f, mesh.Origin[2], 5);
            for (int i = 0; i < positions.Length; i++)
            {
                Assert.Equal(positions[i], mesh.Positions[i], 5);
            }
        }

        [Fact]
        public void FromWorldSpace_OfAnEmptyMeshIsEmptyWithZeroOrigin()
        {
            TessellatedMesh mesh = TessellatedMesh.FromWorldSpace(
                new float[0], new float[0], new int[0]);

            Assert.True(mesh.IsEmpty);
            Assert.Equal(3, mesh.Origin.Length);
            Assert.Equal(0f, mesh.Origin[0]);
            Assert.Equal(0f, mesh.Origin[1]);
            Assert.Equal(0f, mesh.Origin[2]);
        }

        // --- costruttore e Empty ---------------------------------------------

        [Fact]
        public void Empty_HasAZeroOrigin()
        {
            TessellatedMesh mesh = TessellatedMesh.Empty;

            Assert.Equal(3, mesh.Origin.Length);
            Assert.Equal(0f, mesh.Origin[0]);
            Assert.Equal(0f, mesh.Origin[1]);
            Assert.Equal(0f, mesh.Origin[2]);
        }

        [Fact]
        public void Constructor_NullOrigin_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new TessellatedMesh(
                new float[] { 0, 0, 0 }, new float[] { 0, 0, 1 }, new int[0], null));
        }

        [Fact]
        public void Constructor_OriginOfWrongLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => new TessellatedMesh(
                new float[] { 0, 0, 0 }, new float[] { 0, 0, 1 }, new int[0],
                new float[] { 1f, 2f }));
        }

        [Fact]
        public void Constructor_KeepsTheOriginItWasGiven()
        {
            TessellatedMesh mesh = new TessellatedMesh(
                new float[] { 0, 0, 0 }, new float[] { 0, 0, 1 }, new int[0],
                new float[] { 1.5f, -2.5f, 3.5f });

            Assert.Equal(1.5f, mesh.Origin[0]);
            Assert.Equal(-2.5f, mesh.Origin[1]);
            Assert.Equal(3.5f, mesh.Origin[2]);
        }
    }
}
