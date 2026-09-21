// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Globalization;

namespace Bilocus.Revit.Proxy
{
    // Outcome of cleaning up the sketch planes left behind when proxy curves
    // are deleted: how many Revit took away on its own, how many we deleted
    // because nobody used them anymore, how many we left because something
    // still rested on them.
    //
    // ONE SINGLE PLACE FOR COUNTS AND TEXTS. Proxy curves are deleted from TWO
    // paths - explicit removal in RemoveProxyCommand and replacement inside
    // ProxyBuilder - and before, each of the two counted the planes on its
    // own. Two copies of the count would diverge on the same day one gets
    // fixed, and it already happened: the measurement of orphan planes only
    // existed in removal, while the path used most is replacement.
    //
    // No reference to the Revit API, like ProxyRemovalResult and
    // ProxyBuildResult that contain it: it is the part of the cleanup that
    // is verified without opening Revit, and it is compiled by the test
    // project as a linked source.
    public sealed class SketchPlaneCleanupResult
    {
        // DISTINCT sketch planes read on the curves BEFORE deleting them. Read
        // first because afterwards the curve is gone and with it the only way
        // back to the plane that supported it.
        public int ConsideredCount;

        // How many of those planes were already gone right after the curves
        // were deleted, without us touching them. Field measurement says
        // zero: Revit does not take the planes away, because the dependency
        // runs the other way - it is the curve that depends on the plane, not
        // the other way around. The counter stays because one day it could
        // change, and in that case we want to see it instead of inferring it.
        public int VanishedWithCurvesCount;

        // How many we deleted ourselves, after verifying they no longer had
        // any dependent element.
        public int DeletedCount;

        // How many we LEFT because something else still rests on them. Not a
        // failure: it is exactly the reason deletion is conditional instead of
        // assumed. Revit can reuse a plane that already exists and carries the
        // user's own sketches, and taking it away would be far worse than
        // leaving an empty one behind.
        public int KeptCount;

        // False if the cleanup failed to measure: the counts above must be
        // read as "not measured", not as zero. A cleanup that does not
        // succeed must NOT make a successful deletion fail - the curves go
        // away regardless, and in the worst case what is left is the same
        // clutter that existed before this fix.
        public bool Measured;

        public string FailureReason = "";

        // How many of the planes considered STILL exist in the document after
        // the commit. Only filled in if someone asks for the verification:
        // looking at the real document is the only way to know what is inside
        // instead of trusting what Delete returned.
        public int SurvivingCount;
        public bool SurvivingChecked;

        // The planes left behind that we had no reason to leave. The ones kept
        // on purpose survive rightfully and are not a loss.
        public int OrphansLeftCount
        {
            get
            {
                if (!SurvivingChecked) { return 0; }
                int left = SurvivingCount - KeptCount;
                return left > 0 ? left : 0;
            }
        }

        // The lines the user reads. Empty string when there is nothing to
        // say, so whoever concatenates it does not need to know.
        public string BuildReportText()
        {
            if (!Measured)
            {
                return ConsideredCount == 0 && FailureReason.Length == 0
                    ? "Sketch planes: not measured."
                    : "Sketch planes: not measured (" + FailureReason + ").";
            }

            if (ConsideredCount == 0) { return ""; }

            string text = string.Format(CultureInfo.InvariantCulture,
                "Sketch planes: {0} distinct, {1} deleted because left without curves",
                ConsideredCount, DeletedCount);

            if (VanishedWithCurvesCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    ", {0} taken away by Revit with the curves", VanishedWithCurvesCount);
            }

            if (KeptCount > 0)
            {
                text = text + string.Format(CultureInfo.InvariantCulture,
                    ", {0} left because something else still uses them", KeptCount);
            }

            text = text + ".";

            if (!SurvivingChecked) { return text; }

            if (OrphansLeftCount > 0)
            {
                // Said plainly, not left to be inferred from two counts: this
                // is the loss this cleanup was meant to close, and if it comes
                // back it must be seen right away.
                return text + string.Format(CultureInfo.InvariantCulture,
                    "\nWARNING: {0} sketch planes remain in the document without curves on them."
                    + " Every create-remove cycle leaves more behind.",
                    OrphansLeftCount);
            }

            return text + "\nNo sketch plane left orphaned.";
        }
    }
}
