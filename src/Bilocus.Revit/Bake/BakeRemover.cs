// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Bake
{
    // "Remove bake": what the bridge has written for the given Blender
    // objects goes away, in a single transaction. Since Phase B2 that means
    // DirectShapes, families marked with all their instances, and marked
    // instances left without a marked family (target "all").
    //
    // Proxy curves carry the same mark with the same field, and "Remove
    // bake" must not take them away: the lookup is by class (see
    // BakeElements).
    //
    // It is the half that makes the bake acceptable, like RemoveProxyCommand
    // for proxies: the bridge leaves real elements in a project file, and if
    // it knows how to write them it must know how to take them away without
    // the user hunting for them by hand.
    //
    // HERE COPIES ARE REMOVED TOO, unlike the bake, which stops when there is
    // more than one element for the same obj_id. The bake must choose WHICH
    // element to update and does not know how; the removal is the explicit
    // request to take away everything that comes from that object, and a
    // copy made in Revit comes from there just as much as the original.
    //
    // No confirmation and no TaskDialog here: the confirmation is asked by
    // Blender before sending bake_remove, and this code runs on an
    // ExternalEvent triggered by the network.
    public static class BakeRemover
    {
        // Must read identical in Revit's undo history, next to
        // BakeBuilder.GroupName.
        public const string TransactionName = "Bilocus: remove bake";

        public static BakeResult Remove(Document document, IList<string> objectIds)
        {
            if (objectIds == null) { throw new ArgumentNullException("objectIds"); }

            long start = Stopwatch.GetTimestamp();

            // The same check as the bake and the proxies, from the same
            // method: a new reason for the document not being writable must
            // hold for every direction of writing.
            string refusal = ProxyBuilder.DescribeWhyNotWritable(document);
            if (refusal != null)
            {
                return Finish(BakeResult.FailedRemoval(objectIds, refusal), start);
            }

            BakeResult result = new BakeResult(BakeResult.ActionRemove, BakeTarget.All);
            result.RequestedCount = objectIds.Count;

            try
            {
                // Phase B2, decision 9: both modes. An object switched from
                // DirectShape to family and back must not leave anything
                // behind after "Remove bake".
                BakeElements.Sweep sweep = BakeElements.Collect(document, objectIds, true, true);

                if (sweep.IsEmpty)
                {
                    // Nothing to remove: succeeded with zero removed, and no
                    // transaction. An empty transaction would still leave an
                    // entry in the undo history for an operation that
                    // touched nothing (same choice as RemoveProxyCommand).
                    result.Succeeded = true;
                    return Finish(result, start);
                }

                using (Transaction transaction = new Transaction(document, TransactionName))
                {
                    TransactionStatus started = transaction.Start();
                    if (started != TransactionStatus.Started)
                    {
                        result.Succeeded = false;
                        result.FailureReason = "the removal transaction did not open: " + started;
                        return Finish(result, start);
                    }

                    int removed;
                    try
                    {
                        // Delete also returns dependent elements (tags,
                        // hosted dimensions, FamilySymbol): removed counts
                        // DirectShapes, families and instances, which are
                        // what the user asked to remove, including the
                        // instances taken away with the family (see
                        // BakeElements.Sweep).
                        removed = BakeElements.DeleteAndCount(document, sweep);
                    }
                    catch (Exception)
                    {
                        // All or nothing: a half-done removal would leave
                        // the user wondering which objects are still in the
                        // model.
                        transaction.RollBack();
                        throw;
                    }

                    TransactionStatus status = transaction.Commit();
                    if (status == TransactionStatus.Committed)
                    {
                        result.Succeeded = true;
                        result.RemovedCount = removed;
                    }
                    else
                    {
                        result.Succeeded = false;
                        result.FailureReason = "the transaction was not confirmed: " + status;
                    }
                }
            }
            catch (Exception ex)
            {
                // GetOrCreate raises on a schema GUID collision, Delete on a
                // non-deletable element: with an unreadable mark or a
                // rejected deletion, stopping and saying so is the only
                // acceptable answer.
                result.Succeeded = false;
                result.FailureReason = ex.GetType().Name + ": " + ex.Message;
            }

            return Finish(result, start);
        }

        private static BakeResult Finish(BakeResult result, long start)
        {
            result.ElapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            return result;
        }
    }
}
