// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Text.Json;
using Bilocus.Revit.Net;
using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // proxy_result, the outcome of proxy creation that goes back to Blender
    // (DESIGN.md 5.4), built by MessageRouter.BuildProxyResult.
    //
    // The message exists to fill a precise gap: creation starts from an
    // ExternalEvent triggered by the network and does NOT open any dialog, on
    // purpose. Whoever pressed the button, though, is in Blender, and without
    // this message, on success they would receive nothing: only failure would
    // reach them, as an error frame.
    //
    // The field names are the contract with bridge_receive.read_proxy_result:
    // changing one here without changing it there does not break any
    // compilation, it breaks the Blender panel at runtime.
    public class ProxyResultMessageTests
    {
        private static ProxyBuildResult Sample()
        {
            ProxyBuildResult result = new ProxyBuildResult();
            result.ObjectId = "abc123";
            result.Name = "Cube";
            result.RequestedCount = 12;
            result.CreatedCount = 10;
            result.ReplacedCount = 4;
            result.SkippedDegenerateCount = 2;
            result.FailedCount = 0;
            result.Succeeded = true;
            return result;
        }

        [Fact]
        public void BuildProxyResult_HasAllTheContractFields()
        {
            string header = MessageRouter.BuildProxyResult(Sample());

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("proxy_result", root.GetProperty("type").GetString());
            Assert.Equal("abc123", root.GetProperty("obj_id").GetString());
            Assert.Equal("Cube", root.GetProperty("name").GetString());
            Assert.True(root.GetProperty("ok").GetBoolean());
            Assert.Equal(12, root.GetProperty("requested").GetInt32());
            Assert.Equal(10, root.GetProperty("created").GetInt32());
            Assert.Equal(4, root.GetProperty("replaced").GetInt32());
            Assert.Equal(2, root.GetProperty("skipped").GetInt32());
            Assert.Equal(0, root.GetProperty("failed").GetInt32());
            Assert.Equal(0, root.GetProperty("planes_deleted").GetInt32());
            Assert.Equal(0, root.GetProperty("planes_kept").GetInt32());
            Assert.Equal("", root.GetProperty("message").GetString());
        }

        // The two counts about plane cleanup travel because replacement, the
        // most used path, does not open any dialog in Revit: without them the
        // only place to see whether the cleanup is working would be on the
        // other side of the bridge from whoever pressed the button.
        [Fact]
        public void BuildProxyResult_CarriesTheSketchPlaneCleanupCounts()
        {
            ProxyBuildResult result = Sample();
            result.ReplacedSketchPlanes.Measured = true;
            result.ReplacedSketchPlanes.ConsideredCount = 4;
            result.ReplacedSketchPlanes.DeletedCount = 3;
            result.ReplacedSketchPlanes.KeptCount = 1;

            JsonElement root = JsonDocument.Parse(
                MessageRouter.BuildProxyResult(result)).RootElement;

            Assert.Equal(3, root.GetProperty("planes_deleted").GetInt32());
            Assert.Equal(1, root.GetProperty("planes_kept").GetInt32());
        }

        [Fact]
        public void BuildProxyResult_OnFailure_SaysSoAndCarriesTheReason()
        {
            ProxyBuildResult result = ProxyBuildResult.Failed(null, "the document is read-only");
            result.Name = "Cube";

            JsonElement root = JsonDocument.Parse(
                MessageRouter.BuildProxyResult(result)).RootElement;

            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Equal("the document is read-only", root.GetProperty("message").GetString());
            Assert.Equal(0, root.GetProperty("created").GetInt32());
        }

        [Fact]
        public void BuildProxyResult_SendsTheFailureReasonOnly_NotTheWholeSummary()
        {
            // BuildSummaryText is multiline and contains the sketch planes and
            // the milliseconds: it is the text of the Status button inside
            // Revit, not a panel line. On this wire travel the counts, which
            // the Blender side reformats on its own.
            ProxyBuildResult result = Sample();
            result.SketchPlanesMeasured = true;
            result.SketchPlaneCount = 10;
            result.ElapsedMs = 123.4;

            string header = MessageRouter.BuildProxyResult(result);

            Assert.DoesNotContain("\\n", header);
            Assert.DoesNotContain("Sketch planes", header);
        }

        [Fact]
        public void BuildProxyResult_NullStrings_BecomeEmptyStrings()
        {
            ProxyBuildResult result = Sample();
            result.ObjectId = null;
            result.Name = null;
            result.FailureReason = null;

            JsonElement root = JsonDocument.Parse(
                MessageRouter.BuildProxyResult(result)).RootElement;

            Assert.Equal("", root.GetProperty("obj_id").GetString());
            Assert.Equal("", root.GetProperty("name").GetString());
            Assert.Equal("", root.GetProperty("message").GetString());
        }

        [Fact]
        public void BuildProxyResult_EscapesAnAwkwardName()
        {
            // Names come from Blender objects and can contain quotes and
            // non-ASCII characters: Utf8JsonWriter writes them correctly, a
            // hand-rolled string.Format does not.
            ProxyBuildResult result = Sample();
            result.Name = "Wall \"north\" \\ 2";

            JsonElement root = JsonDocument.Parse(
                MessageRouter.BuildProxyResult(result)).RootElement;

            Assert.Equal("Wall \"north\" \\ 2", root.GetProperty("name").GetString());
        }

        [Fact]
        public void BuildProxyResult_NullResult_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MessageRouter.BuildProxyResult(null));
        }

        [Fact]
        public void BuildProxyResult_CarriesTheArcCount()
        {
            ProxyBuildResult result = Sample();
            result.CreatedArcCount = 4;

            JsonElement root = JsonDocument.Parse(
                MessageRouter.BuildProxyResult(result)).RootElement;

            Assert.Equal(4, root.GetProperty("arcs").GetInt32());
        }
    }
}
