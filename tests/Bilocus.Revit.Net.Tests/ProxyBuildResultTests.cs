// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The outcome summary is the only thing the user reads after the bridge
    // has written into their document: if it lies or stays silent, a proxy
    // created too many or one skipped silently is only discovered at the next
    // open-and-close.
    public class ProxyBuildResultTests
    {
        private static ProxyEdgeRequest Request(int edges)
        {
            byte[] payload = new byte[edges * ProxyEdgeRequest.BytesPerEdge];
            return ProxyEdgeRequest.Parse("obj-1", "Cube", edges, payload);
        }

        [Fact]
        public void Summary_OnSuccess_ReportsCreatedOverRequested()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 3;
            result.CreatedCount = 3;

            string text = result.BuildSummaryText();

            Assert.Contains("Cube", text);
            Assert.Contains("3 created out of 3 edges", text);
        }

        // Skipped edges must not disappear: a real mesh contains degenerate
        // edges, and "3 out of 5" without explanation looks like a bug.
        [Fact]
        public void Summary_MentionsSkippedDegenerateEdges()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 5;
            result.CreatedCount = 3;
            result.SkippedDegenerateCount = 2;

            Assert.Contains("2 skipped", result.BuildSummaryText());
        }

        [Fact]
        public void Summary_MentionsFailuresAndTheLastReason()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 2;
            result.CreatedCount = 1;
            result.FailedCount = 1;
            result.LastFailureReason = "edge 1: ArgumentException: curve not in plane";

            string text = result.BuildSummaryText();

            Assert.Contains("1 failed", text);
            Assert.Contains("curve not in plane", text);
        }

        // How many elements were removed matters more than how many proxies
        // were replaced: if the two numbers coincide, the previous proxies'
        // SketchPlane stayed in the document.
        [Fact]
        public void Summary_WhenReplacing_ReportsHowManyElementsWentAway()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 3;
            result.CreatedCount = 3;
            result.ReplacedCount = 3;
            result.DeletedElementCount = 6;

            string text = result.BuildSummaryText();

            Assert.Contains("3 replaced", text);
            Assert.Contains("6 elements removed", text);
        }

        // Replacement is the path the user uses the most: they resend the
        // edges many times for every "Remove proxy". If the plane cleanup
        // only works here and nobody writes it down, the fixed defect stays
        // indistinguishable from the unfixed one.
        [Fact]
        public void Summary_WhenReplacing_ReportsWhatHappenedToTheOldPlanes()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 3;
            result.CreatedCount = 3;
            result.ReplacedCount = 3;
            result.DeletedElementCount = 6;
            result.ReplacedSketchPlanes.Measured = true;
            result.ReplacedSketchPlanes.ConsideredCount = 3;
            result.ReplacedSketchPlanes.DeletedCount = 3;

            string text = result.BuildSummaryText();

            Assert.Contains("Replacement.", text);
            Assert.Contains("3 deleted", text);
        }

        // Without replacement there is nothing to clean up, and a line about
        // the replacement's planes would be a line about an operation that
        // did not happen.
        [Fact]
        public void Summary_WithoutReplacement_SaysNothingAboutCleanedPlanes()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 3;
            result.CreatedCount = 3;

            Assert.DoesNotContain("Replacement.", result.BuildSummaryText());
        }

        [Fact]
        public void Summary_OnFailure_SaysWhyAndNothingElse()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = false;
            result.FailureReason = "the document is currently read-only";

            string text = result.BuildSummaryText();

            Assert.Contains("NOT created", text);
            Assert.Contains("read-only", text);
            Assert.DoesNotContain("created out of", text);
        }

        [Fact]
        public void Failed_CarriesTheRequestIdentityIntoTheResult()
        {
            ProxyBuildResult result = ProxyBuildResult.Failed(Request(2), "document missing");

            Assert.False(result.Succeeded);
            Assert.Equal("obj-1", result.ObjectId);
            Assert.Equal("Cube", result.Name);
            Assert.Equal(2, result.RequestedCount);
            Assert.Equal("document missing", result.FailureReason);
        }

        [Fact]
        public void Failed_ToleratesANullRequest()
        {
            ProxyBuildResult result = ProxyBuildResult.Failed(null, "no active document");

            Assert.False(result.Succeeded);
            Assert.Equal(0, result.RequestedCount);
            Assert.Contains("no active document", result.BuildSummaryText());
        }

        // The expected number is 2: one ModelCurve and one SketchPlane per
        // edge. This is the measure to compare against the
        // document's element count.
        [Fact]
        public void ElementsPerEdge_IsTwoWhenEveryCurveHasItsOwnPlane()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.CreatedCount = 4;
            result.SketchPlaneCount = 4;
            result.SketchPlanesMeasured = true;

            Assert.Equal(2.0, result.ElementsPerEdge, 6);
        }

        // If Revit reuses the planes, the cost per edge drops below 2: this is
        // exactly what the measure must be able to distinguish.
        [Fact]
        public void ElementsPerEdge_DropsWhenRevitReusesPlanes()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.CreatedCount = 4;
            result.SketchPlaneCount = 1;
            result.ReusedSketchPlaneCount = 3;
            result.SketchPlanesMeasured = true;

            Assert.Equal(1.25, result.ElementsPerEdge, 6);
        }

        // Not measured is not zero: saying "0 planes" where the read did not
        // succeed would lead to concluding none were left.
        [Fact]
        public void ElementsPerEdge_IsZeroWhenPlanesWereNotMeasured()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.CreatedCount = 4;
            result.SketchPlanesMeasured = false;

            Assert.Equal(0.0, result.ElementsPerEdge, 6);
        }

        [Fact]
        public void Summary_SaysWhenPlanesWereNotMeasured()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 1;
            result.CreatedCount = 1;
            result.SketchPlanesMeasured = false;

            Assert.Contains("not measured", result.BuildSummaryText());
        }

        [Fact]
        public void Summary_ReportsThePlaneCountsWhenMeasured()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Cube";
            result.Succeeded = true;
            result.RequestedCount = 2;
            result.CreatedCount = 2;
            result.SketchPlaneCount = 2;
            result.SketchPlanesMeasured = true;

            string text = result.BuildSummaryText();

            Assert.Contains("2 distinct", text);
            Assert.Contains("2.00 elements per edge", text);
        }

        [Fact]
        public void Summary_WithArcs_MentionsThem()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.Name = "Curve";
            result.Succeeded = true;
            result.RequestedCount = 5;
            result.CreatedCount = 5;
            result.CreatedArcCount = 3;

            Assert.Contains("5 created (3 arcs) out of 5 segments", result.BuildSummaryText());
        }
    }
}
