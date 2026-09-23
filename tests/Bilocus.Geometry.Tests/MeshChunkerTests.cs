// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    public class MeshChunkerTests
    {
        // Builds a ribbon of quads along X: two vertices per column, two
        // triangles per quad. Every vertex has a unique position, so it can
        // be verified that the split did not scramble anything.
        private static void BuildRibbon(int columns, out float[] positions, out float[] normals, out int[] indices)
        {
            int vertexCount = columns * 2;
            int triangleCount = (columns - 1) * 2;

            positions = new float[vertexCount * 3];
            normals = new float[vertexCount * 3];
            indices = new int[triangleCount * 3];

            for (int i = 0; i < columns; i++)
            {
                int a = i * 2;
                positions[a * 3] = i;
                positions[a * 3 + 1] = 0;
                positions[a * 3 + 2] = 0;
                positions[(a + 1) * 3] = i;
                positions[(a + 1) * 3 + 1] = 1;
                positions[(a + 1) * 3 + 2] = 0;

                normals[a * 3 + 2] = 1;
                normals[(a + 1) * 3 + 2] = 1;
            }

            int k = 0;
            for (int i = 0; i < columns - 1; i++)
            {
                int a = i * 2;
                indices[k++] = a; indices[k++] = a + 1; indices[k++] = a + 2;
                indices[k++] = a + 1; indices[k++] = a + 3; indices[k++] = a + 2;
            }
        }

        // Rebuilds, for every triangle, its three world positions, walking
        // through all the chunks in order. It is the one check that
        // matters: it says whether the geometry survived the split.
        private static List<float[]> WorldTriangles(IList<MeshChunk> chunks)
        {
            List<float[]> triangles = new List<float[]>();
            foreach (MeshChunk chunk in chunks)
            {
                for (int t = 0; t < chunk.TriangleCount; t++)
                {
                    float[] tri = new float[9];
                    for (int c = 0; c < 3; c++)
                    {
                        int v = chunk.Indices[t * 3 + c];
                        tri[c * 3] = chunk.Positions[v * 3];
                        tri[c * 3 + 1] = chunk.Positions[v * 3 + 1];
                        tri[c * 3 + 2] = chunk.Positions[v * 3 + 2];
                    }
                    triangles.Add(tri);
                }
            }
            return triangles;
        }

        // The limit is NOT a choice of ours: it is DirectContext3D's own
        // index buffer limit, measured in Phase 0
        // (IndexTriangle.GetSizeInShortInts() is 3, i.e. two bytes per
        // index). See DESIGN.md 8, Risks.
        //
        // This test uses the literal 65536 on purpose. Every other test in
        // here compares against MeshChunker.MaxVerticesPerChunk, so under
        // mutation the constant scales along with it and stays green: they
        // are tautological with respect to the value. This is the only one
        // that ties the code to Revit's reality instead of our own
        // intentions.
        [Fact]
        public void MaxVerticesPerChunk_MatchesRevitMeasuredLimit()
        {
            Assert.Equal(65536, MeshChunker.MaxVerticesPerChunk);
        }

        [Fact]
        public void NoChunkExceedsRevitHardLimit()
        {
            float[] positions, normals;
            int[] indices;
            BuildRibbon(40000, out positions, out normals, out indices);

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            Assert.True(chunks.Count >= 2, "an 80000-vertex mesh must be split");
            foreach (MeshChunk chunk in chunks)
            {
                Assert.True(chunk.VertexCount <= 65536,
                    "chunk with " + chunk.VertexCount + " vertices: Revit would truncate it silently");
            }
        }

        [Fact]
        public void EmptyMesh_ProducesNoChunks()
        {
            List<MeshChunk> chunks = MeshChunker.Split(new float[0], new float[0], new int[0]);
            Assert.Empty(chunks);
        }

        [Fact]
        public void SingleTriangle_ProducesOneChunk()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[] { 0, 1, 2 };

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            Assert.Single(chunks);
            Assert.Equal(3, chunks[0].VertexCount);
            Assert.Equal(1, chunks[0].TriangleCount);
            Assert.Equal(new int[] { 0, 1, 2 }, chunks[0].Indices);
        }

        [Fact]
        public void MeshAtExactLimit_StaysInOneChunk()
        {
            float[] positions, normals;
            int[] indices;
            BuildRibbon(MeshChunker.MaxVerticesPerChunk / 2, out positions, out normals, out indices);

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            Assert.Single(chunks);
            Assert.Equal(MeshChunker.MaxVerticesPerChunk, chunks[0].VertexCount);
        }

        [Fact]
        public void MeshOverLimit_IsSplit()
        {
            float[] positions, normals;
            int[] indices;
            BuildRibbon(MeshChunker.MaxVerticesPerChunk / 2 + 100, out positions, out normals, out indices);

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            Assert.True(chunks.Count >= 2);
            foreach (MeshChunk chunk in chunks)
            {
                Assert.True(chunk.VertexCount <= MeshChunker.MaxVerticesPerChunk,
                    "a chunk exceeds the limit: " + chunk.VertexCount);
            }
        }

        [Fact]
        public void SplitPreservesEveryTriangleGeometry()
        {
            float[] positions, normals;
            int[] indices;
            BuildRibbon(MeshChunker.MaxVerticesPerChunk / 2 + 500, out positions, out normals, out indices);

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);
            List<float[]> produced = WorldTriangles(chunks);

            int triangleCount = indices.Length / 3;
            Assert.Equal(triangleCount, produced.Count);

            for (int t = 0; t < triangleCount; t++)
            {
                for (int c = 0; c < 3; c++)
                {
                    int v = indices[t * 3 + c];
                    Assert.Equal(positions[v * 3], produced[t][c * 3]);
                    Assert.Equal(positions[v * 3 + 1], produced[t][c * 3 + 1]);
                    Assert.Equal(positions[v * 3 + 2], produced[t][c * 3 + 2]);
                }
            }
        }

        [Fact]
        public void SplitPreservesNormalsAlongsidePositions()
        {
            float[] positions, normals;
            int[] indices;
            BuildRibbon(MeshChunker.MaxVerticesPerChunk / 2 + 500, out positions, out normals, out indices);

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            foreach (MeshChunk chunk in chunks)
            {
                Assert.Equal(chunk.VertexCount * 3, chunk.Normals.Length);
                for (int v = 0; v < chunk.VertexCount; v++)
                {
                    // the ribbon has all normals (0,0,1)
                    Assert.Equal(0f, chunk.Normals[v * 3]);
                    Assert.Equal(0f, chunk.Normals[v * 3 + 1]);
                    Assert.Equal(1f, chunk.Normals[v * 3 + 2]);
                }
            }
        }

        [Fact]
        public void TriangleNeverStraddlesTwoChunks()
        {
            float[] positions, normals;
            int[] indices;
            BuildRibbon(MeshChunker.MaxVerticesPerChunk / 2 + 500, out positions, out normals, out indices);

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            foreach (MeshChunk chunk in chunks)
            {
                foreach (int index in chunk.Indices)
                {
                    Assert.InRange(index, 0, chunk.VertexCount - 1);
                }
            }
        }

        [Fact]
        public void IndexOutOfRange_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[9];
            int[] indices = new int[] { 0, 1, 7 };

            Assert.Throws<ArgumentException>(() => MeshChunker.Split(positions, normals, indices));
        }

        [Fact]
        public void IndicesNotMultipleOfThree_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[9];
            int[] indices = new int[] { 0, 1 };

            Assert.Throws<ArgumentException>(() => MeshChunker.Split(positions, normals, indices));
        }

        [Fact]
        public void NormalsLengthMismatch_Throws()
        {
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[6];
            int[] indices = new int[] { 0, 1, 2 };

            Assert.Throws<ArgumentException>(() => MeshChunker.Split(positions, normals, indices));
        }

        [Fact]
        public void DegenerateMesh_ManyTrianglesFewVertices_StaysInOneChunk()
        {
            // 10000 triangles reusing the same 3 vertices: the limit is on
            // VERTICES, not triangles, so it must not split
            float[] positions = new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 };
            float[] normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
            int[] indices = new int[10000 * 3];
            for (int t = 0; t < 10000; t++)
            {
                indices[t * 3] = 0; indices[t * 3 + 1] = 1; indices[t * 3 + 2] = 2;
            }

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            Assert.Single(chunks);
            Assert.Equal(3, chunks[0].VertexCount);
            Assert.Equal(10000, chunks[0].TriangleCount);
        }

        // Does the count of new
        // vertices per triangle (in MeshChunker.Split) correctly handle
        // degenerate triangles, i.e. ones with two or three indices equal to
        // each other within the SAME triangle? A count that undershot would
        // leave a chunk above the 65536-vertex limit without raising any
        // exception, exactly the kind of silent bug described in
        // DESIGN.md 8, Risks.
        //
        // This test builds a mesh where EVERY triangle is degenerate in a
        // different way (two equal indices, in every position combination,
        // and one fully degenerate case a==b==c), interleaved with
        // non-degenerate connecting triangles, and pushes it past the
        // 65536-vertex threshold. If the count of "new vertices" per
        // triangle were wrong in the presence of repeated indices within the
        // same triangle, a chunk here would end up exceeding the limit.
        [Fact]
        public void DegenerateTrianglesAtChunkBoundary_NoChunkExceedsLimit()
        {
            // Enough vertices to cross the threshold multiple times with the
            // generation scheme below (each "group" of 4 vertices produces
            // 5 triangles, some of them degenerate).
            int groups = (MeshChunker.MaxVerticesPerChunk / 4) + 200;
            int vertexCount = groups * 4;

            float[] positions = new float[vertexCount * 3];
            float[] normals = new float[vertexCount * 3];
            for (int v = 0; v < vertexCount; v++)
            {
                positions[v * 3] = v;
                positions[v * 3 + 1] = 0;
                positions[v * 3 + 2] = 0;
                normals[v * 3 + 2] = 1;
            }

            List<int> indexList = new List<int>();
            for (int g = 0; g < groups; g++)
            {
                int baseIndex = g * 4;
                int a = baseIndex;
                int b = baseIndex + 1;
                int c = baseIndex + 2;
                int d = baseIndex + 3;

                // Normal, non-degenerate triangle: introduces the group's 4
                // vertices into the global-to-local map.
                indexList.Add(a); indexList.Add(b); indexList.Add(c);

                // Degenerate: two equal indices in the first and second
                // position.
                indexList.Add(a); indexList.Add(a); indexList.Add(d);

                // Degenerate: two equal indices in the first and third
                // position.
                indexList.Add(b); indexList.Add(d); indexList.Add(b);

                // Degenerate: two equal indices in the second and third
                // position.
                indexList.Add(d); indexList.Add(c); indexList.Add(c);

                // Fully degenerate: all three indices equal.
                indexList.Add(d); indexList.Add(d); indexList.Add(d);
            }

            int[] indices = indexList.ToArray();

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);

            Assert.True(chunks.Count >= 2);

            int totalTrianglesProduced = 0;
            foreach (MeshChunk chunk in chunks)
            {
                Assert.True(chunk.VertexCount <= MeshChunker.MaxVerticesPerChunk,
                    "a chunk exceeds the limit with degenerate triangles: " + chunk.VertexCount);
                foreach (int index in chunk.Indices)
                {
                    Assert.InRange(index, 0, chunk.VertexCount - 1);
                }
                totalTrianglesProduced += chunk.TriangleCount;
            }

            Assert.Equal(indices.Length / 3, totalTrianglesProduced);
        }
    }
}
