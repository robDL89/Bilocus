// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The pure half of sketch plane cleanup, shared by the two paths that
    // delete proxy curves: explicit removal and replacement inside
    // ProxyBuilder.
    //
    // Why it is worth verifying these texts: the loss this cleanup closes was
    // INVISIBLE. Sixty sketch planes left in a project file do not show up in
    // any view and do not appear in any schedule; the only place they can be
    // noticed is this line of counts. If it lies, the defect goes back to
    // being invisible even after being fixed.
    public class SketchPlaneCleanupResultTests
    {
        private static SketchPlaneCleanupResult Cleaned(int considered, int deleted)
        {
            SketchPlaneCleanupResult planes = new SketchPlaneCleanupResult();
            planes.Measured = true;
            planes.ConsideredCount = considered;
            planes.DeletedCount = deleted;
            return planes;
        }

        [Fact]
        public void Report_SaysHowManyPlanesWereConsideredAndDeleted()
        {
            string text = Cleaned(60, 60).BuildReportText();

            Assert.Contains("60 distinct", text);
            Assert.Contains("60 deleted", text);
        }

        // No plane to look at is not news: the line disappears instead of
        // saying "0 distinct, 0 deleted", which would take up space in the
        // dialog without adding anything.
        [Fact]
        public void Report_IsSilentWhenThereWasNothingToClean()
        {
            Assert.Equal("", Cleaned(0, 0).BuildReportText());
        }

        // A plane left on purpose must be SAID. It is the difference between
        // "the cleanup did not work" and "that plane also supports the user's
        // sketches, and taking it away would have been far worse".
        [Fact]
        public void Report_SaysWhenAPlaneWasKeptBecauseSomethingElseUsesIt()
        {
            SketchPlaneCleanupResult planes = Cleaned(10, 7);
            planes.KeptCount = 3;

            string text = planes.BuildReportText();

            Assert.Contains("3 left because something else still uses them", text);
            Assert.DoesNotContain("WARNING", text);
        }

        // The counter exists for a case that does not happen today: if Revit
        // ever started taking planes away on its own, it must be seen, not
        // inferred from a difference between two numbers.
        [Fact]
        public void Report_SaysWhenRevitTookThePlanesAway()
        {
            SketchPlaneCleanupResult planes = Cleaned(10, 0);
            planes.VanishedWithCurvesCount = 10;

            Assert.Contains("10 taken away by Revit", planes.BuildReportText());
        }

        // The verification after the commit is the one that matters: it looks
        // at the real document instead of trusting what Delete declared.
        [Fact]
        public void Report_ConfirmsNothingWasLeftBehindOnlyAfterLooking()
        {
            SketchPlaneCleanupResult planes = Cleaned(10, 10);

            Assert.DoesNotContain("orphaned", planes.BuildReportText());

            planes.SurvivingChecked = true;
            planes.SurvivingCount = 0;

            Assert.Contains("No sketch plane left orphaned", planes.BuildReportText());
        }

        // The defect's return, said plainly.
        [Fact]
        public void Report_WarnsAboutPlanesThatSurvivedWithoutAReason()
        {
            SketchPlaneCleanupResult planes = Cleaned(60, 0);
            planes.SurvivingChecked = true;
            planes.SurvivingCount = 60;

            string text = planes.BuildReportText();

            Assert.Contains("WARNING", text);
            Assert.Contains("60 sketch planes remain", text);
        }

        // Planes kept on purpose survive rightfully: counting them as a loss
        // would teach the user to ignore the warning exactly when it matters.
        [Fact]
        public void OrphansLeft_DoesNotCountThePlanesKeptOnPurpose()
        {
            SketchPlaneCleanupResult planes = Cleaned(10, 7);
            planes.KeptCount = 3;
            planes.SurvivingChecked = true;
            planes.SurvivingCount = 3;

            Assert.Equal(0, planes.OrphansLeftCount);
            Assert.DoesNotContain("WARNING", planes.BuildReportText());
        }

        // If more survive than the ones kept on purpose, the difference is a
        // loss and must be counted in full.
        [Fact]
        public void OrphansLeft_CountsWhatSurvivedBeyondTheKeptOnes()
        {
            SketchPlaneCleanupResult planes = Cleaned(10, 5);
            planes.KeptCount = 3;
            planes.SurvivingChecked = true;
            planes.SurvivingCount = 5;

            Assert.Equal(2, planes.OrphansLeftCount);
        }

        // Without a check after the commit, nothing is known, and "nothing"
        // must not be readable as "zero losses".
        [Fact]
        public void OrphansLeft_IsZeroWhileNobodyHasLooked()
        {
            SketchPlaneCleanupResult planes = Cleaned(10, 0);

            Assert.Equal(0, planes.OrphansLeftCount);
            Assert.DoesNotContain("WARNING", planes.BuildReportText());
        }

        // Not measured is not zero: saying "0 planes" where the read did not
        // succeed would lead to concluding none were left.
        [Fact]
        public void Report_DistinguishesUnmeasuredFromClean()
        {
            SketchPlaneCleanupResult planes = new SketchPlaneCleanupResult();

            string text = planes.BuildReportText();

            Assert.Contains("not measured", text);
            Assert.DoesNotContain("orphaned", text);
        }

        // When the cleanup is skipped for a specific reason, that reason is
        // the only thing that lets you understand why without opening Revit.
        [Fact]
        public void Report_CarriesTheReasonThePlanesWereNotMeasured()
        {
            SketchPlaneCleanupResult planes = new SketchPlaneCleanupResult();
            planes.FailureReason = "InvalidOperationException: element no longer valid";

            string text = planes.BuildReportText();

            Assert.Contains("not measured", text);
            Assert.Contains("element no longer valid", text);
        }
    }
}
