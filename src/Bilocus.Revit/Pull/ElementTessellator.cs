// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Bilocus.Protocol;

namespace Bilocus.Revit.Pull
{
    // From Element to float array. This is the piece that is not unit
    // testable, because without Revit in memory there is no Element to
    // tessellate: so it is kept thin, and everything that can be decided
    // elsewhere (name, color, packing) lives elsewhere.
    public static class ElementTessellator
    {
        // Level of detail for Face.Triangulate, from 0 to 1. This needs to be
        // reference geometry to model against, not a rendering: 1.0 is the
        // right choice because chord error shows up immediately on anything
        // curved (columns, pipes, fillets, handles) while on a straight wall
        // it changes nothing, flat faces stay two triangles at any value. The
        // cost is only paid where it is actually needed.
        private const double LevelOfDetail = 1.0;

        // Recursion limit on nested GeometryInstance. Real families stop at
        // two or three levels; the limit is not an estimate of the worst case
        // but a safety net against a cycle, which would block Revit.
        private const int MaxInstanceDepth = 8;

        // Below this length the cross product no longer defines a reliable
        // direction. The cross product's length equals twice the triangle's
        // area, in square meters: 1e-12 corresponds to a triangle with a side
        // under a micron, which carries no information.
        private const double MinCrossLength = 1e-12;

        // Returns an empty TessellatedMesh when the element has no 3d
        // geometry: this is the normal case for levels, grids, views and
        // annotations, for which get_Geometry returns null by documentation
        // ("null will be returned for symbols, annotations or details").
        //
        // A real failure, instead, propagates as an exception: there is no
        // catch in here. This way the caller distinguishes the two cases
        // without guessing - empty means "nothing to send", an exception
        // means "something went wrong" - and can skip the single element
        // while continuing with the others.
        public static TessellatedMesh Tessellate(Element element)
        {
            if (element == null) { return TessellatedMesh.Empty; }

            GeometryElement geometry = element.get_Geometry(CreateOptions());
            if (geometry == null) { return TessellatedMesh.Empty; }

            List<float> positions = new List<float>();
            List<float> normals = new List<float>();

            Collect(geometry, positions, normals, 0);

            // Non-indexed triangles: the vertices are already emitted three
            // per triangle, so the indices are the natural sequence.
            int[] indices = new int[positions.Count / 3];
            for (int i = 0; i < indices.Length; i++) { indices[i] = i; }

            // Here the vertices are still in world coordinates: FromWorldSpace
            // computes the bounding box center and translates them into local
            // coordinates. The calculation lives there, on TessellatedMesh,
            // because it is pure logic and needs testing; this file is not
            // unit testable.
            return TessellatedMesh.FromWorldSpace(
                positions.ToArray(), normals.ToArray(), indices);
        }

        private static Options CreateOptions()
        {
            Options options = new Options();

            // No View, deliberately. Options.View and Options.DetailLevel are
            // mutually exclusive by documentation, and a view would give
            // view-specific geometry: the same element would produce
            // different meshes depending on what is open at that moment. A
            // reference needs deterministic model geometry.
            options.DetailLevel = ViewDetailLevel.Fine;
            options.ComputeReferences = false;
            options.IncludeNonVisibleObjects = false;
            return options;
        }

        // The instanceable path (spec 2026-10-04, section 2.1): returns the
        // symbol's mesh in the family's LOCAL space, its key and the
        // instance's matrix, or null when the element must take the flat
        // path of Tessellate.
        //
        // Instanceable means: the top-level geometry is EXACTLY ONE
        // GeometryInstance and nothing else that produces triangles. Revit
        // already returns a cut or joined family instance as plain solids,
        // so those fall out here on their own; empty solids, curves and
        // points next to the instance are harmless and allowed. When in
        // doubt, flat: a wrong share is a silent error, a missed one only
        // costs memory.
        //
        // GetSymbolGeometry and not GetInstanceGeometry: here the point is
        // the geometry BEFORE the instance's transform, the one identical
        // across instances. Nested instances inside it are flattened by
        // Collect with GetInstanceGeometry, which for them means "in the
        // coordinate system of the symbol that owns them".
        public static InstancedMesh TessellateInstance(Element element)
        {
            if (element == null) { return null; }

            GeometryElement geometry = element.get_Geometry(CreateOptions());
            if (geometry == null) { return null; }

            GeometryInstance single = null;
            foreach (GeometryObject obj in geometry)
            {
                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                {
                    if (single != null) { return null; }
                    single = instance;
                    continue;
                }
                if (HasTriangles(obj)) { return null; }
            }
            if (single == null) { return null; }

            List<float> positions = new List<float>();
            List<float> normals = new List<float>();
            Collect(single.GetSymbolGeometry(), positions, normals, 1);
            if (positions.Count == 0) { return null; }

            int[] indices = new int[positions.Count / 3];
            for (int i = 0; i < indices.Length; i++) { indices[i] = i; }

            // Origin unused on this path: the matrix places the object.
            TessellatedMesh mesh = new TessellatedMesh(
                positions.ToArray(), normals.ToArray(), indices, new float[3]);

            Transform transform = single.Transform;
            float[] matrix = InstanceGeometry.RowMajorFromTransform(
                ToArray(transform.BasisX), ToArray(transform.BasisY),
                ToArray(transform.BasisZ), ToArray(transform.Origin));

            return new InstancedMesh(mesh, InstanceGeometry.ComputeMeshKey(mesh.Positions), matrix);
        }

