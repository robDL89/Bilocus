// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Globalization;

namespace Bilocus.Revit.Proxy
{
    // Outcome of a bulk proxy removal: counts, the outcome of cleaning up the
    // sketch planes, and the texts the user reads, both the confirmation
    // question and the final summary.
    //
    // No reference to the Revit API, like ProxyBuildResult and for the same
    // reason: it is the part of the command that can be verified without
    // opening Revit, and is compiled by the test project as a linked source.
    //
    // HERE THE TEXTS MATTER MORE THAN THE NUMBERS. "Remove proxy" is the
    // bridge's only wide-range destructive action: it deletes everything that
    // carries the mark, wherever it is in the document. The number written in
    // the confirmation is the user's only defense against a removal they did
    // not want - if it says 4000 and they expected 40, that number just saved
    // their file. That is why the confirmation text lives here, where a test
    // verifies it, and not inline inside the command.
    public sealed class ProxyRemovalResult
    {
        // Marked elements found in the document before deleting.
        public int FoundCount;

        // How many ids Document.Delete returned in total, curves and sketch
        // planes together. Can be greater than FoundCount: the marked curves
        // are joined by the planes left uncovered that SketchPlaneCleaner took
        // away. Seeing the two numbers side by side is the only way to know
        // whether the cleanup did anything.
        public int DeletedIdCount;

        // The counts and texts about sketch planes, shared with the
        // replacement inside ProxyBuilder: proxy curves are deleted from two
        // paths, and the planes must be counted and reported the same way in
        // both.
        public readonly SketchPlaneCleanupResult SketchPlanes = new SketchPlaneCleanupResult();

        // The user answered no to the confirmation. Not a failure: it is
        // exactly what the confirmation exists for.
        public bool Cancelled;

        public bool Succeeded;

        public string FailureReason = "";

        public double ElapsedMs;

        // The document contains no Bilocus proxies. Said and done, without
        // opening transactions and without asking to confirm anything.
        public static string BuildEmptyText()
        {
            return "No Bilocus proxies in this document: nothing to remove.";
        }

        // The confirmation question.
        //
        // The count goes in the first line, not at the bottom: it is the
        // reason the dialog exists. The rest explains what survives the
        // removal, because "remove proxy" alone does not say whether it also
        // takes away hand-drawn lines.
        public static string BuildConfirmationText(int count)
        {
            string subject = count == 1
                ? "1 element created by Bilocus"
                : string.Format(CultureInfo.InvariantCulture,
                    "{0} elements created by Bilocus", count);

            return "Remove " + subject + " from this document?"
                + "\n\nOnly elements carrying the bridge's mark are deleted."
                + "\nHand-drawn lines are not touched."
                + "\n\nThis operation can be undone with Ctrl+Z.";
        }

        public string BuildSummaryText()
        {
            if (Cancelled)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "Removal cancelled: the {0} proxies remain in the document.", FoundCount);
            }

            if (!Succeeded)
            {
                return "Proxies NOT removed: " + FailureReason;
            }

            string text = string.Format(CultureInfo.InvariantCulture,
                "Removed {0} proxies. Revit deleted {1} elements in total.",
                FoundCount, DeletedIdCount);

            string planes = SketchPlanes.BuildReportText();
            if (planes.Length > 0) { text = text + "\n" + planes; }

            text = text + string.Format(CultureInfo.InvariantCulture,
                "\nTime: {0:F0} ms", ElapsedMs);

            return text;
        }
    }
}
