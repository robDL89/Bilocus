// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Bilocus.Geometry;
using Bilocus.Protocol;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Bake
{
    // The Phase B2 bake: each object of a BakeBatch with target family
    // becomes a loadable family BL_<name> containing a FreeFormElement, plus
    // an instance placed where the object is.
    //
    // WHY IT IS NOT A COPY OF BakeBuilder. A DirectShape is written entirely
    // inside a single project transaction. A family is not: the geometry
    // goes into ANOTHER document (the family document), and passing it to
    // the project is LoadFamily, which wants the project WITHOUT open
    // transactions (docs/family-bake-findings.md, finding 6; RevitAPI.xml
    // also says so for EditFamily). So for each object:
    //
    //   creation:   NewFamilyDocument -> [T family: category, FreeFormElement,
    //               name, mark] -> LoadFamily -> Close(false)
    //               -> [T project: family mark, instance, rotation, instance
    //               mark, DirectShapes of the other mode deleted]
    //   re-bake:    EditFamily -> [T family: UpdateSolidGeometry, category]
    //               -> LoadFamily overwriting -> Close(false)
    //               -> [T project: instance moved/created/left, rename,
    //               DirectShapes deleted]
    //
    // No project transaction is ever open during EditFamily or LoadFamily.
    // The family document is ALWAYS closed, in a finally: in testing an
    // exception left ghost documents open. And if it is Close(false) itself
    // that fails, at the end of the batch FamilyDocumentLeaks closes the
    // documents opened by the batch and still open, and writes it in the
    // Note.
    //
    // CTRL+Z. A TransactionGroup is tried around the whole batch, as in the
    // DirectShape bake. If Revit rejects LoadFamily or EditFamily with the
    // group open (nobody could test it outside the add-in), the group rolls
    // back, it is remembered for the session and the batch is run again from
    // scratch without a group: more undo entries, stated in the Status Note.
    //
    // As in BakeBuilder: a failure that concerns an object fails only that
    // ONE object, counted and named in BakeResult. No TaskDialog.
    public static class FamilyBaker
    {
        public const string GroupName = "Bilocus: family bake";
        public const string ObjectTransactionPrefix = "Bilocus: family bake ";
        public const string CleanupTransactionPrefix = "Bilocus: family cleanup ";
        public const string FamilyTransactionName = "Bilocus: geometry";

        // Decision 1: English\Metric Generic Model.rft, no search in other
        // languages. Testing found an installation with the default library
        // set to Italian and the English folder present anyway.
        public const string TemplateLanguageFolder = "English";
        public const string TemplateFileName = "Metric Generic Model.rft";

        public const string OpenMeshRefusal =
            "open mesh: check 'accept open solid' or use Bake DirectShape";
        public const string NonManifoldRefusal =
            "non-manifold mesh: a family wants a solid, use Bake DirectShape";

        // Below these thresholds moving or rotating the instance would be
        // float32 noise, and every change is an element changed in the
        // document (worksharing, history) for no reason.
        private const double MoveToleranceFeet = 1e-9;
        private const double AngleToleranceRadians = 1e-9;

        // Whether LoadFamily/EditFamily are allowed with a TransactionGroup
        // open on the project. null: not yet discovered in this Revit
        // session. Static on purpose: it is a fact about Revit, not about the
        // document, and rediscovering it on every bake would mean redoing
        // half the batch every time.
        private static bool? _groupAllowsFamilyLoad;

        public static bool? GroupAllowsFamilyLoad
        {
            get { return _groupAllowsFamilyLoad; }
        }

        // The template path for this version of Revit. Public because the
        // error message cites it, and whoever reads the Status must be able
        // to compare it with the disk.
        public static string TemplatePath(Application application)
        {
            if (application == null) { throw new ArgumentNullException("application"); }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Autodesk",
                "RVT " + application.VersionNumber,
                "Family Templates",
                TemplateLanguageFolder,
                TemplateFileName);
        }

        public static BakeResult Build(Document project, BakeBatch batch, double planarToleranceMeters)
        {
            if (batch == null) { throw new ArgumentNullException("batch"); }

            long start = Stopwatch.GetTimestamp();

            if (batch.Target != BakeTarget.Family)
            {
                return Finish(BakeResult.Failed(batch,
                    "FamilyBaker only accepts batches with target family, not " + batch.Target), start);
            }

            // The same writability check as proxies and DirectShapes, from
            // the same method. Also excludes IsModifiable: with a
            // transaction already open, LoadFamily would fail anyway, and
            // with the batch already started.
            string refusal = ProxyBuilder.DescribeWhyNotWritable(project);
            if (refusal != null)
            {
                return Finish(BakeResult.Failed(batch, refusal), start);
            }

            if (batch.Requests.Count == 0)
            {
                return Finish(BakeResult.Failed(batch, string.Format(
                    "no object to write: {0} announced and none arrived as a valid bake_mesh",
                    batch.AnnouncedIds.Count)), start);
            }

            // The template is missing: the whole batch fails, with the path.
            // Every object would fail the same way, and fifty identical
            // lines say less than one.
            string template = TemplatePath(project.Application);
            if (!File.Exists(template))
            {
                return Finish(BakeResult.Failed(batch,
                    "family template not found: " + template
                    + " (the English Revit template library is required)"), start);
            }

            // Shared between the run with the group and its possible
            // fallback: the documents opened by either one are checked
            // together.
            FamilyDocumentLeaks<Document> opened = new FamilyDocumentLeaks<Document>();

            BakeResult result;
            try
            {
                // Before any document and transaction, as in BakeBuilder:
                // without a mark there is no way to update or remove, so the
                // whole batch fails.
                ProxySchema.GetOrCreate();

                bool knownRefused = _groupAllowsFamilyLoad == false;
                Run run = new Run(project, batch, planarToleranceMeters, template, !knownRefused, false, opened);
                run.Execute();

                if (run.GroupRefusal != null)
                {
                    // The group has already rolled back inside Execute, and
                    // no write to the project had happened: the rejection
                    // arrives at the FIRST LoadFamily/EditFamily of the batch
                    // (see Run.CallFamilyApi). We start over without a
                    // group.
                    string why = run.GroupRefusal;
                    string earlierNote = run.Result.Note;

                    _groupAllowsFamilyLoad = false;
                    Run retry = new Run(project, batch, planarToleranceMeters, template, false, true, opened);
                    retry.Execute();

                    if (retry.RefusalDisproved || !retry.LoadSucceeded)
                    {
                        // The same error without the group, or no successful
                        // LoadFamily to confirm it: the group is not the
                        // proven cause. An unproven rejection is not stored,
                        // the next bake tries again with the group.
                        _groupAllowsFamilyLoad = null;
                        AppendNote(retry.Result, "first attempt with TransactionGroup failed (" + why
                            + "), re-run without a group; the group's rejection is not confirmed and will be "
                            + "retried on the next bake");
                    }
                    else
                    {
                        AppendNote(retry.Result, "TransactionGroup rejected by Revit (" + why
                            + "): bake re-run without a group, one undo entry per write "
                            + "(remembered for this session)");
                    }

                    if (!string.IsNullOrEmpty(earlierNote)) { AppendNote(retry.Result, earlierNote); }
                    run = retry;
                }
                else if (knownRefused)
                {
                    AppendNote(run.Result, "bake without a TransactionGroup (rejected by Revit in this session): "
                        + "one undo entry per write");
                }

                result = run.Result;
            }
            catch (Exception ex)
            {
                // What arrives here does not concern an object: the schema,
                // the group that does not open or does not close cleanly.
                // With the group, the using has already rolled it back.
                result = BakeResult.Failed(batch, Describe(ex));
            }

            // Last safety net, even after a batch failure: every family
            // document opened by this bake and still open is closed here.
            Application application = project.Application;
            AppendNote(result, opened.Sweep(
                document => IsStillOpen(application, document),
                document => document.Close(false) ? null : "Close returned false"));

            return Finish(result, start);
        }

        // Open = object still valid AND still among Revit's documents. Both
        // are needed: if either check did not behave as expected after
        // Close, the result would be closing nothing, closing twice, or
        // reporting a false alarm on every bake.
        private static bool IsStillOpen(Application application, Document document)
        {
            if (!document.IsValidObject) { return false; }

            foreach (Document open in application.Documents)
            {
                if (open != null && open.Equals(document)) { return true; }
            }
            return false;
        }

        private static void AppendNote(BakeResult result, string note)
        {
            if (string.IsNullOrEmpty(note)) { return; }
            result.Note = string.IsNullOrEmpty(result.Note) ? note : result.Note + "; " + note;
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

        private static XYZ ToFeet(double[] meters)
        {
            return new XYZ(
                meters[0] * BridgeConstants.FeetPerMeter,
                meters[1] * BridgeConstants.FeetPerMeter,
                meters[2] * BridgeConstants.FeetPerMeter);
        }

        // In (-pi, pi]: the difference between two angles in (-pi, pi] can
        // reach almost 2pi, and rotating by 350 degrees instead of -10 gives
        // the same instance but a wrong rotation halfway through an update.
        private static double NormalizeAngle(double angle)
        {
            while (angle > Math.PI) { angle -= 2.0 * Math.PI; }
            while (angle <= -Math.PI) { angle += 2.0 * Math.PI; }
            return angle;
        }

        // Revit rejected LoadFamily/EditFamily with the group open, before
        // the batch wrote anything. It is not a failure of the object: it
        // interrupts the run and has it redone without a group.
        private sealed class GroupRefusedException : Exception
        {
            public GroupRefusedException(string message) : base(message) { }
        }

        // One execution of the batch, with or without a group. A class and
        // not static methods because the fallback runs again from scratch: a
        // new run has new counts, and those of the aborted run must not end
        // up in the result.
        private sealed class Run
        {
            private readonly Document _project;
            private readonly BakeBatch _batch;
            private readonly double _tolerance;
            private readonly string _template;
            private readonly bool _useGroup;
            private readonly bool _verifyingRefusal;
            private readonly Tally _tally = new Tally();
            private readonly FamilyDocumentLeaks<Document> _opened;

            public readonly BakeResult Result;

            // Not null: Revit rejected the group, this run is to be
            // discarded.
            public string GroupRefusal;

            // Verification run: InvalidOperationException from
            // LoadFamily/EditFamily even without a group, before any
            // successful LoadFamily.
            public bool RefusalDisproved;

            public bool LoadSucceeded;

            public Run(Document project, BakeBatch batch, double tolerance, string template,
                bool useGroup, bool verifyingRefusal, FamilyDocumentLeaks<Document> opened)
            {
                _opened = opened;
                _project = project;
                _batch = batch;
                _tolerance = tolerance;
                _template = template;
                _useGroup = useGroup;
                _verifyingRefusal = verifyingRefusal;

                Result = new BakeResult(BakeResult.ActionBake, BakeTarget.Family);
                Result.RequestedCount = batch.AnnouncedIds.Count;
                Result.MissingCount = batch.MissingIds.Count;
            }

            public void Execute()
            {
                if (_useGroup) { ExecuteInGroup(); }
                else { ExecuteWithoutGroup(); }
            }

            private void ExecuteInGroup()
            {
                using (TransactionGroup group = new TransactionGroup(_project, GroupName))
                {
                    TransactionStatus started = group.Start();
                    if (started != TransactionStatus.Started)
                    {
                        Fail("the bake's transaction group did not open: " + started);
                        return;
                    }

                    RunObjects();

                    if (GroupRefusal != null)
                    {
                        group.RollBack();
                        return;
                    }

                    // No object succeeded: roll everything back, including a
                    // reloaded geometry for an object whose instance then did
                    // not get updated. With the group this is possible, and
                    // an intact document is the clearest answer to
                    // "failed".
                    if (_tally.Written == 0)
                    {
                        group.RollBack();
                        Fail("no object written to the document");
                        return;
                    }

                    TransactionStatus status = group.Assimilate();
                    if (status != TransactionStatus.Committed)
                    {
                        Fail("the bake was not confirmed by Revit: " + status);
                        return;
                    }
                }

                // Only once the group is assimilated, as in BakeBuilder.
                Result.Succeeded = true;
                _tally.CopyTo(Result);
            }

            // Without a group, every write is already confirmed when it is
            // counted: the counts are valid even if the run is interrupted,
            // and go into the result regardless.
            private void ExecuteWithoutGroup()
            {
                try
                {
                    RunObjects();
                }
                catch (Exception ex)
                {
                    _tally.CopyTo(Result);
                    Fail(Describe(ex));
                    return;
                }

                _tally.CopyTo(Result);
                if (_tally.Written == 0)
                {
                    Fail("no object written to the document");
                    return;
                }
                Result.Succeeded = true;
            }

            private void Fail(string reason)
            {
                Result.Succeeded = false;
                Result.FailureReason = reason;
            }

            private void RunObjects()
            {
                foreach (BakeMeshRequest request in _batch.Requests)
                {
                    BakeOne(request);
                    if (GroupRefusal != null) { return; }
                }
            }

            private void BakeOne(BakeMeshRequest request)
            {
                try
                {
                    string refusal = BakeObject(request);
                    if (refusal != null) { Result.AddFailure(request.Name, refusal); }
                }
                catch (GroupRefusedException ex)
                {
                    GroupRefusal = ex.Message;
                }
                catch (Exception ex)
                {
                    // Isolated to the single object: open transactions have
                    // already rolled back (InTransaction), the family
                    // document is already closed (finally), the batch
                    // continues.
                    Result.AddFailure(request.Name, Describe(ex));
                }
            }

            // One object. Returns the failure reason, or null.
            private string BakeObject(BakeMeshRequest request)
            {
                // Category: existence here, admissibility for a family only
                // when it is set on the family document (Revit is the one
                // that knows, see SetCategory).
                BuiltInCategory builtIn;
                if (!Enum.TryParse<BuiltInCategory>(request.Category, false, out builtIn))
                {
                    return "category " + request.Category + " unknown to this version of Revit";
                }

                Category category = Category.GetCategory(_project, builtIn);
                if (category == null)
                {
                    return "category " + request.Category + " not allowed for a family";
                }

                // Geometry OUTSIDE any transaction and before opening
                // documents: an object the builder rejects must not cost an
                // opened-and-closed family document.
                //
                // Points in FAMILY coordinates: the linear part of the
                // matrix minus the in-plane rotation, which goes to the
                // instance. Planarity is measured here as in the world,
                // because a rotation around Z does not change a polygon's
                // deviation.
                FamilyPlacement placement = FamilyPlacement.Decompose(request.Matrix);
                double[] points = placement.ToFamilyPoints(request.Mesh.Positions);
                BakeFaceSet faces = BakeFaceSet.Build(points, request.Mesh, _tolerance, placement.FlipWinding);

                using (TessellatedShapeBuilder builder = new TessellatedShapeBuilder())
                {
                    int skippedFaces;
                    string fillRefusal = BakeBuilder.FillBuilder(builder, faces, points, out skippedFaces);
                    if (fillRefusal != null) { return fillRefusal; }

                    builder.Build();

                    // Builder and result stay alive until after LoadFamily:
                    // the Solid comes from there, and FreeFormElement.Create /
                    // UpdateSolidGeometry only make a copy of it when they
                    // are called.
                    using (TessellatedShapeBuilderResult built = builder.GetBuildResult())
                    {
                        // Decision 6: Solid always, Sheet (open shell) only
                        // with the checkbox, Mesh/Mixed/Nothing never: they
                        // do not give a Solid, and without a Solid there is
                        // no FreeFormElement.
                        TessellatedShapeBuilderOutcome outcome = built.Outcome;
                        bool openShell = false;
                        if (outcome == TessellatedShapeBuilderOutcome.Sheet)
                        {
                            if (!request.AcceptOpen) { return OpenMeshRefusal; }
                            openShell = true;
                        }
                        else if (outcome != TessellatedShapeBuilderOutcome.Solid)
                        {
                            return NonManifoldRefusal;
                        }

                        Solid solid = FirstSolid(built.GetGeometricalObjects());
                        if (solid == null)
                        {
                            return "the builder did not return a solid (outcome " + outcome + ")";
                        }

                        IList<ElementId> families = BakeElements.Families(_project, request.ObjectId);
                        if (families.Count > 1)
                        {
                            return string.Format(
                                "{0} families carry the same obj_id: remove the copies or use Remove bake",
                                families.Count);
                        }

                        ObjectWrite write = families.Count == 0
                            ? CreateFamily(request, category, solid, placement)
                            : UpdateFamily(request, families[0], category, solid, placement);

                        if (write.Refusal != null) { return write.Refusal; }

                        _tally.Add(write.Created, openShell, faces.PlanarCount, faces.TriangulatedCount,
                            skippedFaces, write.Switched, write.NotMoved);
                        if (!string.IsNullOrEmpty(write.Note)) { AppendNote(Result, write.Note); }
                        return null;
                    }
                }
            }

            private static Solid FirstSolid(IList<GeometryObject> geometry)
            {
                if (geometry == null) { return null; }
                foreach (GeometryObject item in geometry)
                {
                    Solid solid = item as Solid;
                    if (solid != null) { return solid; }
                }
                return null;
            }

            // No marked family: new document from the template, loaded,
            // closed; then the instance in the project.
            private ObjectWrite CreateFamily(
                BakeMeshRequest request, Category category, Solid solid, FamilyPlacement placement)
            {
                // The level before the family document: a project without
                // levels must not receive a family that cannot be placed.
                Level level = PickLevel(placement);
                if (level == null) { return ObjectWrite.Refused(NoLevelRefusal); }

                // Unique among the project's families: with
                // OverwriteFamilyLoadOptions, a name already taken by a
                // user's family would mean reloading OVER their family.
                string familyName = FamilyNaming.MakeUnique(
                    FamilyNaming.BuildName(request.Name), FamilyNames(ElementId.InvalidElementId));

                Document familyDoc = _project.Application.NewFamilyDocument(_template);
                if (familyDoc == null)
                {
                    return ObjectWrite.Refused("Revit did not open a family document from the template " + _template);
                }
                _opened.Register(familyDoc, request.Name);

                Family loaded;
                try
                {
                    string refusal = InTransaction(familyDoc, FamilyTransactionName, () =>
                    {
                        string categoryRefusal = SetCategory(familyDoc, category, request.Category);
                        if (categoryRefusal != null) { return categoryRefusal; }

                        FreeFormElement freeForm = FreeFormElement.Create(familyDoc, solid);
                        familyDoc.OwnerFamily.Name = familyName;

                        // Last, as in the other builders: an exception above
                        // rolls everything back and no half-marked element
                        // stays.
                        ProxySchema.Mark(freeForm, request.ObjectId);
                        return null;
                    });
                    if (refusal != null) { return ObjectWrite.Refused(refusal); }

                    loaded = LoadInto(familyDoc);
                }
                finally
                {
                    CloseFamilyDocument(familyDoc, request.Name);
                }

                if (loaded == null)
                {
                    return ObjectWrite.Refused("LoadFamily did not return the family " + familyName
                        + ": check the project browser to see if it was loaded");
                }

                int switched = 0;
                string placeRefusal = TryInTransaction(_project, ObjectTransactionPrefix + request.Name, () =>
                {
                    ProxySchema.Mark(loaded, request.ObjectId);

                    string instanceRefusal = PlaceInstance(loaded, level, placement, request.ObjectId);
                    if (instanceRefusal != null) { return instanceRefusal; }

                    // Decision 7: the DirectShape of the same object goes
                    // away in the same transaction as the instance.
                    switched = DeleteDirectShapes(request.ObjectId);
                    return null;
                });

                if (placeRefusal == null)
                {
                    return ObjectWrite.Done(true, switched, false, null);
                }

                // The family is loaded but without a mark or an instance: on
                // the next bake it would not be found and a BL_..._2 would
                // be born. It is removed, and if that fails it is stated.
                return ObjectWrite.Refused(placeRefusal + RemoveOrphanFamily(loaded.Id, familyName, request.Name));
            }

            // A marked family: EditFamily, geometry updated on the bridge's
            // FreeFormElement (the user's voids stay, finding 10),
            // reloaded; then the instance.
            private ObjectWrite UpdateFamily(
                BakeMeshRequest request, ElementId familyId, Category category, Solid solid, FamilyPlacement placement)
            {
                Family family = _project.GetElement(familyId) as Family;
                if (family == null)
                {
                    return ObjectWrite.Refused("the marked element " + familyId.Value + " is not readable as a Family");
                }

                string familyName = family.Name;
                if (!family.IsEditable)
                {
                    return ObjectWrite.Refused("the family " + familyName + " is not editable by API");
                }

                // Plan decision, point 8: family open in the editor. Checked
                // BEFORE calling EditFamily, because EditFamily's exception
                // for "already being edited" is the same class as the one
                // for "group open": without this check an open editor would
                // trigger the fallback without a group.
                if (IsOpenInEditor(familyName))
                {
                    return ObjectWrite.Refused(EditorOpenRefusal(familyName));
                }

                Document familyDoc;
                try
                {
                    familyDoc = CallFamilyApi("EditFamily", () => _project.EditFamily(family), false);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    return ObjectWrite.Refused(EditorOpenRefusal(familyName) + " (Revit: " + ex.Message + ")");
                }

                if (familyDoc == null)
                {
                    return ObjectWrite.Refused("EditFamily did not return the document of family " + familyName);
                }
                _opened.Register(familyDoc, request.Name);

                Family loaded;
                try
                {
                    string refusal = InTransaction(familyDoc, FamilyTransactionName, () =>
                    {
                        FreeFormElement freeForm;
                        string findRefusal = FindBridgeFreeForm(familyDoc, request.ObjectId, familyName, out freeForm);
                        if (findRefusal != null) { return findRefusal; }

                        freeForm.UpdateSolidGeometry(solid);

                        // For a family the category is changed in place: it
                        // is always replaced, never recreated (contract).
                        if (familyDoc.OwnerFamily.FamilyCategoryId != category.Id)
                        {
                            return SetCategory(familyDoc, category, request.Category);
                        }
                        return null;
                    });
                    if (refusal != null) { return ObjectWrite.Refused(refusal); }

                    loaded = LoadInto(familyDoc);
                }
                finally
                {
                    CloseFamilyDocument(familyDoc, request.Name);
                }

                Family target = loaded != null ? loaded : _project.GetElement(familyId) as Family;
                if (target == null)
                {
                    return ObjectWrite.Refused("after LoadFamily the family " + familyName + " cannot be found in the project");
                }

                int switched = 0;
                bool notMoved = false;
                string renameNote = null;
                string instanceRefusal = TryInTransaction(_project, ObjectTransactionPrefix + request.Name, () =>
                {
                    List<ElementId> instances = MarkedInstancesOf(target.Id, request.ObjectId);

                    if (instances.Count == 1)
                    {
                        string moveRefusal = MoveInstance(instances[0], placement);
                        if (moveRefusal != null) { return moveRefusal; }
                    }
                    else if (instances.Count == 0)
                    {
                        // Deleted by the user: one is put back, as on
                        // creation. A family without instances is not what
                        // the bake promises.
                        Level level = PickLevel(placement);
                        if (level == null) { return NoLevelRefusal; }

                        string placeRefusal = PlaceInstance(target, level, placement, request.ObjectId);
                        if (placeRefusal != null) { return placeRefusal; }
                    }
                    else
                    {
                        // Decision 8: copies made in Revit carry the mark,
                        // and it is not known which one is "the Blender
                        // one". Geometry updated on all of them (it is the
                        // family), no move.
                        notMoved = true;
                    }

                    renameNote = RenameIfFree(target, request.Name);
                    switched = DeleteDirectShapes(request.ObjectId);
                    return null;
                });

                if (instanceRefusal != null)
                {
                    return ObjectWrite.Refused("geometry of family " + familyName
                        + " reloaded, but instance not updated: " + instanceRefusal);
                }

                return ObjectWrite.Done(false, switched, notMoved, renameNote);
            }

            // Every call that does not allow open project transactions goes
            // through here: it is the point where it is discovered whether
            // TransactionGroup is allowed.
            //
            // With the group and the answer still unknown, an
            // InvalidOperationException becomes GroupRefusedException. It
            // is always the first call of this kind that can receive it
            // before the batch writes to the project: the project
            // transactions come AFTER LoadFamily, for each object. The
            // rejection has the same class as other causes (family being
            // edited, read-only): the verification run without a group
            // exists so as not to store the wrong cause.
            private T CallFamilyApi<T>(string what, Func<T> call, bool isLoad)
            {
                try
                {
                    T value = call();
                    if (isLoad)
                    {
                        LoadSucceeded = true;
                        if (_useGroup && _groupAllowsFamilyLoad == null) { _groupAllowsFamilyLoad = true; }
                    }
                    return value;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
                {
                    if (_useGroup && _groupAllowsFamilyLoad == null)
                    {
                        throw new GroupRefusedException(what + ": " + ex.Message);
                    }
                    if (_verifyingRefusal && !LoadSucceeded)
                    {
                        RefusalDisproved = true;
                    }
                    throw;
                }
            }

            private Family LoadInto(Document familyDoc)
            {
                return CallFamilyApi("LoadFamily",
                    () => familyDoc.LoadFamily(_project, new OverwriteFamilyLoadOptions()), true);
            }

            // Close(false) always, and a failure to close is not swallowed:
            // it ends up in the Status Note. It does not fail the object,
            // whose write is already decided, and it must not hide the
            // exception that got here inside a finally.
            private void CloseFamilyDocument(Document familyDoc, string objectName)
            {
                try
                {
                    if (!familyDoc.Close(false))
                    {
                        AppendNote(Result, "family document of '" + objectName
                            + "' not closed: Close returned false");
                    }
                }
                catch (Exception ex)
                {
                    AppendNote(Result, "family document of '" + objectName + "' not closed: " + Describe(ex));
                }
            }

            private string RemoveOrphanFamily(ElementId familyId, string familyName, string objectName)
            {
                string refusal = TryInTransaction(_project, CleanupTransactionPrefix + objectName, () =>
                {
                    _project.Delete(familyId);
                    return null;
                });

                return refusal == null
                    ? " (the family " + familyName + " just loaded was removed)"
                    : " - the family " + familyName + " stayed loaded WITHOUT a mark, remove it by hand: " + refusal;
            }

            // The bridge's marked FreeFormElement in the family document. The
            // search is by GenericForm, the base class, and then filtered:
            // filtering by class on a subclass is not guaranteed for all of
            // them, while GenericForm is the class Revit uses to enumerate
            // shapes.
            private static string FindBridgeFreeForm(
                Document familyDoc, string objectId, string familyName, out FreeFormElement freeForm)
            {
                freeForm = null;

                List<FreeFormElement> found = new List<FreeFormElement>();
                foreach (ElementId id in ProxySchema.FindMarked(familyDoc, typeof(GenericForm), objectId))
                {
                    FreeFormElement candidate = familyDoc.GetElement(id) as FreeFormElement;
                    if (candidate != null) { found.Add(candidate); }
                }

                if (found.Count == 0)
                {
                    return "bridge FreeFormElement not found in family " + familyName;
                }
                if (found.Count > 1)
                {
                    return string.Format(
                        "{0} FreeFormElement carry the same obj_id in family {1}: remove the copies in the editor",
                        found.Count, familyName);
                }

                freeForm = found[0];
                return null;
            }

            private static string SetCategory(Document familyDoc, Category category, string categoryName)
            {
                try
                {
                    familyDoc.OwnerFamily.FamilyCategoryId = category.Id;
                    return null;
                }
                catch (Exception ex)
                {
                    // The expected case is ArgumentException for a system
                    // category (walls, floors): Revit's reason stays in the
                    // text.
                    return "category " + categoryName + " not allowed for a family (" + Describe(ex) + ")";
                }
            }

            // A family document open with the same name. The title of a
            // family open in the editor is the family name, with the
            // extension if Windows shows it. A same-named .rfa opened from
            // disk triggers the same message: closing it costs little.
            private bool IsOpenInEditor(string familyName)
            {
                foreach (Document open in _project.Application.Documents)
                {
                    if (open == null || !open.IsFamilyDocument) { continue; }

                    string title = open.Title ?? "";
                    if (title.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                    {
                        title = title.Substring(0, title.Length - 4);
                    }

                    if (string.Equals(title, familyName, StringComparison.OrdinalIgnoreCase)) { return true; }
                }
                return false;
            }

            private static string EditorOpenRefusal(string familyName)
            {
                return "close the editor of family " + familyName + " and try again";
            }

            private const string NoLevelRefusal =
                "no level in the project: a level is needed to place the instance";

            // Decision 4: the closest level below the origin. Elevations as
            // ProjectElevation, i.e. relative to the project origin and not
            // the shared point: it is the coordinate system that arrives
            // from the wire (Revit's internal origin, CLAUDE.md).
            private Level PickLevel(FamilyPlacement placement)
            {
                List<Level> levels = new List<Level>();
                List<double> elevations = new List<double>();

                foreach (Element element in new FilteredElementCollector(_project).OfClass(typeof(Level)))
                {
                    Level level = element as Level;
                    if (level == null) { continue; }
                    levels.Add(level);
                    elevations.Add(level.ProjectElevation * BridgeConstants.MetersPerFoot);
                }

                int index = LevelPicker.Pick(elevations, placement.OriginMeters[2]);
                return index < 0 ? null : levels[index];
            }

            // Inside an already open project transaction.
            private string PlaceInstance(Family family, Level level, FamilyPlacement placement, string objectId)
            {
                FamilySymbol symbol = FirstSymbol(family);
                if (symbol == null) { return "family " + family.Name + " has no types to place"; }

                // A just-loaded type can be inactive, and an instance of an
                // inactive type has no geometry until it is regenerated.
                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    _project.Regenerate();
                }

                // Absolute Z: Revit computes the offset from the level
                // (testing, finding 8).
                XYZ origin = ToFeet(placement.OriginMeters);
                FamilyInstance instance = _project.Create.NewFamilyInstance(
                    origin, symbol, level, StructuralType.NonStructural);
                if (instance == null) { return "Revit did not create the instance of family " + family.Name; }

                if (Math.Abs(placement.AngleRadians) > AngleToleranceRadians)
                {
                    ElementTransformUtils.RotateElement(
                        _project, instance.Id, Line.CreateBound(origin, origin + XYZ.BasisZ), placement.AngleRadians);
                }

                ProxySchema.Mark(instance, objectId);
                return null;
            }

            // The bridge's one and only instance follows the object:
            // translation with MoveElement, then the rotation brought to
            // AngleRadians by rotating the difference around the new
            // insertion point.
            private string MoveInstance(ElementId instanceId, FamilyPlacement placement)
            {
                FamilyInstance instance = _project.GetElement(instanceId) as FamilyInstance;
                LocationPoint location = instance == null ? null : instance.Location as LocationPoint;
                if (location == null)
                {
                    return "instance " + instanceId.Value + " has no insertion point to move";
                }

                XYZ target = ToFeet(placement.OriginMeters);
                XYZ current = location.Point;
                if (!current.IsAlmostEqualTo(target, MoveToleranceFeet))
                {
                    ElementTransformUtils.MoveElement(_project, instanceId, target - current);
                }

                double delta = NormalizeAngle(placement.AngleRadians - location.Rotation);
                if (Math.Abs(delta) > AngleToleranceRadians)
                {
                    ElementTransformUtils.RotateElement(
                        _project, instanceId, Line.CreateBound(target, target + XYZ.BasisZ), delta);
                }

                return null;
            }

            private FamilySymbol FirstSymbol(Family family)
            {
                foreach (ElementId id in family.GetFamilySymbolIds())
                {
                    FamilySymbol symbol = _project.GetElement(id) as FamilySymbol;
                    if (symbol != null) { return symbol; }
                }
                return null;
            }

            // The marked instances with the obj_id that really belong to
            // THIS family: an instance whose type the user changed to
            // another family still carries the mark, but it is no longer
            // this object's geometry and must not be moved.
            private List<ElementId> MarkedInstancesOf(ElementId familyId, string objectId)
            {
                HashSet<ElementId> ofFamily = new HashSet<ElementId>(
                    BakeElements.InstancesOf(_project, new[] { familyId }));

                List<ElementId> found = new List<ElementId>();
                foreach (ElementId id in BakeElements.MarkedInstances(_project, objectId))
                {
                    if (ofFamily.Contains(id)) { found.Add(id); }
                }
                return found;
            }

            // Decision 3: rename to BL_<new name> if different and free. A
            // name taken by another family is NOT resolved with a suffix:
            // the bridge's family already has a valid name, and changing it
            // to BL_x_2 on every bake would be worse. It is stated in the
            // Note.
            private string RenameIfFree(Family family, string objectName)
            {
                string wanted = FamilyNaming.BuildName(objectName);
                if (string.Equals(wanted, family.Name, StringComparison.Ordinal)) { return null; }

                if (FamilyNaming.MakeUnique(wanted, FamilyNames(family.Id)) != wanted)
                {
                    return "family " + family.Name + " not renamed to " + wanted + ": name already used";
                }

                family.Name = wanted;
                return null;
            }

            private List<string> FamilyNames(ElementId exclude)
            {
                List<string> names = new List<string>();
                foreach (Element element in new FilteredElementCollector(_project).OfClass(typeof(Family)))
                {
                    if (element.Id == exclude) { continue; }
                    names.Add(element.Name);
                }
                return names;
            }

            private int DeleteDirectShapes(string objectId)
            {
                BakeElements.Sweep sweep = BakeElements.Collect(_project, new[] { objectId }, true, false);
                return BakeElements.DeleteAndCount(_project, sweep);
            }
        }

        // A transaction on a document (project or family) around body. body
        // returns the rejection reason or null. Rejection, exception or
        // unconfirmed commit: the transaction has rolled back. Exceptions
        // bubble up to the caller.
        private static string InTransaction(Document document, string name, Func<string> body)
        {
            using (Transaction transaction = new Transaction(document, name))
            {
                TransactionStatus started = transaction.Start();
                if (started != TransactionStatus.Started)
                {
                    return "transaction '" + name + "' did not open: " + started;
                }

                string refusal;
                try
                {
                    refusal = body();
                }
                catch (Exception)
                {
                    transaction.RollBack();
                    throw;
                }

                if (refusal != null)
                {
                    transaction.RollBack();
                    return refusal;
                }

                TransactionStatus status = transaction.Commit();
                if (status != TransactionStatus.Committed)
                {
                    return "transaction '" + name + "' was not confirmed: " + status;
                }
                return null;
            }
        }

        // Like InTransaction, with the exception turned into a reason: used
        // where the failure needs to be ENRICHED (family already loaded,
        // geometry already reloaded) instead of reported as is.
        private static string TryInTransaction(Document document, string name, Func<string> body)
        {
            try
            {
                return InTransaction(document, name, body);
            }
            catch (Exception ex)
            {
                return Describe(ex);
            }
        }

        private sealed class ObjectWrite
        {
            public string Refusal;
            public bool Created;
            public int Switched;
            public bool NotMoved;
            public string Note;

            public static ObjectWrite Refused(string reason)
            {
                return new ObjectWrite { Refusal = string.IsNullOrEmpty(reason) ? "reason not reported" : reason };
            }

            public static ObjectWrite Done(bool created, int switched, bool notMoved, string note)
            {
                return new ObjectWrite { Created = created, Switched = switched, NotMoved = notMoved, Note = note };
            }
        }

        // The counts of successful objects. With the group, they only end
        // up in BakeResult once assimilated, as in BakeBuilder.
        private sealed class Tally
        {
            public int Written;
            public int Created;
            public int Replaced;
            public int AsOpen;
            public int FacesPlanar;
            public int FacesTriangulated;
            public int SkippedFaces;
            public int Switched;
            public int NotMoved;

            public void Add(bool created, bool asOpen, int facesPlanar, int facesTriangulated,
                int skippedFaces, int switched, bool notMoved)
            {
                Written++;
                if (created) { Created++; } else { Replaced++; }
                if (asOpen) { AsOpen++; }
                FacesPlanar += facesPlanar;
                FacesTriangulated += facesTriangulated;
                SkippedFaces += skippedFaces;
                Switched += switched;
                if (notMoved) { NotMoved++; }
            }

            public void CopyTo(BakeResult result)
            {
                result.CreatedCount = Created;
                result.ReplacedCount = Replaced;
                result.RecreatedCount = 0;
                result.AsMeshCount = AsOpen;
                result.FacesPlanarCount = FacesPlanar;
                result.FacesTriangulatedCount = FacesTriangulated;
                result.SkippedFaceCount = SkippedFaces;
                result.SwitchedCount = Switched;
                result.NotMovedCount = NotMoved;
            }
        }
    }
}
