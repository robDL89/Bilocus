// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using Bilocus.Geometry;

namespace Bilocus.Revit.Preview
{
    // Single source of truth for the preview.
    //
    // NOT thread-safe, and that is fine: it is only touched from Revit's main
    // thread, inside MessageHandler.Execute and inside
    // PreviewServer.RenderScene. The socket thread never sees it. Adding
    // locks here would give a false sense of safety on an object that must
    // not be shared in the first place.
    //
    // This file must NOT contain any reference to the Revit API: it is
    // included as a linked source by the test project, which cannot load it.
    public sealed class GeometryStore
    {
        private readonly Dictionary<string, StoredObject> _objects =
            new Dictionary<string, StoredObject>();

        private HashSet<string> _announced;
        private long _revision;

        // Cache of the world bounding box, valid as long as the revision does
        // not change. GetWorldBounds gets called every frame by
        // PreviewServer: scanning the vertices must not repeat if nothing
        // has changed.
        private float[] _worldBounds;
        private long _worldBoundsRevision = -1;

        public long Revision { get { return _revision; } }

        public List<StoredObject> Objects
        {
            get { return new List<StoredObject>(_objects.Values); }
        }

        public int TotalTriangles
        {
            get
            {
                int total = 0;
                foreach (StoredObject item in _objects.Values) { total += item.TriangleCount; }
                return total;
            }
        }

        // Bounding box of the WHOLE preview, in world coordinates and in
        // METERS, as { minX, minY, minZ, maxX, maxY, maxZ }.
        //
        // Null when there is not a single vertex to draw: in that case the
        // server contributes no geometry, and declaring a degenerate box at
        // the origin would be a lie that would mess up Zoom to Fit and the
        // scene's extent.
        //
        // The caller receives a copy: the cache must not be modifiable from
        // outside.
        public float[] GetWorldBounds()
        {
            if (_worldBoundsRevision != _revision)
            {
                _worldBounds = ComputeWorldBounds();
                _worldBoundsRevision = _revision;
            }

            if (_worldBounds == null) { return null; }
            return (float[])_worldBounds.Clone();
        }

        private float[] ComputeWorldBounds()
        {
            float[] total = null;

            foreach (StoredObject item in _objects.Values)
            {
                float[] box = item.GetWorldBounds();
                if (box == null) { continue; }

                if (total == null) { total = box; continue; }

                for (int i = 0; i < 3; i++)
                {
                    if (box[i] < total[i]) { total[i] = box[i]; }
                    if (box[i + 3] > total[i + 3]) { total[i + 3] = box[i + 3]; }
                }
            }

            return total;
        }

        public void Upsert(string objectId, string name, List<MeshChunk> chunks, float[] matrix, float[] color)
        {
            StoredObject item;
            if (!_objects.TryGetValue(objectId, out item))
            {
                item = new StoredObject(objectId);
                _objects.Add(objectId, item);
            }

            item.Name = name;
            item.Chunks = chunks;
            if (matrix != null) { item.Matrix = matrix; }
            if (color != null) { item.Color = color; }

            _revision++;
            item.GeometryRevision = _revision;
            item.TransformRevision = _revision;
        }

        // The live path: does not touch the chunks, so PreviewServer does
        // not rebuild any GPU buffer.
        public void SetTransform(string objectId, float[] matrix)
        {
            StoredObject item;
            if (!_objects.TryGetValue(objectId, out item)) { return; }

            item.Matrix = matrix;
            _revision++;
            item.TransformRevision = _revision;
        }

        public void Remove(string objectId)
        {
            if (_objects.Remove(objectId)) { _revision++; }
        }

        public void Clear()
        {
            if (_objects.Count == 0) { return; }
            _objects.Clear();
            _revision++;
        }

        // sync_begin lists the ids that are about to arrive. sync_end removes
        // the ones that were not announced: this way an object removed from
        // the collection disappears from Revit, and removal happens in one
        // shot at the end instead of at the start of sync, avoiding flicker.
        public void BeginSync(List<string> announcedIds)
        {
            _announced = new HashSet<string>(announcedIds);
        }

        public void EndSync()
        {
            if (_announced == null) { return; }

            List<string> stale = new List<string>();
            foreach (string id in _objects.Keys)
            {
                if (!_announced.Contains(id)) { stale.Add(id); }
            }
            foreach (string id in stale) { _objects.Remove(id); }

            _announced = null;
            if (stale.Count > 0) { _revision++; }
        }
    }
}
