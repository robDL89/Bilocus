// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using Bilocus.Geometry;

namespace Bilocus.Revit.Preview
{
    // An entry of the preview. Lives only in RAM: none of this exists in the
    // Revit document.
    //
    // This file must NOT contain any reference to the Revit API: it is
    // included as a linked source by the test project, which cannot load it.
    // That is why the bounding box is a float[] and not an Outline or XYZ.
    public sealed class StoredObject
    {
        public string ObjectId { get; private set; }
        public string Name { get; set; }

        private List<MeshChunk> _chunks;

        // LOCAL bounding box, in meters: depends only on the geometry, so it
        // is computed once when the chunks are assigned. Null when the
        // object does not even have one vertex.
        private float[] _localMin;
        private float[] _localMax;

        public List<MeshChunk> Chunks
        {
            get { return _chunks; }
            set
            {
                _chunks = value;
                RecomputeLocalBounds();
            }
        }

        public bool HasLocalBounds { get { return _localMin != null; } }

        // Row-major 4x4 matrix, in METERS as on the wire. Conversion to feet
        // happens only when Revit's Transform is built.
        public float[] Matrix { get; set; }

        // RGBA 0..1
        public float[] Color { get; set; }

        // Changes on every Upsert: PreviewServer uses it to know whether the
        // GPU buffers it has cached are still valid.
        public long GeometryRevision { get; set; }

        // Changes on every SetTransform. Kept separate on purpose: a
        // transform must never invalidate the GPU buffers.
        public long TransformRevision { get; set; }

        public StoredObject(string objectId)
        {
            ObjectId = objectId;
            Matrix = new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
            Color = new float[] { 0.8f, 0.8f, 0.8f, 1f };
            Chunks = new List<MeshChunk>();
        }

        public int TriangleCount
        {
            get
            {
                int total = 0;
                foreach (MeshChunk chunk in _chunks) { total += chunk.TriangleCount; }
                return total;
            }
        }

        // Bounding box in WORLD coordinates and in meters, as
        // { minX, minY, minZ, maxX, maxY, maxZ }. Null if the object has no
        // vertices.
        //
        // Transforming just the two corners of the local box is not enough:
        // with a rotation their images are no longer the extremes of the
        // world box (a symmetric box rotated 45 degrees would give a flat
        // box). All eight vertices must be transformed.
        public float[] GetWorldBounds()
        {
            if (_localMin == null) { return null; }

            float[] m = Matrix;
            float[] bounds = null;

            for (int corner = 0; corner < 8; corner++)
            {
                float x = (corner & 1) == 0 ? _localMin[0] : _localMax[0];
                float y = (corner & 2) == 0 ? _localMin[1] : _localMax[1];
                float z = (corner & 4) == 0 ? _localMin[2] : _localMax[2];

                // row-major: row 0 produces world x
                float wx = m[0] * x + m[1] * y + m[2] * z + m[3];
                float wy = m[4] * x + m[5] * y + m[6] * z + m[7];
                float wz = m[8] * x + m[9] * y + m[10] * z + m[11];

                if (bounds == null)
                {
                    bounds = new float[] { wx, wy, wz, wx, wy, wz };
                    continue;
                }

                if (wx < bounds[0]) { bounds[0] = wx; }
                if (wy < bounds[1]) { bounds[1] = wy; }
                if (wz < bounds[2]) { bounds[2] = wz; }
                if (wx > bounds[3]) { bounds[3] = wx; }
                if (wy > bounds[4]) { bounds[4] = wy; }
                if (wz > bounds[5]) { bounds[5] = wz; }
            }

            return bounds;
        }

        private void RecomputeLocalBounds()
        {
            _localMin = null;
            _localMax = null;

            if (_chunks == null) { return; }

            foreach (MeshChunk chunk in _chunks)
            {
                float[] positions = chunk.Positions;
                if (positions == null) { continue; }

                for (int i = 0; i + 2 < positions.Length; i += 3)
                {
                    float x = positions[i];
                    float y = positions[i + 1];
                    float z = positions[i + 2];

                    if (_localMin == null)
                    {
                        _localMin = new float[] { x, y, z };
                        _localMax = new float[] { x, y, z };
                        continue;
                    }

                    if (x < _localMin[0]) { _localMin[0] = x; }
                    if (y < _localMin[1]) { _localMin[1] = y; }
                    if (z < _localMin[2]) { _localMin[2] = z; }
                    if (x > _localMax[0]) { _localMax[0] = x; }
                    if (y > _localMax[1]) { _localMax[1] = y; }
                    if (z > _localMax[2]) { _localMax[2] = z; }
                }
            }
        }
    }
}
