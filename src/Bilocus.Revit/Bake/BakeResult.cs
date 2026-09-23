// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Bilocus.Revit.Bake
{
    // Outcome of a bake or of a bake removal: the counts that come back to
    // Blender with bake_result and the readable summary of the Status
    // button.
    //
    // No reference to the Revit API, like ProxyBuildResult and for the same
    // reason: it is the part of BakeBuilder and BakeRemover that can be
    // checked without opening Revit. Public and mutable fields because the
    // builder fills them in as the transactions proceed. Action no: it
    // travels on the wire and the Blender side only accepts "bake" or
    // "remove", so it is fixed at construction and cannot be wrong
    // afterwards.
    public sealed class BakeResult
    {
        public const string ActionBake = "bake";
        public const string ActionRemove = "remove";

        public string Action { get; private set; }

        // Phase B2: BakeTarget.DirectShape or BakeTarget.Family for a bake,
        // BakeTarget.All for a removal. Travels on the wire like action, and
        // is fixed at construction like action.
        public string Target { get; private set; }

        // The write to the document succeeded: group assimilated, or removal
        // transaction confirmed. Objects that failed one by one inside a
        // successful bake do NOT make this false: they end up in
        // FailedCount. Document not writable, or no object succeeded: false.
        public bool Succeeded;

        // bake: obj_id announced in bake_begin, so
        // created + replaced + recreated + failed + missing.
        // remove: obj_id requested.
        public int RequestedCount;

        // New DirectShapes: no element with that mark in the document.
        public int CreatedCount;

        // Same category: SetShape on the existing element, same ElementId,
        // the parameters filled in by hand stay.
        public int ReplacedCount;

        // Category changed: the category of an element cannot be changed, so
        // it is deleted and recreated. Kept separate from ReplacedCount
        // because the parameters filled in by hand on the old element are
        // lost, and the user must know it.
        public int RecreatedCount;

        // remove: elements deleted, copies included.
        public int RemovedCount;

        // Objects failed one by one, with the rest of the group continuing.
        public int FailedCount;

        // Announced in bake_begin and never arrived as a valid bake_mesh.
        public int MissingCount;

        // Objects that came out of the builder as a mesh and not as a solid:
        // open or non-manifold mesh.
        public int AsMeshCount;

        // Polygons kept whole and polygons folded back onto Blender's
        // triangles, summed over the whole batch (BakeFaceSet).
        public int FacesPlanarCount;
        public int FacesTriangulatedCount;

        // Faces that did not pass DoesFaceHaveEnoughLoopsAndVertices and were
        // skipped. Does not travel on bake_result, whose fields are fixed by
        // the contract: it stays in the Status text, which is where you look
        // when an object arrives in Revit with a hole.
        public int SkippedFaceCount;

        // Elements of the OTHER mode removed by the
        // mode switch. In the family bake, the DirectShapes of the same
        // object; in the DirectShape bake, instances and families. Travels
        // on bake_result as switched.
        public int SwitchedCount;

        // Objects whose family re-bake updated the
        // geometry but did NOT move the instance, because there was more
        // than one marked instance (copies made in Revit). Travels as
        // not_moved.
        public int NotMovedCount;

        // The why when the whole operation fails: document not writable,
        // group rejected, no object written. Concerns Revit or the document,
        // not an object.
        public string FailureReason = "";

        // The last failed object and its reason, filled by AddFailure.
        //
        // Kept separate from FailureReason on purpose: a group that fails
        // AFTER a failed object must not attribute its reason to that object
        // in the message the user reads.
        public string LastFailureName = "";
        public string LastFailureReason = "";

        // A note for whoever reads the Status, outside the wire contract:
        // today the family bake run without a TransactionGroup (more undo
        // entries), or a family document that Revit did not close. Does not
        // travel on bake_result, whose fields are fixed; without this line
        // the user would find out about the broken Ctrl+Z only by pressing
        // it.
        public string Note = "";

        public double ElapsedMs;

        // The Phase B constructor: the target follows the action, DirectShape
        // for a bake and All for a removal.
        public BakeResult(string action)
            : this(action, action == ActionRemove ? BakeTarget.All : BakeTarget.DirectShape)
        {
        }

        // Only the contract pairs: bake with directshape or family, remove
        // with all. Anything else is ArgumentException, because the Blender
        // side reads both fields strictly.
        public BakeResult(string action, string target)
        {
            if (action != ActionBake && action != ActionRemove)
            {
                throw new ArgumentException(
                    "action must be \"bake\" or \"remove\", not \"" + action + "\"");
            }

            bool valid = action == ActionRemove
                ? target == BakeTarget.All
                : target == BakeTarget.DirectShape || target == BakeTarget.Family;
            if (!valid)
            {
                throw new ArgumentException(string.Format(
                    "target \"{0}\" not allowed for action \"{1}\"", target, action));
            }

            Action = action;
            Target = target;
        }

        private bool IsFamily
        {
            get { return Target == BakeTarget.Family; }
        }

        private string BakeTitle
        {
            get { return IsFamily ? "Family bake" : "DirectShape bake"; }
        }

        // A failed object: counted, and its name and reason kept.
        public void AddFailure(string name, string reason)
        {
            FailedCount++;
            LastFailureName = name == null ? "" : name;
            LastFailureReason = string.IsNullOrEmpty(reason) ? UnknownReason : reason;
        }

        private const string UnknownReason = "reason not reported";

        // The message field of bake_result, and the line of the error frame
        // that goes with it when the operation fails.
        //
        // Empty if everything went well. Otherwise the reason for the
        // operation, if it failed, followed by the last failed object with
        // its name, if there is one. "No object written" alone does not say
        // WHY; the last failed one does.
        public string BuildMessage()
        {
            string operation = !Succeeded && !string.IsNullOrEmpty(FailureReason) ? FailureReason : "";
            string lastObject = FailedCount > 0 ? DescribeLastFailure() : "";

            if (operation.Length > 0 && lastObject.Length > 0)
            {
                return operation + " - last failed " + lastObject;
            }
            if (operation.Length > 0) { return operation; }
            if (lastObject.Length > 0) { return lastObject; }

            // Failed with no reason recorded: it is a defect of whoever
            // filled in the result, but a false ok with an empty message
            // would leave the user with nothing to read.
            return Succeeded ? "" : UnknownReason;
        }

        private string DescribeLastFailure()
        {
            string reason = string.IsNullOrEmpty(LastFailureReason) ? UnknownReason : LastFailureReason;
            if (string.IsNullOrEmpty(LastFailureName)) { return reason; }
            return "'" + LastFailureName + "': " + reason;
        }

        // Multi-line text for the Status button. Does not go on the wire: the
        // Blender side gets the counts, and the panel formats its own line.
        public string BuildSummaryText()
        {
            string text = Action == ActionRemove ? BuildRemovalText() : BuildBakeText();

            if (!string.IsNullOrEmpty(Note))
            {
                text = text + "\nNote: " + Note;
            }

            return text + string.Format(CultureInfo.InvariantCulture, "\nTime: {0:F0} ms", ElapsedMs);
        }

        private string BuildBakeText()
        {
            if (!Succeeded)
            {
                string failed = BakeTitle + " NOT performed: " + BuildMessage();

                if (FailedCount > 0)
                {
                    failed = failed + string.Format(CultureInfo.InvariantCulture,
                        "\nFailed: {0} out of {1} objects", FailedCount, RequestedCount);
                }
                if (MissingCount > 0)
                {
                    failed = failed + string.Format(CultureInfo.InvariantCulture,
                        "\nAnnounced but never arrived: {0}", MissingCount);
                }
                return failed;
            }

            // In the family bake, families are counted, and "recreated" is
            // always zero: a family's category is changed in place.
            string text = IsFamily
                ? string.Format(CultureInfo.InvariantCulture,
                    "Family bake: {0} objects requested - families {1} created, {2} updated",
                    RequestedCount, CreatedCount, ReplacedCount)
                : string.Format(CultureInfo.InvariantCulture,
                    "DirectShape bake: {0} objects requested - {1} created, {2} updated, {3} recreated",
                    RequestedCount, CreatedCount, ReplacedCount, RecreatedCount);

            text = text + string.Format(CultureInfo.InvariantCulture,
                "\nFaces: {0} whole, {1} triangulated", FacesPlanarCount, FacesTriangulatedCount);

            if (SkippedFaceCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    ", {0} skipped as rejected by Revit", SkippedFaceCount);
            }

            // In the family bake a non-manifold mesh fails: the "as_mesh"
            // ones are the open shells accepted via the checkbox.
            if (AsMeshCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    IsFamily
                        ? "\nAccepted as an open shell: {0}"
                        : "\nCame out as a mesh instead of a solid: {0}",
                    AsMeshCount);
            }

            if (SwitchedCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    "\nElements replaced from the other mode: {0}", SwitchedCount);
            }

            if (NotMovedCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    "\nInstances not moved (copies made in Revit): {0}", NotMovedCount);
            }

            if (MissingCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    "\nAnnounced but never arrived: {0}", MissingCount);
            }

            if (FailedCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    "\nFailed: {0} - last {1}", FailedCount, DescribeLastFailure());
            }

            return text;
        }

        private string BuildRemovalText()
        {
            if (!Succeeded)
            {
                return "Bake removal NOT performed: " + BuildMessage();
            }

            return string.Format(CultureInfo.InvariantCulture,
                "Bake removal: removed {0} elements from {1} objects", RemovedCount, RequestedCount);
        }

        // A bake that failed before writing anything: document not writable,
        // unexpected exception. The missing ones are still counted, they are
        // a fact of the batch and not of the write.
        // The target comes from the batch; without a batch it is DirectShape,
        // like any bake that says nothing else.
        public static BakeResult Failed(BakeBatch batch, string reason)
        {
            BakeResult result = new BakeResult(ActionBake, batch != null ? batch.Target : BakeTarget.DirectShape);
            if (batch != null)
            {
                result.RequestedCount = batch.AnnouncedIds.Count;
                result.MissingCount = batch.MissingIds.Count;
            }
            result.Succeeded = false;
            result.FailureReason = reason == null ? "" : reason;
            return result;
        }

        // A removal that failed before deleting anything.
        public static BakeResult FailedRemoval(IList<string> objectIds, string reason)
        {
            BakeResult result = new BakeResult(ActionRemove);
            if (objectIds != null) { result.RequestedCount = objectIds.Count; }
            result.Succeeded = false;
            result.FailureReason = reason == null ? "" : reason;
            return result;
        }
    }
}
