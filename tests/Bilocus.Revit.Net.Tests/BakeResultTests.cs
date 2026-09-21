// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The outcome of a bake or a removal: the counts that come back to
    // Blender and the text of the Status button. As with proxies, it is the
    // only thing the user reads after the bridge has written into its
    // document.
    public class BakeResultTests
    {
        [Theory]
        [InlineData("bake")]
        [InlineData("remove")]
        public void Constructor_AcceptsTheTwoActions(string action)
        {
            Assert.Equal(action, new BakeResult(action).Action);
        }

        // action goes on the wire and the Blender side reads it strictly: a
        // value other than bake/remove must stop here, not in a panel.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Bake")]
        [InlineData("delete")]
        public void Constructor_RejectsAnyOtherAction(string action)
        {
            Assert.Throws<ArgumentException>(delegate { new BakeResult(action); });
        }

        // ---- target (Phase B2) ----

        // The Phase B constructor stays: a bake without a target is a
        // DirectShape bake, a removal takes "all".
        [Fact]
        public void Constructor_WithoutTarget_DefaultsFromTheAction()
        {
            Assert.Equal(BakeTarget.DirectShape, new BakeResult(BakeResult.ActionBake).Target);
            Assert.Equal(BakeTarget.All, new BakeResult(BakeResult.ActionRemove).Target);
        }

        [Theory]
        [InlineData("bake", "directshape")]
        [InlineData("bake", "family")]
        [InlineData("remove", "all")]
        public void Constructor_WithTarget_AcceptsTheContractPairs(string action, string target)
        {
            BakeResult result = new BakeResult(action, target);

            Assert.Equal(action, result.Action);
            Assert.Equal(target, result.Target);
        }

        // target also travels on the wire and the Blender side reads it
        // strictly.
        [Theory]
        [InlineData("bake", "all")]
        [InlineData("bake", null)]
        [InlineData("bake", "Family")]
        [InlineData("remove", "family")]
        [InlineData("remove", "directshape")]
        public void Constructor_WithTarget_RejectsAnyOtherPair(string action, string target)
        {
            Assert.Throws<ArgumentException>(delegate { new BakeResult(action, target); });
        }

        [Fact]
        public void Failed_FromAFamilyBatch_HasTargetFamily()
        {
            BakeBatch batch = new BakeBatch(new List<string> { "a" }, BakeTarget.Family);

            BakeResult result = BakeResult.Failed(batch, "template missing");

            Assert.Equal(BakeTarget.Family, result.Target);
            Assert.Equal(1, result.MissingCount);
        }

        [Fact]
        public void Failed_WithoutABatch_IsDirectShape_AndFailedRemoval_IsAll()
        {
            Assert.Equal(BakeTarget.DirectShape, BakeResult.Failed(null, "x").Target);
            Assert.Equal(BakeTarget.All, BakeResult.FailedRemoval(null, "x").Target);
        }

        [Fact]
        public void Summary_Family_SpeaksOfFamiliesAndOpenShells()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake, BakeTarget.Family);
            result.Succeeded = true;
            result.RequestedCount = 3;
            result.CreatedCount = 1;
            result.ReplacedCount = 2;
            result.AsMeshCount = 1;
            result.FacesPlanarCount = 6;

            string text = result.BuildSummaryText();

            Assert.Contains("Family bake", text);
            Assert.DoesNotContain("DirectShape", text);
            Assert.Contains("1 created", text);
            Assert.Contains("2 updated", text);
            Assert.Contains("open shell: 1", text);
            Assert.DoesNotContain("as a mesh", text);
        }

        [Fact]
        public void Summary_Family_OnFailure_SaysFamily()
        {
            BakeResult result = BakeResult.Failed(
                new BakeBatch(new List<string> { "a" }, BakeTarget.Family), "template missing");

            string text = result.BuildSummaryText();

            Assert.Contains("Family bake NOT performed", text);
            Assert.Contains("template missing", text);
        }

        [Fact]
        public void Summary_MentionsSwitchedAndNotMoved_OnlyWhenNotZero()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake, BakeTarget.Family);
            result.Succeeded = true;
            result.RequestedCount = 2;
            result.CreatedCount = 2;

            string quiet = result.BuildSummaryText();
            Assert.DoesNotContain("other mode", quiet);
            Assert.DoesNotContain("not moved", quiet);

            result.SwitchedCount = 3;
            result.NotMovedCount = 1;

            string text = result.BuildSummaryText();
            Assert.Contains("other mode", text);
            Assert.Contains("3", text);
            Assert.Contains("not moved", text);
        }

        [Fact]
        public void Summary_ShowsTheNote_OnlyWhenPresent_AndNeverInTheMessage()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake, BakeTarget.Family);
            result.Succeeded = true;
            result.RequestedCount = 1;
            result.CreatedCount = 1;

            Assert.DoesNotContain("Note:", result.BuildSummaryText());

            result.Note = "bake without TransactionGroup";

            Assert.Contains("\nNote: bake without TransactionGroup", result.BuildSummaryText());
            Assert.Equal("", result.BuildMessage());

            result.Succeeded = false;
            result.FailureReason = "no object written to the document";
            Assert.Contains("Note: bake without TransactionGroup", result.BuildSummaryText());
            Assert.DoesNotContain("TransactionGroup", result.BuildMessage());
        }

        [Fact]
        public void Summary_DirectShape_MentionsSwitched()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.RequestedCount = 1;
            result.ReplacedCount = 1;
            result.SwitchedCount = 2;

            Assert.Contains("other mode: 2", result.BuildSummaryText());
        }

        [Fact]
        public void NewResult_StartsFromZeroAndEmptyStrings()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);

            Assert.False(result.Succeeded);
            Assert.Equal(0, result.RequestedCount + result.CreatedCount + result.ReplacedCount
                + result.RecreatedCount + result.RemovedCount + result.FailedCount + result.MissingCount
                + result.AsMeshCount + result.FacesPlanarCount + result.FacesTriangulatedCount
                + result.SkippedFaceCount + result.SwitchedCount + result.NotMovedCount);
            Assert.Equal("", result.FailureReason);
            Assert.Equal("", result.LastFailureName);
            Assert.Equal("", result.LastFailureReason);
        }

        [Fact]
        public void AddFailure_CountsAndRemembersTheLastOne()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);

            result.AddFailure("First", "reason one");
            result.AddFailure("Second", "reason two");

            Assert.Equal(2, result.FailedCount);
            Assert.Equal("Second", result.LastFailureName);
            Assert.Equal("reason two", result.LastFailureReason);
        }

        // A failure without a reason is still a failure: the message must
        // not stay empty.
        [Fact]
        public void AddFailure_WithoutAReason_StillSaysSomething()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);

            result.AddFailure(null, null);

            Assert.Equal(1, result.FailedCount);
            Assert.NotEqual("", result.LastFailureReason);
        }

        // ---- message ----

        [Fact]
        public void Message_OnSuccessWithoutFailures_IsEmpty()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.CreatedCount = 3;

            Assert.Equal("", result.BuildMessage());
        }

        // The contract: objects that failed inside a successful bake do not
        // make ok false, and message carries the reason of the LAST failed
        // one with its name.
        [Fact]
        public void Message_OnSuccessWithFailures_NamesTheLastFailedObject()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.CreatedCount = 2;
            result.AddFailure("Stair", "old reason");
            result.AddFailure("Wall", "category OST_Views not allowed for a DirectShape");

            string message = result.BuildMessage();

            Assert.Contains("Wall", message);
            Assert.Contains("OST_Views not allowed", message);
            Assert.DoesNotContain("Stair", message);
        }

        [Fact]
        public void Message_OnBatchFailure_IsTheReason()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = false;
            result.FailureReason = "the document is read-only";

            Assert.Equal("the document is read-only", result.BuildMessage());
        }

        // No object succeeded: the batch reason alone ("no object written")
        // does not say WHY. The last failed one does.
        [Fact]
        public void Message_WhenNoObjectSucceeded_CarriesTheReasonAndTheLastFailure()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = false;
            result.FailureReason = "no object written";
            result.AddFailure("Wall", "geometry rejected by the builder");

            string message = result.BuildMessage();

            Assert.Contains("no object written", message);
            Assert.Contains("Wall", message);
            Assert.Contains("geometry rejected", message);
        }

        // A batch failure that arrives AFTER object failures (for example a
        // rejected Assimilate) stays distinguishable: its reason is not
        // attributed to the last failed object.
        [Fact]
        public void Message_KeepsTheBatchReasonSeparateFromTheObjectFailure()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.AddFailure("Wall", "category not allowed");
            result.Succeeded = false;
            result.FailureReason = "Assimilate rejected";

            string message = result.BuildMessage();

            Assert.StartsWith("Assimilate rejected", message);
            Assert.DoesNotContain("'Wall': Assimilate", message);
        }

        [Fact]
        public void Message_OnFailureWithoutAnyReason_IsNotEmpty()
        {
            BakeResult result = new BakeResult(BakeResult.ActionRemove);
            result.Succeeded = false;

            Assert.NotEqual("", result.BuildMessage());
        }

        // ---- Status text ----

        [Fact]
        public void Summary_Bake_ReportsTheCountsAndTheFaces()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.RequestedCount = 3;
            result.CreatedCount = 1;
            result.ReplacedCount = 1;
            result.RecreatedCount = 1;
            result.FacesPlanarCount = 14;
            result.FacesTriangulatedCount = 2;
            result.ElapsedMs = 120;

            string text = result.BuildSummaryText();

            Assert.Contains("3 objects", text);
            Assert.Contains("1 created", text);
            Assert.Contains("1 updated", text);
            Assert.Contains("1 recreated", text);
            Assert.Contains("14 whole", text);
            Assert.Contains("2 triangulated", text);
            Assert.Contains("\nTime: 120 ms", text);
        }

        [Fact]
        public void Summary_Bake_WithoutExtras_SaysNothingAboutThem()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.RequestedCount = 1;
            result.CreatedCount = 1;

            string text = result.BuildSummaryText();

            Assert.DoesNotContain("mesh", text);
            Assert.DoesNotContain("never arrived", text);
            Assert.DoesNotContain("Failed", text);
            Assert.DoesNotContain("skipped", text);
        }

        [Fact]
        public void Summary_Bake_MentionsAsMeshMissingSkippedFacesAndFailures()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.RequestedCount = 5;
            result.CreatedCount = 2;
            result.AsMeshCount = 1;
            result.MissingCount = 2;
            result.SkippedFaceCount = 4;
            result.AddFailure("Wall", "category OST_Views not allowed for a DirectShape");

            string text = result.BuildSummaryText();

            Assert.Contains("as a mesh", text);
            Assert.Contains("never arrived: 2", text);
            Assert.Contains("4 skipped", text);
            Assert.Contains("Failed: 1", text);
            Assert.Contains("'Wall'", text);
            Assert.Contains("OST_Views not allowed", text);
        }

        [Fact]
        public void Summary_Bake_OnFailure_SaysWhyAndNotTheWrittenCounts()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = false;
            result.RequestedCount = 2;
            result.FailureReason = "the document is read-only";

            string text = result.BuildSummaryText();

            Assert.Contains("NOT performed", text);
            Assert.Contains("read-only", text);
            Assert.DoesNotContain("created", text);
        }

        [Fact]
        public void Summary_Remove_ReportsRemovedElementsAndRequestedObjects()
        {
            BakeResult result = new BakeResult(BakeResult.ActionRemove);
            result.Succeeded = true;
            result.RequestedCount = 2;
            result.RemovedCount = 3;

            string text = result.BuildSummaryText();

            Assert.Contains("3 elements", text);
            Assert.Contains("2 objects", text);
            Assert.DoesNotContain("whole", text);
        }

        [Fact]
        public void Summary_Remove_OnFailure_SaysWhy()
        {
            BakeResult result = new BakeResult(BakeResult.ActionRemove);
            result.Succeeded = false;
            result.FailureReason = "no active document";

            string text = result.BuildSummaryText();

            Assert.Contains("NOT performed", text);
            Assert.Contains("no active document", text);
        }

        // ---- Failed ----

        [Fact]
        public void Failed_FromABatch_CarriesRequestedAndMissing()
        {
            BakeBatch batch = new BakeBatch(new List<string> { "a", "b", "c" });
            batch.Add(BakeGolden.Request("a", "A"));

            BakeResult result = BakeResult.Failed(batch, "the document is a family");

            Assert.Equal(BakeResult.ActionBake, result.Action);
            Assert.False(result.Succeeded);
            Assert.Equal(3, result.RequestedCount);
            Assert.Equal(2, result.MissingCount);
            Assert.Equal("the document is a family", result.FailureReason);
            Assert.Equal("the document is a family", result.BuildMessage());
        }

        [Fact]
        public void Failed_ToleratesANullBatch()
        {
            BakeResult result = BakeResult.Failed(null, "no active document");

            Assert.False(result.Succeeded);
            Assert.Equal(0, result.RequestedCount);
            Assert.Contains("no active document", result.BuildSummaryText());
        }

        [Fact]
        public void FailedRemoval_CarriesTheRequestedIds()
        {
            BakeResult result = BakeResult.FailedRemoval(new List<string> { "a", "b" }, "read-only");

            Assert.Equal(BakeResult.ActionRemove, result.Action);
            Assert.False(result.Succeeded);
            Assert.Equal(2, result.RequestedCount);
            Assert.Equal("read-only", result.BuildMessage());
        }

        [Fact]
        public void FailedRemoval_ToleratesNullIds()
        {
            BakeResult result = BakeResult.FailedRemoval(null, "no active document");

            Assert.Equal(0, result.RequestedCount);
            Assert.False(result.Succeeded);
        }
    }
}