        private static bool HasTriangles(GeometryObject obj)
        {
            Solid solid = obj as Solid;
            if (solid != null) { return solid.Faces != null && solid.Faces.Size > 0; }

            Mesh mesh = obj as Mesh;
            if (mesh != null) { return mesh.NumTriangles > 0; }

            return false;
        }

        private static double[] ToArray(XYZ vector)
        {
            return new double[] { vector.X, vector.Y, vector.Z };
        }

        private static void Collect(
            GeometryElement geometry, List<float> positions, List<float> normals, int depth)
        {
            if (geometry == null) { return; }

            foreach (GeometryObject obj in geometry)
            {
                Solid solid = obj as Solid;
                if (solid != null)
                {
                    AddSolid(solid, positions, normals);
                    continue;
                }

                Mesh mesh = obj as Mesh;
                if (mesh != null)
                {
                    AddMesh(mesh, positions, normals);
                    continue;
                }

                GeometryInstance instance = obj as GeometryInstance;
                if (instance != null)
                {
                    if (depth >= MaxInstanceDepth) { continue; }

                    // GetInstanceGeometry with no arguments, not
                    // GetSymbolGeometry: the former returns the geometry "in
                    // the coordinate system of the model that owns this
                    // instance", i.e. already positioned in the world. The
                    // latter returns it in the symbol's local space, which
                    // would then need to be multiplied by hand by
                    // instance.Transform. Flattening here leaves no local
                    // rotation worth sending, which is why revit_geometry
                    // carries a single translation, origin, and not a full
                    // matrix.
                    Collect(instance.GetInstanceGeometry(), positions, normals, depth + 1);
                }

                // Curve, PolyLine, Point and the rest have no surface and
                // produce no triangles: they are silently ignored.
            }
        }

        private static void AddSolid(Solid solid, List<float> positions, List<float> normals)
        {
            FaceArray faces = solid.Faces;
            if (faces == null || faces.Size == 0) { return; }

            foreach (Face face in faces)
            {
                if (face == null) { continue; }
                AddMesh(face.Triangulate(LevelOfDetail), positions, normals);
            }
        }

        private static void AddMesh(Mesh mesh, List<float> positions, List<float> normals)
        {
            if (mesh == null) { return; }

            double scale = BridgeConstants.MetersPerFoot;
            int triangleCount = mesh.NumTriangles;

            for (int i = 0; i < triangleCount; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                if (triangle == null) { continue; }

                XYZ a = triangle.get_Vertex(0);
                XYZ b = triangle.get_Vertex(1);
                XYZ c = triangle.get_Vertex(2);
                if (a == null || b == null || c == null) { continue; }

                double ax = a.X * scale, ay = a.Y * scale, az = a.Z * scale;
                double bx = b.X * scale, by = b.Y * scale, bz = b.Z * scale;
                double cx = c.X * scale, cy = c.Y * scale, cz = c.Z * scale;

                if (!IsFinite(ax, ay, az) || !IsFinite(bx, by, bz) || !IsFinite(cx, cy, cz))
                {
                    continue;
                }

                // Face normal from the triangle itself, never Revit's
                // normals: Mesh.DistributionOfNormals can be AtEachPoint,
                // OnEachFacet or OnePerFace and GetNormal changes meaning
                // accordingly, i.e. three cases to get right to obtain the
                // same result the cross product always gives.
                double ux = bx - ax, uy = by - ay, uz = bz - az;
                double vx = cx - ax, vy = cy - ay, vz = cz - az;

                double nx = uy * vz - uz * vy;
                double ny = uz * vx - ux * vz;
                double nz = ux * vy - uy * vx;

                double length = Math.Sqrt(nx * nx + ny * ny + nz * nz);

                // The degenerate triangle is SKIPPED, not normalized: dividing
                // by zero would produce NaNs that travel all the way to
                // Blender and make the mesh disappear without a single error
                // message.
                //
                // The condition is written negated on purpose. "length <=
                // MinCrossLength" would be false for NaN and would let through
                // exactly the worst case; "!(length > MinCrossLength)" is true
                // for NaN, because every comparison with NaN is false.
                if (!(length > MinCrossLength) || double.IsInfinity(length)) { continue; }

                float fnx = (float)(nx / length);
                float fny = (float)(ny / length);
                float fnz = (float)(nz / length);

                // Three own vertices per triangle: a face normal cannot be
                // shared with adjacent triangles, and this is what produces
                // the faceted shading, honest for tessellated BIM geometry.
                AddVertex(positions, normals, ax, ay, az, fnx, fny, fnz);
                AddVertex(positions, normals, bx, by, bz, fnx, fny, fnz);
                AddVertex(positions, normals, cx, cy, cz, fnx, fny, fnz);
            }
        }

        private static void AddVertex(
            List<float> positions, List<float> normals,
            double x, double y, double z, float nx, float ny, float nz)
        {
            positions.Add((float)x);
            positions.Add((float)y);
            positions.Add((float)z);
            normals.Add(nx);
            normals.Add(ny);
            normals.Add(nz);
        }

        // double.IsFinite does not exist on net48: the .NET Framework BCL does
        // not have it. Written by hand, it works on both TFMs.
        private static bool IsFinite(double x, double y, double z)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x)
                && !double.IsNaN(y) && !double.IsInfinity(y)
                && !double.IsNaN(z) && !double.IsInfinity(z);
        }
    }
}
