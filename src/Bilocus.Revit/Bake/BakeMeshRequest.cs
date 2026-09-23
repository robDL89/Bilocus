// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Geometry;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Bake
{
    // An object of the bake, already read from the wire and checked, waiting
    // for bake_end and then for the transaction on the Revit main thread.
    //
    // Same scheme as ProxyEdgeRequest, for the same reason: the router that
    // reads bake_mesh does not have the Revit API and must not have it, so it
    // cannot create the DirectShape. It goes as far as it can - header read,
    // category in the right shape, finite matrix, payload consistent down to
    // the last index - and packs it up here. BakeBuilder only receives valid
    // requests, and what is left for it to fail concerns Revit for real: the
    // category not allowed for a DirectShape, the geometry rejected by the
    // builder.
    //
    // The payload positions are LOCAL and in meters, as they arrive; Matrix
    // is the object's row-major world matrix. The transform and the
    // conversion to feet are done by BakeBuilder, the only point where Revit
    // units are entered.
    public sealed class BakeMeshRequest
    {
        // Already normalized by ProxyNaming: it is the form used to write the
        // mark and to look up the DirectShape to replace on re-bake.
        public string ObjectId { get; private set; }

        // Label, not identity: it becomes the DirectShape name and the
        // subject of messages. Never empty, falls back to obj_id.
        public string Name { get; private set; }

        // Name of a BuiltInCategory value, in the form checked by
        // BakeCategory. Whether it is allowed for a DirectShape is decided by
        // Revit.
        public string Category { get; private set; }

        // 16 row-major floats, all finite. Copy of the one received.
        public float[] Matrix { get; private set; }

        public BakeMeshPayload Mesh { get; private set; }

        // "Accept open solid". Only counts in the
        // family bake: with a Sheet outcome from the builder, the family is
        // only created if this is true. In the DirectShape bake it is
        // ignored, an open mesh still goes in as a mesh. False if the message
        // does not carry it.
        public bool AcceptOpen { get; private set; }

        private BakeMeshRequest(string objectId, string name, string category, float[] matrix,
            BakeMeshPayload mesh, bool acceptOpen)
        {
            ObjectId = objectId;
            Name = name;
            Category = category;
            Matrix = matrix;
            Mesh = mesh;
            AcceptOpen = acceptOpen;
        }

        // The Phase B Parse: AcceptOpen false.
        public static BakeMeshRequest Parse(string objectId, string name, string category, float[] matrix,
            int vertCount, int faceCount, int loopCount, int triCount, byte[] payload)
        {
            return Parse(objectId, name, category, matrix, vertCount, faceCount, loopCount, triCount,
                payload, false);
        }

        // Reads and checks a bake_mesh.
        //
        // Raises ArgumentException for any inconsistency: these are defects
        // of the received message, and the router re-encodes them as a
        // "bake_mesh: ..." error frame without closing the connection
        // (DESIGN.md 5.1).
        //
        // First the cheap checks (id, category, matrix), then the payload: a
        // wrong category must not require reading megabytes of geometry to
        // be reported.
        public static BakeMeshRequest Parse(string objectId, string name, string category, float[] matrix,
            int vertCount, int faceCount, int loopCount, int triCount, byte[] payload, bool acceptOpen)
        {
            // Without identity there is neither replacement nor removal:
            // better to reject the message than to leave a DirectShape in the
            // document that nobody will be able to find again.
            string normalizedId = ProxyNaming.NormalizeObjectId(objectId);

            string label = name;
            if (label == null || label.Trim().Length == 0) { label = normalizedId; }

            if (!BakeCategory.IsWellFormed(category))
            {
                throw new ArgumentException(string.Format(
                    "category '{0}' invalid: expected the name of a Revit category like {1}",
                    category, BakeCategory.DefaultCategory));
            }

            if (matrix == null)
            {
                throw new ArgumentException("matrix missing: the bake requires the object's world matrix");
            }
            if (matrix.Length != RowMajorMatrix.Length)
            {
                throw new ArgumentException(string.Format(
                    "matrix with {0} numbers instead of {1}", matrix.Length, RowMajorMatrix.Length));
            }

            // A non-finite value in the matrix would move ALL vertices to a
            // point that does not exist, halfway through the transaction
            // group. No NaN arrives from the JSON, but a number beyond the
            // float32 range becomes infinite on cast: that is where it comes
            // from.
            for (int i = 0; i < matrix.Length; i++)
            {
                if (float.IsNaN(matrix[i]) || float.IsInfinity(matrix[i]))
                {
                    throw new ArgumentException("matrix with a non-finite value at position " + i);
                }
            }

            BakeMeshPayload mesh = BakeMeshPayload.Parse(payload, vertCount, faceCount, loopCount, triCount);

            // The request stays pending until it is drained: a copy, so a
            // caller that reuses its array does not move the object.
            return new BakeMeshRequest(normalizedId, label, category, (float[])matrix.Clone(), mesh, acceptOpen);
        }
    }
}
