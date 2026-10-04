// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bilocus.Geometry;
using Bilocus.Revit.Net;
using Bilocus.Revit.Proxy;

// Autodesk.Revit.DB has its own Frame type (the reference frame of a camera
// view): the alias avoids ambiguity with Bilocus.Protocol.Frame, which is the
// one needed here, the header+payload container of the wire.
using Frame = Bilocus.Protocol.Frame;

namespace Bilocus.Revit.Pull
{
    // "Send selection" button: reads uidoc.Selection, tessellates every
    // element and sends revit_batch_begin / revit_mesh / revit_instance /
    // revit_geometry / revit_batch_end (DESIGN.md 5.4) to the connected client.
    [Transaction(TransactionMode.Manual)]
    public sealed class SendSelectionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Current == null || App.Current.BridgeServer == null)
            {
                TaskDialog.Show("Bilocus", "Server not initialized.");
                return Result.Cancelled;
            }

            BridgeServer server = App.Current.BridgeServer;

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null)
            {
                TaskDialog.Show("Bilocus", "No active document.");
                return Result.Cancelled;
            }

            ICollection<ElementId> selectedIds = uidoc.Selection.GetElementIds();
            if (selectedIds.Count == 0)
            {
                TaskDialog.Show("Bilocus", "No element selected.");
                return Result.Cancelled;
            }

            // The client must be checked BEFORE tessellating: tessellating a
            // selection of hundreds of elements only to find out that nobody
            // is listening is wasted time, and the downstream error message
            // would be confusing ("send failed" on everything, instead of
            // clear from the start).
            if (!server.IsClientConnected)
            {
                TaskDialog.Show("Bilocus",
                    "No Blender client connected.\nStart Blender and connect before sending.");
                return Result.Cancelled;
            }

            Document doc = uidoc.Document;
            SendSelectionResult result = new SendSelectionResult();
            List<PendingElement> ready = new List<PendingElement>();

            // One mesh per key: the duplicates produced by tessellating every
            // instance are dropped here, so a selection of a hundred windows
            // keeps one window's geometry in memory, not a hundred.
            Dictionary<string, TessellatedMesh> sharedMeshes = new Dictionary<string, TessellatedMesh>();

            long tessellateStart = Stopwatch.GetTimestamp();
            foreach (ElementId id in selectedIds)
            {
                Element element = doc.GetElement(id);

                TessellatedMesh mesh;
                InstancedMesh instanced;
                try
                {
                    // Before tessellation, which is the expensive part: a
                    // bridge bake must not come back to Blender (feedback
                    // loop).
                    if (IsBridgeBake(element))
                    {
                        result.SkippedBridgeCount++;
                        continue;
                    }

                    instanced = ElementTessellator.TessellateInstance(element);
                    mesh = instanced != null ? instanced.Mesh : ElementTessellator.Tessellate(element);
                }
                catch (Exception ex)
                {
                    // An element that blows up must be counted and named, and
                    // must not stop the others from starting: Tessellate
                    // propagates exceptions on purpose (see the comment
                    // there), so the try/catch per element lives here, not
                    // inside Tessellate. This also applies to reading the
                    // mark, which throws on a schema GUID collision: without
                    // knowing whether it is a bake, the element does not
                    // start and the reason is readable.
                    result.FailedCount++;
                    result.LastFailureLabel = DescribeForError(element, id);
                    result.LastFailureError = string.Format(
                        "{0}: {1}", ex.GetType().Name, ex.Message);
                    continue;
                }

                if (mesh.IsEmpty)
                {
                    // Normal case: levels, grids, views, annotations.
                    result.SkippedEmptyCount++;
                    continue;
                }

                string category = element != null && element.Category != null
                    ? element.Category.Name
                    : null;
                string typeName = ReadTypeName(doc, element);
                long elementIdValue = id.Value;
                string name = ElementNaming.BuildName(category, typeName, elementIdValue);

                if (instanced != null)
                {
                    if (!sharedMeshes.ContainsKey(instanced.MeshKey))
                    {
                        sharedMeshes.Add(instanced.MeshKey, instanced.Mesh);
                    }
                    ready.Add(PendingElement.Instance(elementIdValue, name, category, typeName, instanced));
                }
                else
                {
                    ready.Add(PendingElement.Flat(elementIdValue, name, category, typeName, mesh));
                }
            }
            result.TessellateMs = ElapsedMs(tessellateStart);

            if (ready.Count == 0)
            {
                // Nothing to send: no batch is opened, for the same reason an
                // empty selection does not open one. SendMs stays 0, which is
                // honest: no time was spent on the network.
                TaskDialog.Show("Bilocus", result.BuildSummaryText());
                return Result.Succeeded;
            }

            long sendStart = Stopwatch.GetTimestamp();
            SendBatch(server, ready, sharedMeshes, result);
            result.SendMs = ElapsedMs(sendStart);

            TaskDialog.Show("Bilocus", result.BuildSummaryText());
            return Result.Succeeded;
        }

        private static void SendBatch(
            BridgeServer server, List<PendingElement> ready,
            Dictionary<string, TessellatedMesh> sharedMeshes, SendSelectionResult result)
        {
            string beginHeader = MessageRouter.BuildBatchBegin(ready.Count);
            if (!server.Send(new Frame(beginHeader, null)))
            {
                result.Aborted = true;
                result.AbortReason = "sending revit_batch_begin failed, the batch did not open";
                return;
            }

            HashSet<string> sentKeys = new HashSet<string>();
            bool midBatchFailure = false;
            foreach (PendingElement pending in ready)
            {
                bool sent;
                if (pending.MeshKey == null)
                {
                    // Mesh.Origin is the bounding box center of the element in
                    // world coordinates: the payload's vertices are already
                    // local, and this is the field that puts them back in place
                    // in Blender.
                    string geometryHeader = MessageRouter.BuildGeometryHeader(
                        pending.ElementIdValue, pending.Name, pending.Category, pending.TypeName,
                        pending.Mesh.VertexCount, pending.Mesh.TriangleCount,
                        pending.Mesh.Origin, ElementNaming.DefaultColor());
                    byte[] payload = MeshPayloadWriter.Write(
                        pending.Mesh.Positions, pending.Mesh.Normals, pending.Mesh.Indices);
                    sent = server.Send(new Frame(geometryHeader, payload));
                }
                else
                {
                    // The shared mesh goes out once per batch, right before
                    // its first instance. Revit does not know what Blender
                    // already has, and does not guess: Blender decides.
                    sent = true;
                    if (!sentKeys.Contains(pending.MeshKey))
                    {
                        TessellatedMesh shared = sharedMeshes[pending.MeshKey];
                        string meshHeader = MessageRouter.BuildMeshHeader(
                            pending.MeshKey, shared.VertexCount, shared.TriangleCount);
                        byte[] meshPayload = MeshPayloadWriter.Write(
                            shared.Positions, shared.Normals, shared.Indices);
                        sent = server.Send(new Frame(meshHeader, meshPayload));
                        if (sent)
                        {
                            sentKeys.Add(pending.MeshKey);
                            result.SharedMeshCount++;
                        }
                    }
                    if (sent)
                    {
                        string instanceHeader = MessageRouter.BuildInstanceHeader(
                            pending.ElementIdValue, pending.Name, pending.Category, pending.TypeName,
                            pending.MeshKey, pending.Matrix, ElementNaming.DefaultColor());
                        sent = server.Send(new Frame(instanceHeader, null));
                    }
                }

                if (!sent)
                {
                    // If sending fails halfway through the batch, there is no
                    // insisting: neither a retry on this element nor an
                    // attempt on the rest. BridgeServer.Send already
                    // distinguishes a rejected frame (recoverable, connection
                    // intact) from a write interrupted midway (terminal, the
                    // connection is already closed by BridgeServer itself): in
                    // neither case is continuing to send megabytes of mesh the
                    // right answer.
                    midBatchFailure = true;
                    result.Aborted = true;
                    result.AbortReason = string.Format(
                        "send interrupted after {0} elements, failed on \"{1}\"",
                        result.SentCount, pending.Name);
                    break;
                }

                result.SentCount++;
                if (pending.MeshKey != null) { result.InstanceCount++; }
                result.TotalTriangles += pending.TriangleCount;
            }

            // A single closing attempt, always, because the batch was opened
            // successfully: if the connection is still alive it closes
            // properly and Blender is not left with a batch open without a
            // revit_batch_end; if it has already dropped it is a harmless
            // no-op, because BridgeServer.Send sees the null stream and
            // returns false without writing anything. This is not
            // "insisting": nothing is retried, it is a single final attempt
            // at closing.
            bool endOk = server.Send(new Frame(MessageRouter.BuildBatchEnd(), null));
            if (!endOk)
            {
                if (!midBatchFailure)
                {
                    result.Aborted = true;
                    result.AbortReason = "sending revit_batch_end failed";
                }
                else
                {
                    result.AbortReason = result.AbortReason
                        + "; revit_batch_end also failed to go out, the batch stays open on the Blender side";
                }
            }
        }

        // A DirectShape with the bridge's mark: geometry that comes from
        // Blender, written by the bake.
        //
        // Both conditions, not just the mark: the same schema also sits on
        // proxy curves, which have no geometry to tessellate and already end
        // up among the "no geometry" ones, and a DirectShape without a mark
        // belongs to the user (an imported IFC, another add-in) and must
        // start like any other element. The class check comes first because
        // it costs nothing, and it saves reading Extensible Storage on the
        // rest of the selection.
        //
        // Phase B2: also an instance of a bridge family. What counts is the
        // FAMILY's mark, not the instance's: an instance dragged from the
        // project browser is not marked, but its geometry comes from Blender
        // just as much as the instance placed by the bake, and sending it
        // back would close the same loop.
        private static bool IsBridgeBake(Element element)
        {
            DirectShape shape = element as DirectShape;
            if (shape != null)
            {
                return ProxySchema.ReadObjectId(shape) != null;
            }

            FamilyInstance instance = element as FamilyInstance;
            if (instance == null) { return false; }

            FamilySymbol symbol = instance.Symbol;
            if (symbol == null || symbol.Family == null) { return false; }

            return ProxySchema.ReadObjectId(symbol.Family) != null;
        }

        private static string ReadTypeName(Document doc, Element element)
        {
            if (element == null) { return null; }

            ElementId typeId = element.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) { return null; }

            Element typeElement = doc.GetElement(typeId);
            return typeElement != null ? typeElement.Name : null;
        }

        // Used only for the error message of a failed element: it must never
        // throw in turn, or an element already in trouble would take down its
        // own report too.
        private static string DescribeForError(Element element, ElementId id)
        {
            if (element == null) { return "id " + id.Value; }
            try
            {
                string name = element.Name;
                return string.IsNullOrEmpty(name)
                    ? "id " + id.Value
                    : name + " [" + id.Value + "]";
            }
            catch (Exception)
            {
                return "id " + id.Value;
            }
        }

        private static double ElapsedMs(long startTimestamp)
        {
            return (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
        }

        // An element already tessellated successfully, waiting to be sent.
        // Flat elements carry their own mesh; instances carry only the key
        // of a mesh held once in the batch's dictionary, and their matrix.
        private sealed class PendingElement
        {
            public readonly long ElementIdValue;
            public readonly string Name;
            public readonly string Category;
            public readonly string TypeName;
            public readonly TessellatedMesh Mesh;
            public readonly string MeshKey;
            public readonly float[] Matrix;
            public readonly int TriangleCount;

            private PendingElement(
                long elementIdValue, string name, string category, string typeName,
                TessellatedMesh mesh, string meshKey, float[] matrix, int triangleCount)
            {
                ElementIdValue = elementIdValue;
                Name = name;
                Category = category;
                TypeName = typeName;
                Mesh = mesh;
                MeshKey = meshKey;
                Matrix = matrix;
                TriangleCount = triangleCount;
            }

            public static PendingElement Flat(
                long elementIdValue, string name, string category, string typeName, TessellatedMesh mesh)
            {
                return new PendingElement(
                    elementIdValue, name, category, typeName, mesh, null, null, mesh.TriangleCount);
            }

            public static PendingElement Instance(
                long elementIdValue, string name, string category, string typeName, InstancedMesh instanced)
            {
                return new PendingElement(
                    elementIdValue, name, category, typeName, null,
                    instanced.MeshKey, instanced.Matrix, instanced.Mesh.TriangleCount);
            }
        }
    }
}
