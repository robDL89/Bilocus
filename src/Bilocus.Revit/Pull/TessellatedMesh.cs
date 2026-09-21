// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Pull
{
    // Result of tessellating a Revit element, in the form the wire expects:
    // positions in METERS and in coordinates LOCAL to the element, one normal
    // per vertex, triangle indices, plus the element's origin in world
    // coordinates.
    //
    // The coordinates are local rather than world for a usability reason, not
    // a format one. With vertices in world coordinates the Blender object is
    // born with an identity matrix, i.e. with its origin at (0,0,0): rotating
    // it makes it rotate around the scene's origin instead of on itself, and
    // a wall thirty meters from the origin, rotated fifteen degrees, ends up
    // very far away. By translating the vertices by -Origin and putting
    // Origin in the header, the Blender side can give the object an origin on
    // the element.
    //
    // Origin is the CENTER OF THE BOUNDING BOX, not the centroid of the
    // vertices: a tessellated wall has far more vertices around a door than
    // over the rest of its length, and a centroid would follow the density of
    // the tessellation instead of the bounding volume. A full matrix is not
    // needed: GeometryInstance are already flattened and there is no local
    // rotation left worth sending, so a translation is enough and gives a
    // useful origin.
    //
    // This file must NOT contain references to the Revit API: it is included
    // as a linked source from the test project, which cannot load it. This is
    // also why it is useful for it to exist as a separate type: it is the
    // point where geometry stops being Revit stuff and becomes floats to
    // pack. For the same reason the origin calculation and the translation
    // live here, where they are pure logic and covered by tests, and not
    // inside ElementTessellator.
    //
    // Triangles are NON-indexed by construction: three own vertices per
    // triangle, so Indices is always 0, 1, 2, 3, ... The reason is in
    // ElementTessellator: normals are face normals, computed from the cross
    // product, and a face normal cannot be shared between adjacent triangles.
    // Indices stays explicit anyway because MeshPayloadWriter wants it and
    // because the wire format does not change between push and pull.
    public sealed class TessellatedMesh
    {
        public float[] Positions { get; private set; }
        public float[] Normals { get; private set; }
        public int[] Indices { get; private set; }

        // Three floats, meters, Revit world coordinates. This is the position
        // where the Blender side puts the object so the geometry ends up
        // where Revit draws it.
        public float[] Origin { get; private set; }

        public TessellatedMesh(float[] positions, float[] normals, int[] indices, float[] origin)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (normals == null) throw new ArgumentNullException("normals");
            if (indices == null) throw new ArgumentNullException("indices");
            if (origin == null) throw new ArgumentNullException("origin");

            if (positions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "positions must have a length that is a multiple of 3, has " + positions.Length);
            }
            if (normals.Length != positions.Length)
            {
                throw new ArgumentException(string.Format(
                    "normals is {0} long but positions is {1} long", normals.Length, positions.Length));
            }
            if (indices.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "indices must have a length that is a multiple of 3, has " + indices.Length);
            }
            if (origin.Length != 3)
            {
                throw new ArgumentException(
                    "origin must have 3 components, has " + origin.Length);
            }

            Positions = positions;
            Normals = normals;
            Indices = indices;
            Origin = origin;
        }

        public int VertexCount { get { return Positions.Length / 3; } }

        public int TriangleCount { get { return Indices.Length / 3; } }

        // True when the element has no 3d geometry to send. Not an error:
        // levels, grids, annotations and views end up here, and it is the
        // case the caller must skip silently.
        public bool IsEmpty { get { return Indices.Length == 0; } }

        // A new instance on every call, so nobody can write into the shared
        // arrays of a hypothetical singleton.
        public static TessellatedMesh Empty
        {
            get
            {
                return new TessellatedMesh(
                    new float[0], new float[0], new int[0], new float[3]);
            }
        }

        // The center of the vertices' bounding box, in meters.
        //
        // ELEMENT WITHOUT VERTICES: it has no bounding box, so origin
        // (0,0,0). This is declared rather than left implicit, even though in
        // practice it never reaches the wire: a mesh with no vertices is
        // empty and SendSelectionCommand skips it before building the header.
        // Zero is the right choice anyway - it is the project's internal
        // origin, the same position the object would have had before this
        // fix.
        public static float[] ComputeOrigin(float[] positions)
        {
            if (positions == null) throw new ArgumentNullException("positions");
            if (positions.Length % 3 != 0)
            {
                throw new ArgumentException(
                    "positions must have a length that is a multiple of 3, has " + positions.Length);
            }

            if (positions.Length == 0) { return new float[3]; }

            float minX = positions[0], maxX = positions[0];
            float minY = positions[1], maxY = positions[1];
            float minZ = positions[2], maxZ = positions[2];

            for (int i = 3; i < positions.Length; i += 3)
            {
                float x = positions[i];
                float y = positions[i + 1];
                float z = positions[i + 2];

                if (x < minX) { minX = x; } else if (x > maxX) { maxX = x; }
                if (y < minY) { minY = y; } else if (y > maxY) { maxY = y; }
                if (z < minZ) { minZ = z; } else if (z > maxZ) { maxZ = z; }
            }

            // The sum is done in double and rounded once: in float, min + max
            // would lose a digit on large project coordinates, the ones with
            // six zeros from survey coordinates.
            return new float[]
            {
                (float)(((double)minX + maxX) * 0.5),
                (float)(((double)minY + maxY) * 0.5),
                (float)(((double)minZ + maxZ) * 0.5)
            };
        }

        // Builds the mesh from positions in WORLD coordinates: computes the
        // origin and translates the vertices by -origin.
        //
        // The passed-in arrays become the mesh's own and positions is
        // translated IN PLACE, with no copy. This is the same contract as the
        // constructor, which makes no defensive copies: the caller is
        // ElementTessellator, which passes arrays just created by ToArray().
        // Copying would keep a large selection's mesh in memory twice for no
        // gain.
        public static TessellatedMesh FromWorldSpace(
            float[] positions, float[] normals, int[] indices)
        {
            float[] origin = ComputeOrigin(positions);

            for (int i = 0; i < positions.Length; i += 3)
            {
                positions[i] -= origin[0];
                positions[i + 1] -= origin[1];
                positions[i + 2] -= origin[2];
            }

            return new TessellatedMesh(positions, normals, indices, origin);
        }
    }
}
