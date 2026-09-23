// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Text.Json;
using Bilocus.Revit.Bake;
using Bilocus.Revit.Net;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // bake_result, the outcome of the bake or removal that goes back to
    // Blender, built by MessageRouter.BuildBakeResult.
    //
    // The field names are the contract with read_bake_result on the Blender
    // side, which reads them STRICTLY: changing one here without changing it
    // there does not break any build, it breaks the Blender panel at
    // runtime.
    public class BakeResultMessageTests
    {
        private static JsonElement Parse(BakeResult result)
        {
            return JsonDocument.Parse(MessageRouter.BuildBakeResult(result)).RootElement;
        }

        private static BakeResult Sample()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.RequestedCount = 11;
            result.CreatedCount = 1;
            result.ReplacedCount = 2;
            result.RecreatedCount = 3;
            result.RemovedCount = 4;
            result.FailedCount = 5;
            result.MissingCount = 6;
            result.AsMeshCount = 7;
            result.FacesPlanarCount = 8;
            result.FacesTriangulatedCount = 9;
            return result;
        }

        // Fields and order of the wire contract (DESIGN.md 5.4). The order does
        // not matter for a JSON parser, but the contract fixes it, and a test
        // that enumerates the names is also the most direct way to notice
        // an extra or missing field.
        [Fact]
        public void BuildBakeResult_HasTheContractFieldsInTheContractOrder()
        {
            List<string> names = new List<string>();
            foreach (JsonProperty property in Parse(Sample()).EnumerateObject())
            {
                names.Add(property.Name);
            }

            // Phase B2: target, switched, not_moved after the Phase B fields
            // and before message, which stays last.
            Assert.Equal(new List<string>
            {
                "type", "action", "ok", "requested", "created", "replaced", "recreated", "removed",
                "failed", "missing", "as_mesh", "faces_planar", "faces_triangulated",
                "target", "switched", "not_moved", "message"
            }, names);
        }

        [Fact]
        public void BuildBakeResult_CarriesTargetSwitchedAndNotMoved()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake, BakeTarget.Family);
            result.Succeeded = true;
            result.SwitchedCount = 12;
            result.NotMovedCount = 13;

            JsonElement root = Parse(result);

            Assert.Equal("family", root.GetProperty("target").GetString());
            Assert.Equal(12, root.GetProperty("switched").GetInt32());
            Assert.Equal(13, root.GetProperty("not_moved").GetInt32());
        }

        // The Blender side rejects crossed pairs: bake with the batch's
        // target, remove always with "all", even in failed outcomes.
        [Fact]
        public void BuildBakeResult_FailedFamilyBatch_HasActionBakeAndTargetFamily()
        {
            BakeBatch batch = new BakeBatch(new List<string> { "a" }, BakeTarget.Family);

            JsonElement root = Parse(BakeResult.Failed(batch, "template missing"));

            Assert.Equal("bake", root.GetProperty("action").GetString());
            Assert.Equal("family", root.GetProperty("target").GetString());
            Assert.Equal(0, root.GetProperty("switched").GetInt32());
            Assert.Equal(0, root.GetProperty("not_moved").GetInt32());
        }

        [Fact]
        public void BuildBakeResult_FailedRemoval_HasActionRemoveAndTargetAll()
        {
            JsonElement root = Parse(BakeResult.FailedRemoval(new List<string> { "a" }, "read-only"));

            Assert.Equal("remove", root.GetProperty("action").GetString());
            Assert.Equal("all", root.GetProperty("target").GetString());
            Assert.Equal(0, root.GetProperty("switched").GetInt32());
            Assert.Equal(0, root.GetProperty("not_moved").GetInt32());
        }

        [Fact]
        public void BuildBakeResult_TargetFollowsTheAction_WhenNotGiven()
        {
            Assert.Equal("directshape",
                Parse(new BakeResult(BakeResult.ActionBake)).GetProperty("target").GetString());
            Assert.Equal("all",
                Parse(new BakeResult(BakeResult.ActionRemove)).GetProperty("target").GetString());
        }

        [Fact]
        public void BuildBakeResult_CarriesEveryCountInItsOwnField()
        {
            JsonElement root = Parse(Sample());

            Assert.Equal("bake_result", root.GetProperty("type").GetString());
            Assert.Equal("bake", root.GetProperty("action").GetString());
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(11, root.GetProperty("requested").GetInt32());
            Assert.Equal(1, root.GetProperty("created").GetInt32());
            Assert.Equal(2, root.GetProperty("replaced").GetInt32());
            Assert.Equal(3, root.GetProperty("recreated").GetInt32());
            Assert.Equal(4, root.GetProperty("removed").GetInt32());
            Assert.Equal(5, root.GetProperty("failed").GetInt32());
            Assert.Equal(6, root.GetProperty("missing").GetInt32());
            Assert.Equal(7, root.GetProperty("as_mesh").GetInt32());
            Assert.Equal(8, root.GetProperty("faces_planar").GetInt32());
            Assert.Equal(9, root.GetProperty("faces_triangulated").GetInt32());
        }

        // The Blender side treats a missing field as a content error, not as
        // zero: zeros must travel written out.
        [Fact]
        public void BuildBakeResult_WritesTheCountsEvenWhenZero()
        {
            JsonElement root = Parse(new BakeResult(BakeResult.ActionRemove));

            foreach (string name in new string[]
            {
                "requested", "created", "replaced", "recreated", "removed", "failed", "missing",
                "as_mesh", "faces_planar", "faces_triangulated", "switched", "not_moved"
            })
            {
                Assert.Equal(JsonValueKind.Number, root.GetProperty(name).ValueKind);
                Assert.Equal(0, root.GetProperty(name).GetInt32());
            }
            Assert.Equal(JsonValueKind.False, root.GetProperty("ok").ValueKind);
        }

        [Fact]
        public void BuildBakeResult_OnFailure_SaysSoAndCarriesTheReason()
        {
            BakeResult result = BakeResult.Failed(null, "the document is read-only");

            JsonElement root = Parse(result);

            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("the document is read-only", root.GetProperty("message").GetString());
        }

        [Fact]
        public void BuildBakeResult_OnPartialSuccess_MessageNamesTheLastFailedObject()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.CreatedCount = 2;
            result.AddFailure("Wall", "category OST_Views not allowed for a DirectShape");

            JsonElement root = Parse(result);

            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(1, root.GetProperty("failed").GetInt32());
            Assert.Equal(result.BuildMessage(), root.GetProperty("message").GetString());
            Assert.Contains("Wall", root.GetProperty("message").GetString());
        }

        [Fact]
        public void BuildBakeResult_Remove_HasActionRemove()
        {
            BakeResult result = new BakeResult(BakeResult.ActionRemove);
            result.Succeeded = true;
            result.RequestedCount = 2;
            result.RemovedCount = 3;

            JsonElement root = Parse(result);

            Assert.Equal("remove", root.GetProperty("action").GetString());
            Assert.Equal(3, root.GetProperty("removed").GetInt32());
            Assert.Equal("", root.GetProperty("message").GetString());
        }

        // The multi-line text is for the Status button inside Revit: only
        // the counts and the message travel on the wire, and the Blender
        // side formats the panel line itself.
        [Fact]
        public void BuildBakeResult_SendsTheMessageOnly_NotTheSummary()
        {
            BakeResult result = Sample();
            result.ElapsedMs = 123.4;
            result.SkippedFaceCount = 3;

            string header = MessageRouter.BuildBakeResult(result);

            Assert.DoesNotContain("\\n", header);
            Assert.DoesNotContain("Time", header);
            Assert.DoesNotContain("skipped", header);
        }

        [Fact]
        public void BuildBakeResult_EscapesAnAwkwardName()
        {
            BakeResult result = new BakeResult(BakeResult.ActionBake);
            result.Succeeded = true;
            result.AddFailure("Wall \"north\" \\ 2", "rejected");

            JsonElement root = Parse(result);

            Assert.Contains("Wall \"north\" \\ 2", root.GetProperty("message").GetString());
        }

        [Fact]
        public void BuildBakeResult_NullResult_Throws()
        {
            Assert.Throws<ArgumentNullException>(delegate { MessageRouter.BuildBakeResult(null); });
        }
    }
}
