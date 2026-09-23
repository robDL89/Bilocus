// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // "Remove proxy" is the bridge's only wide-range destructive action. The
    // count in the confirmation and the final summary are the only two things
    // the user sees: if the first stays silent, they delete blindly; if the
    // second rounds off, they do not notice what is left in the file.
    public class ProxyRemovalResultTests
    {
        // The count in the confirmation is the reason the confirmation exists:
        // 4000 where they expected 40 is how the user notices they are about
        // to do the wrong thing.
        [Fact]
        public void Confirmation_StatesHowManyElementsAreAboutToGo()
        {
            string text = ProxyRemovalResult.BuildConfirmationText(4000);

            Assert.Contains("4000", text);
        }

        // With a single element "1 elements" is the kind of sloppiness that
        // makes you doubt the rest of the dialog, right when it is asking for
        // trust to delete something.
        [Fact]
        public void Confirmation_UsesSingularForOneElement()
        {
            string text = ProxyRemovalResult.BuildConfirmationText(1);

            Assert.Contains("1 element ", text);
            Assert.DoesNotContain("1 elements", text);
        }

        // Whoever reads "remove proxy" does not know on their own whether it
        // also takes away hand-drawn lines. The dialog must say so.
        [Fact]
        public void Confirmation_SaysHandDrawnLinesAreNotTouched()
        {
            string text = ProxyRemovalResult.BuildConfirmationText(12);

            Assert.Contains("mark", text);
            Assert.Contains("Hand-drawn", text);
        }

        [Fact]
        public void Empty_SaysThereIsNothingToRemove()
        {
            Assert.Contains("No Bilocus proxies", ProxyRemovalResult.BuildEmptyText());
        }

        // Two distinct numbers on purpose: those found with the mark and
        // those Revit declares it deleted in total. If the second equals the
        // first, Delete did not take away any dependency, which is exactly
        // what the removal needs to measure.
        [Fact]
        public void Summary_ReportsFoundAndDeletedSeparately()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.Succeeded = true;
            result.FoundCount = 24;
            result.DeletedIdCount = 24;

            string text = result.BuildSummaryText();

            Assert.Contains("Removed 24 proxies", text);
            Assert.Contains("24 elements in total", text);
        }

        // If the loss of planes returned - a cleanup that does not pass, a
        // Revit that behaves differently - it must be said plainly and not
        // left to be inferred from two counts.
        [Fact]
        public void Summary_WarnsLoudlyWhenSketchPlanesSurvive()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.Succeeded = true;
            result.FoundCount = 24;
            result.DeletedIdCount = 24;
            result.SketchPlanes.Measured = true;
            result.SketchPlanes.ConsideredCount = 24;
            result.SketchPlanes.DeletedCount = 0;
            result.SketchPlanes.SurvivingCount = 24;
            result.SketchPlanes.SurvivingChecked = true;

            string text = result.BuildSummaryText();

            Assert.Contains("WARNING", text);
            Assert.Contains("24 sketch planes remain", text);
        }

        // The normal case after the fix: the curves go and their planes go
        // with them. It is also the count that shows the cleanup worked,
        // instead of just not complaining.
        [Fact]
        public void Summary_SaysWhenNoSketchPlaneWasLeftBehind()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.Succeeded = true;
            result.FoundCount = 24;
            result.DeletedIdCount = 48;
            result.SketchPlanes.Measured = true;
            result.SketchPlanes.ConsideredCount = 24;
            result.SketchPlanes.DeletedCount = 24;
            result.SketchPlanes.SurvivingCount = 0;
            result.SketchPlanes.SurvivingChecked = true;

            string text = result.BuildSummaryText();

            Assert.Contains("24 deleted", text);
            Assert.Contains("No sketch plane left orphaned", text);
            Assert.DoesNotContain("WARNING", text);
        }

        // A plane kept on purpose survives rightfully and is NOT a loss:
        // mistaking it for one would teach the user to ignore the warning
        // exactly when it matters.
        [Fact]
        public void Summary_DoesNotCallAKeptPlaneALeak()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.Succeeded = true;
            result.FoundCount = 24;
            result.DeletedIdCount = 47;
            result.SketchPlanes.Measured = true;
            result.SketchPlanes.ConsideredCount = 24;
            result.SketchPlanes.DeletedCount = 23;
            result.SketchPlanes.KeptCount = 1;
            result.SketchPlanes.SurvivingCount = 1;
            result.SketchPlanes.SurvivingChecked = true;

            string text = result.BuildSummaryText();

            Assert.Contains("1 left because something else still uses them", text);
            Assert.DoesNotContain("WARNING", text);
        }

        // Measured zero and not measured are two different things: the second
        // must not be readable as "all good".
        [Fact]
        public void Summary_DistinguishesUnmeasuredFromZero()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.Succeeded = true;
            result.FoundCount = 24;
            result.DeletedIdCount = 24;
            result.SketchPlanes.Measured = false;

            string text = result.BuildSummaryText();

            Assert.Contains("not measured", text);
            Assert.DoesNotContain("orphaned", text);
        }

        // A no to the confirmation is not an error, and it is important that
        // the summary says so: the user must know their proxies are all still
        // there.
        [Fact]
        public void Summary_OnCancel_SaysTheProxiesAreStillThere()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.FoundCount = 24;
            result.Cancelled = true;

            string text = result.BuildSummaryText();

            Assert.Contains("cancelled", text);
            Assert.Contains("24 proxies remain", text);
        }

        [Fact]
        public void Summary_OnFailure_ReportsTheReason()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.FoundCount = 24;
            result.Succeeded = false;
            result.FailureReason = "the document is currently read-only";

            string text = result.BuildSummaryText();

            Assert.Contains("NOT removed", text);
            Assert.Contains("read-only", text);
        }

        // A failure must not show plane counts: they would be numbers about a
        // removal that did not happen.
        [Fact]
        public void Summary_OnFailure_DoesNotReportSketchPlaneCounts()
        {
            ProxyRemovalResult result = new ProxyRemovalResult();
            result.FoundCount = 24;
            result.Succeeded = false;
            result.FailureReason = "ModificationForbiddenException: document in failure mode";
            result.SketchPlanes.Measured = true;
            result.SketchPlanes.ConsideredCount = 24;

            Assert.DoesNotContain("Sketch planes", result.BuildSummaryText());
        }
    }
}
