// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Geometry;
using Bilocus.Revit.Preview;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // Bounding box of the preview. All in METERS and world coordinates: the
    // conversion to feet and to Outline lives in PreviewServer, which is not
    // compiled here because it touches the Revit API.
    public class GeometryBoundsTests
    {
        private const double Tol = 1e-4;

        private static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        private static float[] Translation(float tx, float ty, float tz)
        {
            return new float[] { 1, 0, 0, tx, 0, 1, 0, ty, 0, 0, 1, tz, 0, 0, 0, 1 };
        }

        // Row-major, as on the wire: row 0 produces world x.
        private static float[] RotationZ(double degrees)
        {
            double r = degrees * Math.PI / 180.0;
            float c = (float)Math.Cos(r);
            float s = (float)Math.Sin(r);
            return new float[]
            {
                c, -s, 0, 0,
                s,  c, 0, 0,
                0,  0, 1, 0,
                0,  0, 0, 1
            };
        }

        // A chunk with the given vertices and an arbitrary triangle: the
        // bounding box only cares about positions.
        private static List<MeshChunk> ChunkOf(params float[] positions)
        {
            float[] normals = new float[positions.Length];
            for (int i = 2; i < normals.Length; i += 3) { normals[i] = 1f; }
            return new List<MeshChunk>
            {
                new MeshChunk(positions, normals, new int[] { 0, 1, 2 })
            };
        }

        // The 8 vertices of the axis-aligned box between min and max.
        private static List<MeshChunk> BoxOf(
            float minX, float minY, float minZ,
            float maxX, float maxY, float maxZ)
        {
            return ChunkOf(
                minX, minY, minZ,
                maxX, minY, minZ,
                maxX, maxY, minZ,
                minX, maxY, minZ,
                minX, minY, maxZ,
                maxX, minY, maxZ,
                maxX, maxY, maxZ,
                minX, maxY, maxZ);
        }

        private static void AssertBounds(
            float[] actual,
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ)
        {
            Assert.NotNull(actual);
            Assert.Equal(6, actual.Length);
            Assert.Equal(minX, actual[0], Tol);
            Assert.Equal(minY, actual[1], Tol);
            Assert.Equal(minZ, actual[2], Tol);
            Assert.Equal(maxX, actual[3], Tol);
            Assert.Equal(maxY, actual[4], Tol);
            Assert.Equal(maxZ, actual[5], Tol);
        }

        [Fact]
        public void EmptyStore_ReturnsNull()
        {
            GeometryStore store = new GeometryStore();
            Assert.Null(store.GetWorldBounds());
        }

        [Fact]
        public void SingleObject_Identity_ReturnsLocalBox()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Box", BoxOf(-1, -2, -3, 4, 5, 6), Identity(), null);

            AssertBounds(store.GetWorldBounds(), -1, -2, -3, 4, 5, 6);
        }

        [Fact]
        public void SingleObject_Translated_ShiftsBox()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Box", BoxOf(0, 0, 0, 1, 1, 1), Translation(10, -20, 30), null);

            AssertBounds(store.GetWorldBounds(), 10, -20, 30, 11, -19, 31);
        }

        // The case that exposes the shortcut: if only the two corners
        // (-1,-1,-1) and (1,1,1) of a box rotated 45 degrees on Z were
        // transformed, their images would both have x = 0 and the world box
        // would come out flat on x. The true result is +/- sqrt(2) on x and y.
        [Fact]
        public void RotatedFortyFiveDegrees_ExpandsBox_NotJustTwoCorners()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Box", BoxOf(-1, -1, -1, 1, 1, 1), RotationZ(45), null);

            double d = Math.Sqrt(2.0);
            AssertBounds(store.GetWorldBounds(), -d, -d, -1, d, d, 1);
        }

        [Fact]
        public void RotatedAndTranslated_CombinesBoth()
        {
            GeometryStore store = new GeometryStore();
            float[] m = RotationZ(45);
            m[3] = 5;
            m[7] = -5;
            m[11] = 2;
            store.Upsert("a", "Box", BoxOf(-1, -1, -1, 1, 1, 1), m, null);

            double d = Math.Sqrt(2.0);
            AssertBounds(store.GetWorldBounds(), 5 - d, -5 - d, 1, 5 + d, -5 + d, 3);
        }

        [Fact]
        public void MultipleObjects_ReturnsUnion()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(0, 0, 0, 1, 1, 1), Identity(), null);
            store.Upsert("b", "B", BoxOf(0, 0, 0, 1, 1, 1), Translation(10, 10, 10), null);
            store.Upsert("c", "C", BoxOf(0, 0, 0, 1, 1, 1), Translation(-5, 0, 0), null);

            AssertBounds(store.GetWorldBounds(), -5, 0, 0, 11, 11, 11);
        }

        [Fact]
        public void BoundsSpanAllChunks()
        {
            GeometryStore store = new GeometryStore();
            List<MeshChunk> chunks = new List<MeshChunk>();
            chunks.AddRange(BoxOf(0, 0, 0, 1, 1, 1));
            chunks.AddRange(BoxOf(-7, 0, 0, 0, 2, 9));
            store.Upsert("a", "A", chunks, Identity(), null);

            AssertBounds(store.GetWorldBounds(), -7, 0, 0, 1, 2, 9);
        }

        [Fact]
        public void ObjectWithoutVertices_IsIgnored()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(0, 0, 0, 1, 1, 1), Identity(), null);
            store.Upsert("vuoto", "Vuoto", new List<MeshChunk>(), Translation(100, 100, 100), null);

            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 1, 1, 1);
        }

        [Fact]
        public void AllObjectsWithoutVertices_ReturnsNull()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("vuoto", "Vuoto", new List<MeshChunk>(), Identity(), null);

            Assert.Null(store.GetWorldBounds());
        }

        [Fact]
        public void SetTransform_UpdatesBounds()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(0, 0, 0, 1, 1, 1), Identity(), null);
            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 1, 1, 1);

            store.SetTransform("a", Translation(3, 0, 0));

            AssertBounds(store.GetWorldBounds(), 3, 0, 0, 4, 1, 1);
        }

        [Fact]
        public void RemoveAndClear_UpdateBounds()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(0, 0, 0, 1, 1, 1), Identity(), null);
            store.Upsert("b", "B", BoxOf(0, 0, 0, 1, 1, 1), Translation(10, 0, 0), null);
            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 11, 1, 1);

            store.Remove("b");
            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 1, 1, 1);

            store.Clear();
            Assert.Null(store.GetWorldBounds());
        }

        // The cache must not return a stale result after an Upsert that
        // replaces the geometry while leaving the matrix unchanged.
        [Fact]
        public void UpsertReplacingGeometry_RecomputesBounds()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(0, 0, 0, 1, 1, 1), Identity(), null);
            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 1, 1, 1);

            store.Upsert("a", "A", BoxOf(-4, -4, -4, 2, 2, 2), null, null);

            AssertBounds(store.GetWorldBounds(), -4, -4, -4, 2, 2, 2);
        }

        [Fact]
        public void RepeatedCalls_ReturnEqualResults()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(-1, -1, -1, 1, 1, 1), RotationZ(45), null);

            float[] first = store.GetWorldBounds();
            float[] second = store.GetWorldBounds();

            Assert.Equal(first, second);
        }

        // Whoever receives the box must not be able to corrupt the store's cache.
        [Fact]
        public void MutatingReturnedArray_DoesNotCorruptStore()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", BoxOf(0, 0, 0, 1, 1, 1), Identity(), null);

            float[] first = store.GetWorldBounds();
            first[0] = -999f;

            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 1, 1, 1);
        }

        [Fact]
        public void StoredObject_WithoutChunks_HasNoBounds()
        {
            StoredObject item = new StoredObject("a");

            Assert.False(item.HasLocalBounds);
            Assert.Null(item.GetWorldBounds());
        }

        [Fact]
        public void StoredObject_ReassigningChunks_RecomputesLocalBounds()
        {
            StoredObject item = new StoredObject("a");
            item.Chunks = BoxOf(0, 0, 0, 1, 1, 1);
            Assert.True(item.HasLocalBounds);
            AssertBounds(item.GetWorldBounds(), 0, 0, 0, 1, 1, 1);

            item.Chunks = BoxOf(5, 5, 5, 6, 6, 6);

            AssertBounds(item.GetWorldBounds(), 5, 5, 5, 6, 6, 6);
        }

        // A flat mesh is legitimate: the box degenerate on one axis must
        // not become null or blow up.
        [Fact]
        public void FlatObject_ReturnsDegenerateBoxNotNull()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Plane", ChunkOf(0, 0, 0, 1, 0, 0, 0, 1, 0), Identity(), null);

            AssertBounds(store.GetWorldBounds(), 0, 0, 0, 1, 1, 0);
        }
    }
}
