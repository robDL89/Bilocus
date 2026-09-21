// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;

namespace Bilocus.Geometry
{
    // The faces of an object ready for the TessellatedShapeBuilder, as
    // vertex indices.
    //
    // This is decision 5 of the Phase B plan: planar quads and n-gons stay
    // whole faces, triangulation is the fallback for the SINGLE non-planar
    // polygon, never for the whole object. A cube with one skewed face
    // arrives in Revit as five square faces and two triangles, not as twelve
    // triangles with visible diagonals on every face.
    //
    // The fallback triangles are Blender's own (loop_triangles), not a
    // triangulation redone here. They are the same ones the user sees in the
    // viewport: a triangulation of our own, on a concave or skewed polygon,
    // could pick different diagonals, i.e. a different shape.
    //
    // No XYZ on the way out: this project knows nothing about the Revit API.
    // The builder converts world points to feet and builds the
    // TessellatedFace objects from the loops.
    public sealed class BakeFaceSet
    {
        // One array per face to pass to the builder, polygon by polygon: one
        // whole loop for a polygon kept intact, size-2 loops of three for a
        // fallback one. New arrays, not views onto the payload.
        public List<int[]> Loops { get; private set; }

        // Polygons kept whole. Three-vertex polygons count here too.
        public int PlanarCount { get; private set; }

        // Polygons folded back onto Blender's triangles: it's the polygons
        // that are counted, not the triangles produced.
        public int TriangulatedCount { get; private set; }

        private BakeFaceSet(List<int[]> loops, int planarCount, int triangulatedCount)
        {
            Loops = loops;
            PlanarCount = planarCount;
            TriangulatedCount = triangulatedCount;
        }

        // worldPoints: the payload positions already transformed to world
        // meters (RowMajorMatrix.TransformPoints). Planarity is measured
        // there and not on local coordinates: a non-uniform scale changes
        // the deviation.
        //
        // planarToleranceMeters: the maximum distance of a vertex from the
        // plane for the polygon to stay whole. The caller decides it,
        // starting from Revit's own tolerance.
        //
        // flipWinding: reverses the order of every loop. Needed when the
        // matrix has a negative determinant: a mirrored scale flips the
        // normals, and without the reversal the faces would arrive in Revit
        // facing inward.
        public static BakeFaceSet Build(double[] worldPoints, BakeMeshPayload mesh,
            double planarToleranceMeters, bool flipWinding)
        {
            if (worldPoints == null) throw new ArgumentNullException("worldPoints");
            if (mesh == null) throw new ArgumentNullException("mesh");
            if (worldPoints.Length != mesh.Positions.Length)
            {
                throw new ArgumentException(string.Format(
                    "worldPoints is {0} long but the payload has {1} coordinates",
                    worldPoints.Length, mesh.Positions.Length));
            }
            if (double.IsNaN(planarToleranceMeters) || double.IsInfinity(planarToleranceMeters)
                || planarToleranceMeters < 0)
            {
                throw new ArgumentException(
                    "invalid planarity tolerance: " + planarToleranceMeters);
            }

            // The payload's invariants (indices in range, size-2 triangles
            // per polygon) are guaranteed by BakeMeshPayload.Parse, the only
            // way to build one: they are not re-checked here.
            int[] triStart;
            int[] trianglesByFace;
            GroupTrianglesByFace(mesh, out triStart, out trianglesByFace);

            List<int[]> loops = new List<int[]>(mesh.FaceCount);
            int planar = 0;
            int triangulated = 0;
            int start = 0;

            for (int face = 0; face < mesh.FaceCount; face++)
            {
                int size = mesh.FaceSizes[face];

                // A triangle is planar by definition, and its fallback would
                // be itself: no measurement needed. Even if degenerate it
                // stays as is, and if Revit rejects it the builder skips it
                // and counts it.
                bool keepWhole = size == 3;
                if (!keepWhole)
                {
                    bool degenerate;
                    double deviation = PolygonPlanarity.MaxDeviation(
                        worldPoints, mesh.FaceVertices, start, size, out degenerate);
                    keepWhole = !degenerate && deviation <= planarToleranceMeters;
                }

                if (keepWhole)
                {
                    int[] loop = new int[size];
                    Array.Copy(mesh.FaceVertices, start, loop, 0, size);
                    if (flipWinding) { Array.Reverse(loop); }
                    loops.Add(loop);
                    planar++;
                }
                else
                {
                    for (int k = triStart[face]; k < triStart[face + 1]; k++)
                    {
                        int at = trianglesByFace[k] * 3;
                        int[] loop = new int[]
                        {
                            mesh.TriVertices[at], mesh.TriVertices[at + 1], mesh.TriVertices[at + 2]
                        };
                        if (flipWinding) { Array.Reverse(loop); }
                        loops.Add(loop);
                    }
                    triangulated++;
                }

                start += size;
            }

            return new BakeFaceSet(loops, planar, triangulated);
        }

        // Groups triangles by polygon with a count, in O(tri).
        //
        // Blender does not promise that loop_triangles is ordered by
        // polygon, and the contract does not require it: assuming so would
        // work on test cubes and fail silently on the first modified mesh.
        // The grouping is stable, within each polygon the triangles keep
        // their arrival order, so the same payload always yields the same
        // faces.
        //
        // trianglesByFace[triStart[f] .. triStart[f + 1]) are the triangle
        // indices of polygon f.
        private static void GroupTrianglesByFace(BakeMeshPayload mesh, out int[] triStart, out int[] trianglesByFace)
        {
            int faceCount = mesh.FaceCount;
            int triCount = mesh.TriangleCount;

            triStart = new int[faceCount + 1];
            for (int t = 0; t < triCount; t++)
            {
                triStart[mesh.TriFaces[t] + 1]++;
            }
            for (int f = 0; f < faceCount; f++)
            {
                triStart[f + 1] += triStart[f];
            }

            int[] cursor = new int[faceCount];
            Array.Copy(triStart, cursor, faceCount);

            trianglesByFace = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                int face = mesh.TriFaces[t];
                trianglesByFace[cursor[face]] = t;
                cursor[face]++;
            }
        }
    }
}
