// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Autodesk.Revit.DB.ExternalService;
using Bilocus.Geometry;
using Bilocus.Protocol;

namespace Bilocus.Revit.Preview
{
    public sealed class PreviewServer : IDirectContext3DServer
    {
        private readonly Guid _serverId = new Guid("b41d6f28-7c93-4a05-9e1b-2f8d06c37a54");
        private readonly GeometryStore _store;

        // GPU buffers per object, with the geometry revision they were built
        // with: if it changes, they get rebuilt, otherwise they are reused
        private readonly Dictionary<string, List<GpuChunk>> _gpu =
            new Dictionary<string, List<GpuChunk>>();
        private readonly Dictionary<string, long> _builtFor = new Dictionary<string, long>();

        // Style revision the cached buffers were built with (0 = defaults).
        private long _builtStyleRevision;

        // Store revision of the last prune: buffers of objects that left the
        // store (remove, clear, sync_end) are dropped once per change, not
        // on every frame.
        private long _prunedRevision = -1;

        public string LastError = "";

        public PreviewServer(GeometryStore store) { _store = store; }

        public Guid GetServerId() { return _serverId; }
        public ExternalServiceId GetServiceId() { return ExternalServices.BuiltInExternalServices.DirectContext3DService; }
        public string GetName() { return "Bilocus Preview"; }
        public string GetVendorId() { return "RDLF"; }
        public string GetDescription() { return "Preview of the Blender geometry"; }
        public string GetApplicationId() { return ""; }
        public string GetSourceId() { return ""; }
        public bool UsesHandles() { return false; }
        public bool UseInTransparentPass(View view) { return false; }
        public bool CanExecute(View view) { return view is View3D; }

        // It is not just Zoom to Fit. RevitAPI.xml, on this method, warns:
        // "there may be unintended side-effects if the box is inconsistent
        // with the submitted geometry", and on CanExecute it describes the
        // server's execution as "called upon to contribute a bounding box and
        // graphics content". Returning null meant the server contributed
        // nothing, so Revit computed the scene's extent ignoring the preview
        // and clipped it on the clipping planes while orbiting.
        //
        // The store works in meters: here it is converted to feet, the
        // internal unit.
        public Outline GetBoundingBox(View view)
        {
            float[] bounds = _store.GetWorldBounds();
            if (bounds == null) { return null; }

            double scale = BridgeConstants.FeetPerMeter;
            return new Outline(
                new XYZ(bounds[0] * scale, bounds[1] * scale, bounds[2] * scale),
                new XYZ(bounds[3] * scale, bounds[4] * scale, bounds[5] * scale));
        }

        public void RenderScene(View view, DisplayStyle displayStyle)
        {
            try
            {
                // The colors live inside the vertex buffers: a new style
                // means rebuilding them all, once.
                if (_store.StyleRevision != _builtStyleRevision)
                {
                    DiscardCache();
                    _builtStyleRevision = _store.StyleRevision;
                }

                List<StoredObject> objects = _store.Objects;

                if (_store.Revision != _prunedRevision)
                {
                    PruneCache(objects);
                    _prunedRevision = _store.Revision;
                }

                foreach (StoredObject item in objects)
                {
                    List<GpuChunk> chunks = GetOrBuild(item);

                    DrawContext.SetWorldTransform(ToRevitTransform(item.Matrix));

                    foreach (GpuChunk chunk in chunks)
                    {
                        if (DrawContext.IsInterrupted()) { return; }
                        chunk.Draw(displayStyle);
                    }
                }
                // Precaution: the DrawContext is shared across the registered
                // DC3D servers. Leaving the last applied transform standing
                // would mean, if Revit did not reset it on its own, drawing
                // the next server's geometry transformed. I have no evidence
                // that this happens, but ruling it out costs one line and the
                // symptom would be incomprehensible.
                DrawContext.SetWorldTransform(Transform.Identity);
            }
            catch (Exception ex)
            {
                LastError = string.Format("{0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        private List<GpuChunk> GetOrBuild(StoredObject item)
        {
            List<GpuChunk> chunks;
            long builtFor;

            bool cached = _gpu.TryGetValue(item.ObjectId, out chunks)
                && _builtFor.TryGetValue(item.ObjectId, out builtFor)
                && builtFor == item.GeometryRevision;

            if (cached)
            {
                bool allValid = true;
                foreach (GpuChunk chunk in chunks)
                {
                    if (!chunk.IsValid()) { allValid = false; break; }
                }
                if (allValid) { return chunks; }
            }

            ColorWithTransparency faceColor = ToRevitColor(_store.FaceColor);
            ColorWithTransparency edgeColor = ToRevitColor(_store.EdgeColor);

            chunks = new List<GpuChunk>();
            foreach (MeshChunk source in item.Chunks)
            {
                GpuChunk gpu = new GpuChunk();
                gpu.Build(source, faceColor, edgeColor);
                chunks.Add(gpu);
            }

            _gpu[item.ObjectId] = chunks;
            _builtFor[item.ObjectId] = item.GeometryRevision;
            return chunks;
        }

        // The matrix arrives row-major and in METERS. Only the translation
        // needs converting to feet: the basis vectors carry rotation and
        // scale, which are dimensionless.
        private static Transform ToRevitTransform(float[] m)
        {
            Transform transform = Transform.Identity;
            transform.BasisX = new XYZ(m[0], m[4], m[8]);
            transform.BasisY = new XYZ(m[1], m[5], m[9]);
            transform.BasisZ = new XYZ(m[2], m[6], m[10]);

            double scale = BridgeConstants.FeetPerMeter;
            transform.Origin = new XYZ(m[3] * scale, m[7] * scale, m[11] * scale);
            return transform;
        }

        // RGB in 0..1 to an opaque Revit color. The preview is never drawn
        // in the transparent pass (UseInTransparentPass is false).
        private static ColorWithTransparency ToRevitColor(float[] rgb)
        {
            uint r = (uint)Math.Max(0, Math.Min(255, (int)Math.Round(rgb[0] * 255)));
            uint g = (uint)Math.Max(0, Math.Min(255, (int)Math.Round(rgb[1] * 255)));
            uint b = (uint)Math.Max(0, Math.Min(255, (int)Math.Round(rgb[2] * 255)));
            return new ColorWithTransparency(r, g, b, 0);
        }

        public void DiscardCache()
        {
            _gpu.Clear();
            _builtFor.Clear();
        }

        // Without this, the buffers of every object ever removed from the
        // preview stay referenced for the whole Revit session.
        private void PruneCache(List<StoredObject> objects)
        {
            HashSet<string> alive = new HashSet<string>();
            foreach (StoredObject item in objects) { alive.Add(item.ObjectId); }

            List<string> stale = new List<string>();
            foreach (string id in _gpu.Keys)
            {
                if (!alive.Contains(id)) { stale.Add(id); }
            }
            foreach (string id in stale)
            {
                _gpu.Remove(id);
                _builtFor.Remove(id);
            }
        }
    }
}
