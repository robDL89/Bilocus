// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Geometry
{
    // Reads and validates the payload of the bake_mesh message (DESIGN.md
    // 5.3). Little-endian, no padding, in this order:
    //
    //   positions     vert_count * 3   float32   x y z LOCAL, meters
    //   face_sizes    face_count       uint32    vertices of each polygon
    //   face_vertices loop_count       uint32    indices, polygon after polygon
    //   tri_vertices  tri_count * 3    uint32    Blender's own triangles
    //   tri_faces     tri_count        uint32    the polygon of each triangle
    //
    // Deliberately does not reuse MeshPayload. The preview only ever travels
    // as triangles; the bake carries the POLYGONS, to keep planar quads and
    // n-gons whole, together with the triangles Blender itself splits them
    // into, for the fallback of the single non-planar polygon. They are two
    // different formats: merging them would make half of each one optional.
    //
    // Unlike MeshPayload, the validation here is COMPLETE, indices and the
    // consistency between polygons and triangles included. This payload ends
    // up inside a transaction that writes to the document: a defect found
    // here is a frame error with a precise message (DESIGN.md 5.1), the same
    // defect found halfway through TessellatedShapeBuilder is a failed
    // object, or worse a wrong shape written into a project file.
    //
    // The Blender side rejects the same cases before producing bytes: the
    // two lists of checks must be kept identical.
    public sealed class BakeMeshPayload
    {
        // Cap on vert_count, face_count and tri_count.
        //
        // Same reason as ProxyEdgeRequest.MaxEdges: the protocol's
        // MaxPayloadBytes would let through objects that would keep Revit
        // stuck for minutes inside the bake transaction, with no way to
        // interrupt it. The Blender side has its own thresholds on vertices;
        // this is the last line of defense, on the side that risks the most.
        //
        // loop_count has no cap of its own and does not need one: the
        // header's consistency check ties it to tri_count and face_count.
        public const int MaxBakeFaces = 2000000;

        public float[] Positions { get; private set; }
        public int[] FaceSizes { get; private set; }
        public int[] FaceVertices { get; private set; }
        public int[] TriVertices { get; private set; }
        public int[] TriFaces { get; private set; }

        public int VertexCount { get { return Positions.Length / 3; } }
        public int FaceCount { get { return FaceSizes.Length; } }
        public int LoopCount { get { return FaceVertices.Length; } }
        public int TriangleCount { get { return TriFaces.Length; } }

        // Private: the only way to get a BakeMeshPayload is through Parse.
        // Whoever receives it (BakeFaceSet, the builder) can therefore trust
        // its invariants without re-checking them: indices in range, correct
        // sums, exactly size-2 triangles per polygon.
        private BakeMeshPayload(float[] positions, int[] faceSizes, int[] faceVertices,
            int[] triVertices, int[] triFaces)
        {
            Positions = positions;
            FaceSizes = faceSizes;
            FaceVertices = faceVertices;
            TriVertices = triVertices;
            TriFaces = triFaces;
        }

        // Raises ArgumentException for every rule violated, with a message
        // saying which: the router prefixes it with "bake_mesh: " and sends
        // it back as is.
        public static BakeMeshPayload Parse(byte[] payload, int vertCount, int faceCount, int loopCount, int triCount)
        {
            CheckMinimum("vert_count", vertCount, 3);
            CheckMinimum("face_count", faceCount, 1);

            // The contract gives loop_count no explicit minimum, but one
            // follows from it: at least one polygon, at least three
            // vertices. Checking it here does not change what is accepted,
            // and it keeps a negative loop_count from reaching the
            // allocation, where it would raise an OverflowException instead
            // of a content error.
            CheckMinimum("loop_count", loopCount, 3);
            CheckMinimum("tri_count", triCount, 1);

            // The cap BEFORE the length: rejecting two million and one faces
            // should not require having received them.
            CheckMaximum("vert_count", vertCount);
            CheckMaximum("face_count", faceCount);
            CheckMaximum("tri_count", triCount);

            // Every polygon has exactly size-2 triangles: summed over all of
            // them, tri_count = loop_count - 2 * face_count. It is not a new
            // rule, it is the consequence of two contract rules, and the
            // Blender side checks it the same way. Doing it here rejects an
            // inconsistent header before reading a single byte, and gives
            // loop_count the cap it lacks: at most tri_count + 2 * face_count.
            long expectedTris = (long)loopCount - 2L * faceCount;
            if (expectedTris != triCount)
            {
                throw new ArgumentException(string.Format(
                    "tri_count {0} inconsistent with loop_count {1} and face_count {2}: every polygon "
                    + "has size-2 triangles, so tri_count must equal loop_count - 2 * face_count = {3}",
                    triCount, loopCount, faceCount, expectedTris));
            }

            if (payload == null)
            {
                throw new ArgumentException("bake_mesh with no payload");
            }

            long expected = (long)vertCount * 12 + (long)faceCount * 4
                + (long)loopCount * 4 + (long)triCount * 16;
            if (payload.Length != expected)
            {
                throw new ArgumentException(string.Format(
                    "payload is {0} bytes but the header declares {1} (vert {2}, face {3}, loop {4}, tri {5})",
                    payload.Length, expected, vertCount, faceCount, loopCount, triCount));
            }

            int offset = 0;

            float[] positions = new float[vertCount * 3];
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] = ReadFloat(payload, offset);
                offset += 4;

                // A NaN or an infinity would become an XYZ that breaks the
                // face halfway through the builder, after the previous
                // objects in the batch have already been written. Same rule
                // as ProxyEdgeRequest.
                if (float.IsNaN(positions[i]) || float.IsInfinity(positions[i]))
                {
                    throw new ArgumentException(string.Format(
                        "non-finite coordinate in vertex {0}, component {1}", i / 3, i % 3));
                }
            }

            int[] faceSizes = new int[faceCount];
            long loopSum = 0;
            for (int i = 0; i < faceSizes.Length; i++)
            {
                uint size = ReadUInt(payload, offset);
                offset += 4;

                if (size < 3)
                {
                    throw new ArgumentException(string.Format(
                        "polygon {0} has {1} vertices: at least 3 are required", i, size));
                }

                // The sum is checked as it grows, in long: a huge uint32
                // must neither overflow nor become a negative size on cast.
                // Once loop_count is exceeded, the rest can never add up
                // again.
                loopSum += size;
                if (loopSum > loopCount)
                {
                    throw new ArgumentException(string.Format(
                        "face_sizes already sum to {0} at polygon {1}, beyond loop_count {2}",
                        loopSum, i, loopCount));
                }
                faceSizes[i] = (int)size;
            }
            if (loopSum != loopCount)
            {
                throw new ArgumentException(string.Format(
                    "face_sizes sum to {0} but loop_count is {1}", loopSum, loopCount));
            }

            int[] faceVertices = ReadIndices(payload, ref offset, loopCount, vertCount, "face_vertices");
            int[] triVertices = ReadIndices(payload, ref offset, triCount * 3, vertCount, "tri_vertices");
            int[] triFaces = ReadIndices(payload, ref offset, triCount, faceCount, "tri_faces");

            // For every polygon, the triangles that reference it must be
            // exactly size-2. Blender guarantees it: if it does not add up,
            // the two arrays arrive misaligned and the whole object is
            // garbage. The check on the total alone (the header's) is not
            // enough: three triangles split 1+2 instead of 2+1 would pass it.
            int[] trisPerFace = new int[faceCount];
            for (int i = 0; i < triFaces.Length; i++)
            {
                trisPerFace[triFaces[i]]++;
            }
            for (int i = 0; i < faceCount; i++)
            {
                if (trisPerFace[i] != faceSizes[i] - 2)
                {
                    throw new ArgumentException(string.Format(
                        "polygon {0} with {1} vertices is referenced by {2} triangles instead of {3}: "
                        + "face_sizes and tri_faces are misaligned",
                        i, faceSizes[i], trisPerFace[i], faceSizes[i] - 2));
                }
            }

            return new BakeMeshPayload(positions, faceSizes, faceVertices, triVertices, triFaces);
        }

        private static void CheckMinimum(string field, int value, int minimum)
        {
            if (value < minimum)
            {
                throw new ArgumentException(string.Format(
                    "{0} {1} is below the minimum of {2}", field, value, minimum));
            }
        }

        private static void CheckMaximum(string field, int value)
        {
            if (value > MaxBakeFaces)
            {
                throw new ArgumentException(string.Format(
                    "{0} {1} is above the maximum of {2}", field, value, MaxBakeFaces));
            }
        }

        // Reads count uint32 values and checks them all against limit
        // (exclusive). The comparison is between uints: a value beyond
        // int.MaxValue is out of range, not a negative index from a cast
        // wraparound.
        private static int[] ReadIndices(byte[] buffer, ref int offset, int count, int limit, string field)
        {
            int[] values = new int[count];
            for (int i = 0; i < count; i++)
            {
                uint value = ReadUInt(buffer, offset);
                offset += 4;

                if (value >= (uint)limit)
                {
                    throw new ArgumentException(string.Format(
                        "{0}[{1}] = {2} is out of range 0..{3}", field, i, value, limit - 1));
                }
                values[i] = (int)value;
            }
            return values;
        }

        private static uint ReadUInt(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        // Without MeshPayload's per-value temporary array: a bake payload
        // reaches tens of millions of reads, and an allocation per read
        // would be felt. The byte-swapped copy stays in place for
        // big-endian machines, which do not exist for Revit but do for the
        // contract.
        private static float ReadFloat(byte[] buffer, int offset)
        {
            if (BitConverter.IsLittleEndian)
            {
                return BitConverter.ToSingle(buffer, offset);
            }
            byte[] raw = new byte[] { buffer[offset + 3], buffer[offset + 2], buffer[offset + 1], buffer[offset] };
            return BitConverter.ToSingle(raw, 0);
        }
    }
}
