// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Bilocus.Geometry;
using Bilocus.Protocol;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Bake
{
    // The Phase B bake: the objects of a BakeBatch become DirectShapes in the
    // Revit document.
    //
    // ONE SINGLE Ctrl+Z FOR THE BAKE, BUT ONE OBJECT AT A TIME. A bake is an
    // assimilated TransactionGroup: a single entry appears in the undo
    // history, "Bilocus: bake DirectShape", and one Ctrl+Z removes all the
    // objects together. Inside the group, though, each object has ITS OWN
    // transaction. It is the only structure that holds together the
    // two requirements: with a single transaction, an object with the wrong
    // category or a copy made in Revit would take down, via rollback, the
    // twenty good objects already written before it.
    //
    // What arrives here has already been checked by the router
    // (BakeMeshRequest, BakeBatch): indices, counts, finite matrix, category
    // shape. What is left to fail concerns Revit for real - category not
    // allowed for a DirectShape, geometry rejected by the builder, more than
    // one element with the same mark - and fails only that ONE object,
    // counted and named in BakeResult.
    //
    // No TaskDialog: it runs on an ExternalEvent triggered by the network,
    // and the outcome goes back to Blender with bake_result (see
    // MessageHandler).
    public static class BakeBuilder
    {
        // Must read identical in Revit's undo history, next to
        // ProxyBuilder.TransactionName: the group name is what the user sees
        // next to Ctrl+Z after assimilation.
        public const string GroupName = "Bilocus: bake DirectShape";
        public const string ObjectTransactionPrefix = "Bilocus: bake ";

        // DirectShape.ApplicationId of the created elements. The real mark,
        // needed for replacement and removal, is the Extensible Storage:
        // ApplicationId and ApplicationDataId are the signature seen by
        // other tools (IFC exporters, other add-ins) without knowing our
        // schema.
        public const string ApplicationIdValue = "Bilocus";

        public static BakeResult Build(Document document, BakeBatch batch, double planarToleranceMeters)
        {
            if (batch == null) { throw new ArgumentNullException("batch"); }

            long start = Stopwatch.GetTimestamp();

            string refusal = DescribeWhyNotBakeable(document);
            if (refusal != null)
            {
                return Finish(BakeResult.Failed(batch, refusal), start);
            }

            // All announced and none arrived: no group to open. An empty
            // group would leave nothing in the history, but there is no
            // reason to touch the document to find that out.
            List<BakeMeshRequest> requests = batch.Requests;
            if (requests.Count == 0)
            {
                return Finish(BakeResult.Failed(batch, string.Format(
                    "no object to write: {0} announced and none arrived as a valid bake_mesh",
                    batch.AnnouncedIds.Count)), start);
            }

            // Explicit target, not derived from the batch: this builder only
            // writes DirectShapes, and MessageHandler only passes it those
            // batches.
            BakeResult result = new BakeResult(BakeResult.ActionBake, BakeTarget.DirectShape);
            result.RequestedCount = batch.AnnouncedIds.Count;
            result.MissingCount = batch.MissingIds.Count;

            try
            {
                // Before any transaction, as in ProxyBuilder: a schema GUID
                // collision is discovered here, with the document intact,
                // and not halfway through the group. Without a mark there is
                // no way to replace or remove, so the whole bake fails, not
                // the single object.
                ProxySchema.GetOrCreate();

                RunGroup(document, requests, planarToleranceMeters, result);
            }
            catch (Exception ex)
            {
                // What arrives here does not concern an object: the group
                // that does not open or does not close, the unreadable
                // schema. The group's using has already rolled back what was
                // written, so the write counts stay at zero (see Tally).
                result.Succeeded = false;
                result.FailureReason = Describe(ex);
            }

            return Finish(result, start);
        }

        // The reason a bake cannot be done, or null.
        //
        // The writability check is ProxyBuilder's, REUSED and not copied
        // (rule 3 inherited from A3). IsSupportedDocument is added on top
        // because it only concerns DirectShape: today
        // DescribeWhyNotWritable already excludes families, which is the
        // documented case, but the check costs nothing and the message stays
        // readable if Revit adds another one someday.
        private static string DescribeWhyNotBakeable(Document document)
        {
            string refusal = ProxyBuilder.DescribeWhyNotWritable(document);
            if (refusal != null) { return refusal; }

            if (!DirectShape.IsSupportedDocument(document))
            {
                return "the active document does not support DirectShape";
            }

            return null;
        }

        private static void RunGroup(
            Document document, List<BakeMeshRequest> requests, double planarToleranceMeters, BakeResult result)
        {
            Tally tally = new Tally();

            using (TransactionGroup group = new TransactionGroup(document, GroupName))
            {
                TransactionStatus started = group.Start();
                if (started != TransactionStatus.Started)
                {
                    result.Succeeded = false;
                    result.FailureReason = "the bake's transaction group did not open: " + started;
                    return;
                }

                foreach (BakeMeshRequest request in requests)
                {
                    BakeOne(document, request, planarToleranceMeters, result, tally);
                }

                // No object succeeded: roll back the group and false ok. The
                // object transactions have already all rolled back, but
                // assimilating an empty group would tell Blender "done" for
                // a bake that wrote nothing.
                if (tally.Written == 0)
                {
                    group.RollBack();
                    result.Succeeded = false;
                    result.FailureReason = "no object written to the document";
                    return;
                }

                TransactionStatus status = group.Assimilate();
                if (status != TransactionStatus.Committed)
                {
                    result.Succeeded = false;
                    result.FailureReason = "the bake was not confirmed by Revit: " + status;
                    return;
                }
            }

            // Only once the group is assimilated. A count of DirectShapes
            // created inside a group later rejected would tell the Blender
            // panel that there is stuff in the document that is not there.
            result.Succeeded = true;
            tally.CopyTo(result);
        }

        // One object: category, geometry, write in its transaction. Any
        // failure stays confined here and becomes AddFailure.
        private static void BakeOne(
            Document document, BakeMeshRequest request, double planarToleranceMeters, BakeResult result, Tally tally)
        {
            string name = request.Name;

            try
            {
                ElementId categoryId;
                string categoryRefusal = ResolveCategory(document, request.Category, out categoryId);
                if (categoryRefusal != null)
                {
                    result.AddFailure(name, categoryRefusal);
                    return;
                }

                // Planarity measured in WORLD coordinates: a non-uniform
                // scale changes the deviation of a polygon (see
                // BakeFaceSet). A negative determinant is a mirrored scale,
                // which without flipping the loops would reverse the
                // normals.
                double[] world = RowMajorMatrix.TransformPoints(request.Matrix, request.Mesh.Positions);
                bool flip = RowMajorMatrix.Determinant3x3(request.Matrix) < 0;

                // Builder and result stay alive until after SetShape: the
                // geometric objects come from there, and there is no reason
                // to find out whether Revit ties them to the result's
                // lifetime.
                BuiltShape shape;
                string buildRefusal = BuildShape(world, request.Mesh, planarToleranceMeters, flip, out shape);
                if (buildRefusal != null)
                {
                    result.AddFailure(name, buildRefusal);
                    return;
                }

                using (shape)
                {
                    TessellatedShapeBuilderOutcome outcome = shape.Result.Outcome;
                    if (outcome == TessellatedShapeBuilderOutcome.Nothing)
                    {
                        result.AddFailure(name, "Revit did not build any geometry from the received faces");
                        return;
                    }

                    // Can only be called once: subsequent calls raise.
                    IList<GeometryObject> geometry = shape.Result.GetGeometricalObjects();
                    if (geometry == null || geometry.Count == 0)
                    {
                        result.AddFailure(name, "the builder did not return geometric objects (outcome "
                            + outcome + ")");
                        return;
                    }

                    WriteKind kind;
                    int switched;
                    string writeRefusal = WriteInTransaction(
                        document, request, categoryId, geometry, out kind, out switched);
                    if (writeRefusal != null)
                    {
                        result.AddFailure(name, writeRefusal);
                        return;
                    }

                    // as_mesh means "did not come out as a closed
                    // solid". With AnyGeometry an open mesh comes out as
                    // a Sheet, not as a Mesh: to whoever reads the panel
                    // it is the same news (no volume), and counting it
                    // separately would require a field the contract does
                    // not have.
                    bool notSolid = outcome != TessellatedShapeBuilderOutcome.Solid;
                    tally.Add(kind, notSolid, shape.Faces.PlanarCount, shape.Faces.TriangulatedCount,
                        shape.SkippedFaces, switched);
                }
            }
            catch (Exception ex)
            {
                // Isolated to the single object: its transaction has already
                // rolled back (WriteInTransaction), the group continues with
                // the others. The reason goes back to Blender as the last
                // failed one.
                result.AddFailure(name, Describe(ex));
            }
        }

        // The category from the BuiltInCategory name, or the reason for the
        // rejection.
        //
        // BakeCategory has already enforced the OST_[A-Za-z0-9_]+ shape,
        // which is what stops Enum.TryParse from accepting numbers or
        // comma-separated lists. What is left is whether the name exists in
        // THIS version of Revit and whether the category is allowed for a
        // DirectShape, and only Revit knows.
        private static string ResolveCategory(Document document, string category, out ElementId categoryId)
        {
            categoryId = ElementId.InvalidElementId;

            BuiltInCategory builtIn;
            if (!Enum.TryParse<BuiltInCategory>(category, false, out builtIn))
            {
                return "category " + category + " unknown to this version of Revit";
            }

            ElementId candidate = new ElementId(builtIn);
            if (!DirectShape.IsValidCategoryId(candidate, document))
            {
                return "category " + category + " not allowed for a DirectShape";
            }

            categoryId = candidate;
            return null;
        }

        // Fills the builder with the object's faces. Returns the rejection
        // What BuildShape hands back: the builder, its result and the face
        // set it was filled with. The caller owns them: the DirectShape bake
        // disposes them after SetShape, the family bake keeps them alive
        // until the FreeFormElement exists.
        internal sealed class BuiltShape : IDisposable
        {
            public TessellatedShapeBuilder Builder;
            public TessellatedShapeBuilderResult Result;
            public BakeFaceSet Faces;
            public int SkippedFaces;

            public void Dispose()
            {
                if (Result != null) { Result.Dispose(); }
                if (Builder != null) { Builder.Dispose(); }
            }
        }

        // One object's geometry through the builder, with ONE retry.
        //
        // The planarity tolerance keeps whole the polygons that are planar
        // only within it. Found on subdivided surfaces (a Subdivision Surface
        // on a solidified shell): closed, manifold, no self-intersections,
        // and Revit fell back to a mesh with its quads whole; the same mesh
        // with every polygon as triangles came out as a Solid. No tighter
        // threshold fixes it: the deviations of those quads start at 1e-9 m,
        // below the float32 noise of exactly planar quads (a torus far from
        // the origin strays up to 4e-8 m). So the first build is the usual
        // one, a cube stays six squares, and when it does not come out as a
        // Solid the polygons that are not EXACTLY planar go to Blender's
        // triangles and the builder runs again. Only the objects that need
        // it pay for the second build.
        //
        // No retry on a Sheet: that is an open mesh Revit already stitched,
        // and a second build of a large terrain would change nothing. None
        // either when tolerance zero would triangulate nothing more. The
        // retry is kept only if it comes out as a Solid or a Sheet: a mesh
        // that stays a mesh keeps its whole faces.
        //
        // Returns the reason if no face is left, otherwise null. Raises like
        // TessellatedShapeBuilder.Build on the FIRST build; an exception in
        // the retry only discards the retry.
        internal static string BuildShape(double[] points, BakeMeshPayload mesh,
            double planarToleranceMeters, bool flipWinding, out BuiltShape shape)
        {
            BakeFaceSet faces = BakeFaceSet.Build(points, mesh, planarToleranceMeters, flipWinding);
            string refusal = BuildOnce(faces, points, out shape);
            if (refusal != null) { return refusal; }

            TessellatedShapeBuilderOutcome outcome = shape.Result.Outcome;
            if (outcome == TessellatedShapeBuilderOutcome.Solid
                || outcome == TessellatedShapeBuilderOutcome.Sheet)
            {
                return null;
            }

            BakeFaceSet exact = BakeFaceSet.Build(points, mesh, 0.0, flipWinding);
            if (exact.TriangulatedCount == faces.TriangulatedCount) { return null; }

            BuiltShape retry = null;
            try
            {
                if (BuildOnce(exact, points, out retry) != null) { return null; }
                TessellatedShapeBuilderOutcome retried = retry.Result.Outcome;
                if (retried != TessellatedShapeBuilderOutcome.Solid
                    && retried != TessellatedShapeBuilderOutcome.Sheet)
                {
                    return null;
                }
                shape.Dispose();
                shape = retry;
                retry = null;
                return null;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (retry != null) { retry.Dispose(); }
            }
        }

        private static string BuildOnce(BakeFaceSet faces, double[] points, out BuiltShape shape)
        {
            shape = new BuiltShape { Faces = faces, Builder = new TessellatedShapeBuilder() };
            try
            {
                string refusal = FillBuilder(shape.Builder, faces, points, out shape.SkippedFaces);
                if (refusal != null)
                {
                    shape.Dispose();
                    shape = null;
                    return refusal;
                }

                // Raises InvalidOperationException if the faces are so
                // inconsistent they cannot be used, or if there are too
                // many facets.
                shape.Builder.Build();
                shape.Result = shape.Builder.GetBuildResult();
                return null;
            }
            catch
            {
                if (shape != null) { shape.Dispose(); }
                shape = null;
                throw;
            }
        }

        // reason if no face is left, otherwise null.
        //
        // FamilyBaker reaches it through BuildShape, passing points in FAMILY
        // coordinates instead of world ones. For the builder it is the same
        // work (meters in, feet inside, the same skipped faces and the same
        // target/fallback choice), and a second copy would diverge at the
        // first fix.
        private static string FillBuilder(
            TessellatedShapeBuilder builder, BakeFaceSet faces, double[] worldPoints, out int skippedFaces)
        {
            skippedFaces = 0;

            ChooseTargetAndFallback(builder);

            XYZ[] points = ToFeet(worldPoints);

            builder.OpenConnectedFaceSet(true);

            int added = 0;
            foreach (int[] loop in faces.Loops)
            {
                // A new list per face: TessellatedFace's constructor states
                // it modifies the content of the list it receives. The XYZ
                // points instead are shared, they are immutable.
                List<XYZ> vertices = new List<XYZ>(loop.Length);
                for (int k = 0; k < loop.Length; k++)
                {
                    vertices.Add(points[loop[k]]);
                }

                TessellatedFace face = new TessellatedFace(vertices, ElementId.InvalidElementId);

                // Faces that Revit considers without enough vertices
                // (usually a triangle with two coincident vertices within
                // its tolerance) are SKIPPED and counted, like the degenerate
                // edges of proxies: AddFace would raise, and a
                // zero-area face must not cost the whole object. The count
                // goes into the Status text, which is where you look when an
                // object arrives with a hole.
                if (!builder.DoesFaceHaveEnoughLoopsAndVertices(face))
                {
                    face.Dispose();
                    skippedFaces++;
                    continue;
                }

                // No Dispose after AddFace: the builder empties the face's
                // loops as it adds it and declares it unusable, and does not
                // say more about who owns what. Leaving it to the GC is the
                // choice that cannot break the builder.
                builder.AddFace(face);
                added++;
            }

            if (added == 0)
            {
                // CloseConnectedFaceSet on an empty set raises: better a
                // reason written for the user.
                builder.CancelConnectedFaceSet();
                return string.Format(
                    "no face accepted by Revit: all {0} faces discarded as degenerate", skippedFaces);
            }

            builder.CloseConnectedFaceSet();
            return null;
        }

        // Solid if the mesh is closed, mesh otherwise.
        //
        // The ideal is Solid with a Mesh fallback. The
        // TessellatedShapeBuilder.Build documentation (RevitAPI.xml 2024 and
        // 2025) says, though, that the only supported combinations are
        // Solid/Abort, AnyGeometry/Mesh and Mesh/Salvage: Solid/Mesh is not
        // there, and today the branch taken is the second one. The check
        // stays because it is the right question to ask Revit instead of the
        // documentation, and the day a version accepts Solid/Mesh it is used
        // without touching code. Not Solid/Abort though: an open mesh (a
        // plane, a terrain) would fail instead of arriving as a mesh.
        private static void ChooseTargetAndFallback(TessellatedShapeBuilder builder)
        {
            if (builder.AreTargetAndFallbackCompatible(
                TessellatedShapeBuilderTarget.Solid, TessellatedShapeBuilderFallback.Mesh))
            {
                builder.Target = TessellatedShapeBuilderTarget.Solid;
                builder.Fallback = TessellatedShapeBuilderFallback.Mesh;
                return;
            }

            builder.Target = TessellatedShapeBuilderTarget.AnyGeometry;
            builder.Fallback = TessellatedShapeBuilderFallback.Mesh;
        }

        // Writing an object in its transaction, inside the group. Returns
        // the failure reason, or null; on any failure the transaction has
        // rolled back and kind is None.
        private static string WriteInTransaction(
            Document document, BakeMeshRequest request, ElementId categoryId,
            IList<GeometryObject> geometry, out WriteKind kind, out int switched)
        {
            kind = WriteKind.None;
            switched = 0;

            using (Transaction transaction = new Transaction(document, ObjectTransactionPrefix + request.Name))
            {
                TransactionStatus started = transaction.Start();
                if (started != TransactionStatus.Started)
                {
                    return "the object's transaction did not open: " + started;
                }

                string refusal;
                WriteKind written;
                int switchedNow;
                try
                {
                    refusal = Write(document, request, categoryId, geometry, out written, out switchedNow);
                }
                catch (Exception)
                {
                    // A half-done failure - the old shape deleted and the new
                    // one not created - must not stay in the document: the
                    // object's transaction is rolled back and the reason
                    // bubbles up to BakeOne, which counts it.
                    transaction.RollBack();
                    throw;
                }

                if (refusal != null)
                {
                    transaction.RollBack();
                    return refusal;
                }

                // Commit can return RolledBack because of Revit's failure
                // handling: in that case there is nothing in the document,
                // and the object failed even if Write went fine.
                TransactionStatus status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    return "the transaction was not confirmed: " + status;
                }

                // Only on confirmed commit: families deleted by a
                // rolled-back transaction are still in the document.
                kind = written;
                switched = switchedNow;
                return null;
            }
        }

        // Replacement or creation, inside an already open transaction.
        private static string Write(
            Document document, BakeMeshRequest request, ElementId categoryId,
            IList<GeometryObject> geometry, out WriteKind kind, out int switched)
        {
            kind = WriteKind.None;
            switched = 0;

            IList<ElementId> marked = ProxySchema.FindMarked(document, typeof(DirectShape), request.ObjectId);

            // More than one: the user has copied the element in Revit, and
            // the copy carries the mark with it. Updating one at random
            // would leave the other with the old shape and nobody would
            // know which of the two is "the Blender one"; deleting them all
            // would throw away an element the user wanted. The object stops,
            // and how to get out of it is stated.
            if (marked.Count > 1)
            {
                return string.Format(
                    "{0} Revit elements carry the same obj_id: remove the copies or use Remove bake",
                    marked.Count);
            }

            // An object has in Revit EITHER a
            // DirectShape OR a family. If the bridge had made it a family,
            // it goes away here with all its instances, in the object's
            // transaction: a rejection further below rolls it back together
            // with everything else, and the user is not left with neither
            // one nor the other.
            BakeElements.Sweep otherMode = BakeElements.Collect(
                document, new[] { request.ObjectId }, false, true);
            switched = BakeElements.DeleteAndCount(document, otherMode);

            DirectShape existing = null;
            if (marked.Count == 1)
            {
                existing = document.GetElement(marked[0]) as DirectShape;
                if (existing == null)
                {
                    return "the marked element " + marked[0].Value + " is not readable as a DirectShape";
                }
            }

            // Same category: SetShape on the existing element. Same
            // ElementId, so the parameters filled in by hand (Comments,
            // Mark) stay, which is why the re-bake does not recreate.
            if (existing != null && existing.Category != null && existing.Category.Id == categoryId)
            {
                if (!existing.IsValidShape(geometry))
                {
                    return DescribeRefusedShape(request.Category);
                }

                existing.SetShape(geometry);
                existing.SetName(request.Name);
                kind = WriteKind.Replaced;
                return null;
            }

            // Category changed: a DirectShape's category is fixed at
            // creation and cannot be changed, so it is deleted and
            // recreated. The result SAYS so (recreated), because the
            // parameters filled in by hand on the old element leave with
            // it.
            bool hadPrevious = existing != null;
            if (hadPrevious)
            {
                document.Delete(existing.Id);
            }

            DirectShape shape = DirectShape.CreateElement(document, categoryId);

            // IsValidShape before SetShape for the message: SetShape would
            // raise an ArgumentException anyway, but with a text meant for
            // developers. The rejection rolls back the transaction, and with
            // it the deletion of the old element.
            if (!shape.IsValidShape(geometry))
            {
                return DescribeRefusedShape(request.Category);
            }

            shape.SetShape(geometry);
            shape.ApplicationId = ApplicationIdValue;
            shape.ApplicationDataId = request.ObjectId;
            shape.SetName(request.Name);

            // Last, as in ProxyBuilder: if one of the lines above raises,
            // the whole transaction fails and no half-marked element stays
            // in the document.
            ProxySchema.Mark(shape, request.ObjectId);

            kind = hadPrevious ? WriteKind.Recreated : WriteKind.Created;
            return null;
        }

        private static string DescribeRefusedShape(string category)
        {
            return "Revit refuses the built geometry as a DirectShape shape in " + category;
        }

        // World meters (what comes out of RowMajorMatrix) to feet, Revit's
        // internal units. The factor lives in BridgeConstants, as for
        // proxies: a second constant for the same conversion would diverge.
        private static XYZ[] ToFeet(double[] worldPoints)
        {
            XYZ[] points = new XYZ[worldPoints.Length / 3];
            for (int i = 0; i < points.Length; i++)
            {
                int at = i * 3;
                points[i] = new XYZ(
                    worldPoints[at] * BridgeConstants.FeetPerMeter,
                    worldPoints[at + 1] * BridgeConstants.FeetPerMeter,
                    worldPoints[at + 2] * BridgeConstants.FeetPerMeter);
            }
            return points;
        }

        private static string Describe(Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }

        private static BakeResult Finish(BakeResult result, long start)
        {
            result.ElapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return result;
        }

        private enum WriteKind
        {
            None,
            Created,
            Replaced,
            Recreated
        }

        // The counts of written objects, held aside until the group is
        // assimilated. Only then do they end up in BakeResult: a
        // rolled-back group leaves nothing in the document, and the numbers
        // must say so. Failures instead go straight into BakeResult,
        // because they are a fact of the object and not of the write.
        private sealed class Tally
        {
            public int Written;
            public int Created;
            public int Replaced;
            public int Recreated;
            public int AsMesh;
            public int FacesPlanar;
            public int FacesTriangulated;
            public int SkippedFaces;
            public int Switched;

            public void Add(
                WriteKind kind, bool asMesh, int facesPlanar, int facesTriangulated, int skippedFaces, int switched)
            {
                Written++;
                Switched += switched;
                if (kind == WriteKind.Created) { Created++; }
                else if (kind == WriteKind.Replaced) { Replaced++; }
                else if (kind == WriteKind.Recreated) { Recreated++; }

                if (asMesh) { AsMesh++; }
                FacesPlanar += facesPlanar;
                FacesTriangulated += facesTriangulated;
                SkippedFaces += skippedFaces;
            }

            public void CopyTo(BakeResult result)
            {
                result.CreatedCount = Created;
                result.ReplacedCount = Replaced;
                result.RecreatedCount = Recreated;
                result.AsMeshCount = AsMesh;
                result.FacesPlanarCount = FacesPlanar;
                result.FacesTriangulatedCount = FacesTriangulated;
                result.SkippedFaceCount = SkippedFaces;
                result.SwitchedCount = Switched;
            }
        }
    }
}
