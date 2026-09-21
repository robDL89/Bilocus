// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bilocus.Revit.Proxy
{
    // "Remove proxy" button: finds every element that carries the bridge's
    // mark and deletes them in a single transaction.
    //
    // It is the mandatory other half of writing. From this phase on the
    // bridge leaves elements in a project file, and if it knows how to write
    // it must know how to undo: without this command, the only way to remove
    // a few thousand lines would be selecting them by hand in the model, i.e.
    // no way at all.
    //
    // CONFIRMATION IS NEEDED HERE, unlike the replacement inside
    // ProxyBuilder. Replacement is the expected consequence of an explicit
    // gesture - you resent the edges of that object, its previous proxies go
    // away - while this command deletes everything the bridge has written,
    // wherever it is, and whoever presses it may not have "everything" in
    // mind. The count in the dialog is its defense: an unexpected number is
    // the best way to notice you are about to do the wrong thing.
    //
    // A SINGLE TRANSACTION, like creation, and for the same reason: a Ctrl+Z
    // must put everything back, not just the last piece.
    [Transaction(TransactionMode.Manual)]
    public sealed class RemoveProxyCommand : IExternalCommand
    {
        // Must read identical in Revit's undo history, next to
        // ProxyBuilder.TransactionName.
        public const string TransactionName = "Bilocus: remove proxy";

        private const string DialogTitle = "Bilocus";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document document = uidoc == null ? null : uidoc.Document;

            // Same check as creation, and called from the same method instead
            // of rewritten: if a fourth reason why the document is not
            // writable is discovered one day, it must apply to both
            // directions of writing. Two copies would diverge on the same day
            // one of them gets fixed.
            string refusal = ProxyBuilder.DescribeWhyNotWritable(document);
            if (refusal != null)
            {
                TaskDialog.Show(DialogTitle, "Cannot remove proxies: " + refusal);
                return Result.Cancelled;
            }

            IList<ElementId> marked;
            try
            {
                marked = ProxySchema.FindMarked(document);
            }
            catch (Exception ex)
            {
                // GetOrCreate throws on a schema GUID collision. With the mark
                // unreadable there is no way to know what belongs to the
                // bridge and what to the user: stopping is the only
                // acceptable response.
                TaskDialog.Show(DialogTitle, string.Format(
                    "Cannot read the proxy mark: {0}: {1}",
                    ex.GetType().Name, ex.Message));
                return Result.Failed;
            }

            if (marked.Count == 0)
            {
                // Nothing to do: no transaction, no confirmation to give.
                // Opening an empty transaction would still leave an entry in
                // the undo history for an operation that touched nothing.
                TaskDialog.Show(DialogTitle, ProxyRemovalResult.BuildEmptyText());
                return Result.Succeeded;
            }

            ProxyRemovalResult result = new ProxyRemovalResult();
            result.FoundCount = marked.Count;

            // No as the default button: whoever dismisses the dialog with
            // Enter or Esc must not delete anything.
            TaskDialogResult answer = TaskDialog.Show(
                DialogTitle,
                ProxyRemovalResult.BuildConfirmationText(marked.Count),
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                TaskDialogResult.No);

            if (answer != TaskDialogResult.Yes)
            {
                result.Cancelled = true;
                TaskDialog.Show(DialogTitle, result.BuildSummaryText());
                return Result.Cancelled;
            }

            Remove(document, marked, result);
            TaskDialog.Show(DialogTitle, result.BuildSummaryText());
            return result.Succeeded ? Result.Succeeded : Result.Failed;
        }

        // The actual removal: the marked curves and, with them, the sketch
        // planes that are left uncovered.
        //
        // Plane cleanup lives in SketchPlaneCleaner and not here, because
        // replacement inside ProxyBuilder deletes proxy curves in exactly the
        // same way and must behave the same way: a second copy would diverge
        // on the same day one of the two gets fixed, and the forgotten branch
        // would be the one used most.
        private static void Remove(
            Document document, IList<ElementId> marked, ProxyRemovalResult result)
        {
            long start = Stopwatch.GetTimestamp();

            // The ids of the planes considered survive the transaction: after
            // the commit they are needed to go and look at what is really
            // left in the document instead of trusting what Delete reported.
            List<ElementId> planes = new List<ElementId>();

            try
            {
                using (Transaction transaction = new Transaction(document, TransactionName))
                {
                    transaction.Start();

                    try
                    {
                        result.DeletedIdCount = SketchPlaneCleaner.DeleteCurvesAndOrphanPlanes(
                            document, marked, result.SketchPlanes, planes);
                    }
                    catch (Exception)
                    {
                        transaction.RollBack();
                        throw;
                    }

                    TransactionStatus status = transaction.Commit();
                    if (status == TransactionStatus.Committed)
                    {
                        result.Succeeded = true;
                    }
                    else
                    {
                        result.Succeeded = false;
                        result.FailureReason =
                            "the transaction was not committed: " + status;
                    }
                }
            }
            catch (Exception ex)
            {
                result.Succeeded = false;
                result.FailureReason = ex.GetType().Name + ": " + ex.Message;
            }

            if (result.Succeeded)
            {
                // After the transaction is closed: before the commit the
                // document state is the one being modified, and an element
                // deleted but not yet confirmed is not an answer to the
                // question.
                SketchPlaneCleaner.CountSurviving(document, planes, result.SketchPlanes);
            }

            result.ElapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        }
    }
}
