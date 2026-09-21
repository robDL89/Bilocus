// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using Bilocus.Geometry;
using Bilocus.Revit.Preview;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    public class GeometryStoreTests
    {
        private static List<MeshChunk> OneTriangle()
        {
            return MeshChunker.Split(
                new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                new int[] { 0, 1, 2 });
        }

        private static float[] Identity()
        {
            return new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
        }

        [Fact]
        public void UpsertThenGet_ReturnsObject()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Cube", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });

            Assert.Single(store.Objects);
            Assert.Equal("Cube", store.Objects[0].Name);
            Assert.Equal(1, store.TotalTriangles);
        }

        [Fact]
        public void UpsertTwice_ReplacesInsteadOfDuplicating()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Cube", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            store.Upsert("a", "Cube renamed", OneTriangle(), Identity(), new float[] { 0, 1, 0, 1 });

            Assert.Single(store.Objects);
            Assert.Equal("Cube renamed", store.Objects[0].Name);
        }

        [Fact]
        public void SetTransform_DoesNotTouchChunks()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Cube", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            List<MeshChunk> before = store.Objects[0].Chunks;

            float[] moved = Identity();
            moved[3] = 5;
            store.SetTransform("a", moved);

            Assert.Same(before, store.Objects[0].Chunks);
            Assert.Equal(5f, store.Objects[0].Matrix[3]);
            Assert.True(store.Objects[0].TransformRevision > 0);
        }

        [Fact]
        public void SetTransform_OnUnknownId_IsIgnored()
        {
            GeometryStore store = new GeometryStore();
            store.SetTransform("never seen", Identity());
            Assert.Empty(store.Objects);
        }

        [Fact]
        public void Remove_DropsObject()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "Cube", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            store.Remove("a");
            Assert.Empty(store.Objects);
        }

        [Fact]
        public void Clear_DropsEverything()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            store.Upsert("b", "B", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            store.Clear();
            Assert.Empty(store.Objects);
        }

        [Fact]
        public void EndSync_RemovesObjectsNotAnnouncedByBeginSync()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            store.Upsert("b", "B", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });

            store.BeginSync(new List<string> { "a" });
            store.EndSync();

            Assert.Single(store.Objects);
            Assert.Equal("A", store.Objects[0].Name);
        }

        [Fact]
        public void EndSync_WithoutBeginSync_RemovesNothing()
        {
            GeometryStore store = new GeometryStore();
            store.Upsert("a", "A", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            store.EndSync();
            Assert.Single(store.Objects);
        }

        [Fact]
        public void Revision_ChangesOnEveryMutation()
        {
            GeometryStore store = new GeometryStore();
            long start = store.Revision;

            store.Upsert("a", "A", OneTriangle(), Identity(), new float[] { 1, 0, 0, 1 });
            long afterUpsert = store.Revision;
            Assert.True(afterUpsert > start);

            store.SetTransform("a", Identity());
            Assert.True(store.Revision > afterUpsert);
        }
    }
}
