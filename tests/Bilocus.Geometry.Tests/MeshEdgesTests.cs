// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Geometry;

namespace Bilocus.Geometry.Tests
{
    public class MeshEdgesTests
    {
        // Unit cube with vertices split per face, as Blender sends them:
        // 24 vertices, 12 triangles. Its outline is the 12 cube edges; the
        // 6 face diagonals must not appear.
        private static void Cube(out float[] positions, out int[] indices)
        {
            float[][] corners =
            {
                new float[] { 0, 0, 0 }, new float[] { 1, 0, 0 }, new float[] { 1, 1, 0 }, new float[] { 0, 1, 0 },
                new float[] { 0, 0, 1 }, new float[] { 1, 0, 1 }, new float[] { 1, 1, 1 }, new float[] { 0, 1, 1 },
            };
            int[][] faces =
            {
                new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                new[] { 2, 3, 7, 6 }, new[] { 1, 2, 6, 5 }, new[] { 3, 0, 4, 7 },
            };
            List<float> p = new List<float>();
            List<int> idx = new List<int>();
            foreach (int[] face in faces)
            {
                int start = p.Count / 3;
                foreach (int c in face) { p.AddRange(corners[c]); }
                idx.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            positions = p.ToArray();
            indices = idx.ToArray();
        }

        private static HashSet<string> Segments(float[] positions, int[] lines)
        {
            HashSet<string> result = new HashSet<string>();
            for (int i = 0; i < lines.Length; i += 2)
            {
                string a = Point(positions, lines[i]);
                string b = Point(positions, lines[i + 1]);
                result.Add(string.CompareOrdinal(a, b) < 0 ? a + "-" + b : b + "-" + a);
            }
            return result;
        }

        private static string Point(float[] p, int v)
        {
            return string.Format("{0},{1},{2}", p[v * 3], p[v * 3 + 1], p[v * 3 + 2]);
        }

        [Fact]
        public void Cube_GivesTheTwelveEdgesAndNoDiagonals()
        {
            float[] positions;
            int[] indices;
            Cube(out positions, out indices);

            int[] lines = MeshEdges.FeatureLines(positions, indices, MeshEdges.DefaultAngleDegrees);

            Assert.Equal(24, lines.Length);
            HashSet<string> segments = Segments(positions, lines);
            Assert.Equal(12, segments.Count);
            Assert.DoesNotContain("0,0,0-1,1,0", segments);
        }

        [Fact]
        public void FlatQuad_GivesOnlyTheBorder()
        {
            float[] positions = { 0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0 };
            int[] indices = { 0, 1, 2, 0, 2, 3 };

            int[] lines = MeshEdges.FeatureLines(positions, indices, MeshEdges.DefaultAngleDegrees);

            Assert.Equal(8, lines.Length);
            Assert.DoesNotContain("0,0,0-1,1,0", Segments(positions, lines));
        }

        [Fact]
        public void ShallowFold_IsHidden_SteepFold_IsShown()
        {
            // Two triangles sharing the edge (0,0,0)-(0,1,0), folded by 10
            // and by 45 degrees.
            foreach (double fold in new[] { 10.0, 45.0 })
            {
                double r = fold * Math.PI / 180.0;
                float[] positions =
                {
                    0, 0, 0, 0, 1, 0, -1, 0, 0,
                    0, 0, 0, 0, 1, 0, (float)Math.Cos(r), 0, (float)Math.Sin(r),
                };
                int[] indices = { 0, 1, 2, 3, 4, 5 };

                HashSet<string> segments = Segments(
                    positions, MeshEdges.FeatureLines(positions, indices, MeshEdges.DefaultAngleDegrees));

                Assert.Equal(fold > MeshEdges.DefaultAngleDegrees, segments.Contains("0,0,0-0,1,0"));
            }
        }

        [Fact]
        public void DegenerateTriangles_AreIgnored()
        {
            float[] positions = { 0, 0, 0, 1, 0, 0, 2, 0, 0 };
            int[] indices = { 0, 1, 2 };

            Assert.Empty(MeshEdges.FeatureLines(positions, indices, MeshEdges.DefaultAngleDegrees));
        }
    }
}
