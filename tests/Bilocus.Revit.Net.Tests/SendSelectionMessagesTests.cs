// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Text.Json;
using Bilocus.Revit.Net;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The three Revit -> Blender headers (DESIGN.md 5.4), built
    // by MessageRouter.BuildBatchBegin / BuildGeometryHeader / BuildBatchEnd.
    // No BridgeServer involved: these methods are pure JSON string
    // construction, verifiable without a socket.
    public class SendSelectionMessagesTests
    {
        private static float[] Origin()
        {
            return new float[] { 0f, 0f, 0f };
        }

        [Fact]
        public void BuildBatchBegin_HasTypeAndCount()
        {
            string header = MessageRouter.BuildBatchBegin(7);

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("revit_batch_begin", root.GetProperty("type").GetString());
            Assert.Equal(7, root.GetProperty("count").GetInt32());
        }

        [Fact]
        public void BuildBatchEnd_HasOnlyType()
        {
            string header = MessageRouter.BuildBatchEnd();

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("revit_batch_end", root.GetProperty("type").GetString());
        }

        [Fact]
        public void BuildGeometryHeader_HasAllEightFields()
        {
            string header = MessageRouter.BuildGeometryHeader(
                123456L, "Walls - Base wall [123456]", "Walls", "Base wall",
                12, 4, new float[] { 1.5f, -2.25f, 3f }, new float[] { 0.45f, 0.55f, 0.65f, 1f });

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("revit_geometry", root.GetProperty("type").GetString());
            Assert.Equal(123456L, root.GetProperty("element_id").GetInt64());
            Assert.Equal("Walls - Base wall [123456]", root.GetProperty("name").GetString());
            Assert.Equal("Walls", root.GetProperty("category").GetString());
            Assert.Equal("Base wall", root.GetProperty("type_name").GetString());
            Assert.Equal(12, root.GetProperty("vert_count").GetInt32());
            Assert.Equal(4, root.GetProperty("tri_count").GetInt32());

            JsonElement origin = root.GetProperty("origin");
            Assert.Equal(3, origin.GetArrayLength());
            Assert.Equal(1.5f, origin[0].GetSingle());
            Assert.Equal(-2.25f, origin[1].GetSingle());
            Assert.Equal(3f, origin[2].GetSingle());

            JsonElement color = root.GetProperty("color");
            Assert.Equal(4, color.GetArrayLength());
            Assert.Equal(0.45f, color[0].GetSingle());
            Assert.Equal(1f, color[3].GetSingle());
        }

        // The origin is ALWAYS present, even when it is zero: the Blender side
        // requires it, a missing field is a content error. Writing it only
        // when different from zero would mean two formats.
        [Fact]
        public void BuildGeometryHeader_WritesAZeroOriginToo()
        {
            string header = MessageRouter.BuildGeometryHeader(
                1L, "x", "Walls", "Base wall", 3, 1, Origin(), new float[] { 0, 0, 0, 1 });

            JsonElement origin = JsonDocument.Parse(header).RootElement.GetProperty("origin");
            Assert.Equal(3, origin.GetArrayLength());
            Assert.Equal(0f, origin[0].GetSingle());
        }

        [Fact]
        public void BuildGeometryHeader_WrongOriginLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageRouter.BuildGeometryHeader(
                1L, "x", "Walls", "Base wall", 3, 1,
                new float[] { 1f, 2f }, new float[] { 0, 0, 0, 1 }));
        }

        [Fact]
        public void BuildGeometryHeader_NullOrigin_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MessageRouter.BuildGeometryHeader(
                1L, "x", "Walls", "Base wall", 3, 1, null, new float[] { 0, 0, 0, 1 }));
        }

        // category and type_name arrive null when the element has no category
        // or no type (system elements, symbols). The wire must never see a
        // JSON "null" on these fields: it would be one more case to handle on
        // the Blender side for a rare situation.
        [Fact]
        public void BuildGeometryHeader_NullCategoryAndTypeName_BecomeEmptyStrings()
        {
            string header = MessageRouter.BuildGeometryHeader(
                1L, "Element [1]", null, null, 3, 1, Origin(), new float[] { 0, 0, 0, 1 });

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal("", root.GetProperty("category").GetString());
            Assert.Equal("", root.GetProperty("type_name").GetString());
        }

        // Names come from the user's model: quotes, backslashes and non-ASCII
        // characters must cross the JSON round trip intact. A hand-rolled
        // string.Format here would be a bug waiting to happen.
        [Fact]
        public void BuildGeometryHeader_EscapesAwkwardName()
        {
            string awkward = "Wall \"exterior\" 20cm \\ \u00e0ngle";

            string header = MessageRouter.BuildGeometryHeader(
                1L, awkward, "Walls", "Exterior wall", 3, 1, Origin(), new float[] { 0, 0, 0, 1 });

            JsonElement root = JsonDocument.Parse(header).RootElement;
            Assert.Equal(awkward, root.GetProperty("name").GetString());
        }

        [Fact]
        public void BuildGeometryHeader_WrongColorLength_Throws()
        {
            Assert.Throws<ArgumentException>(() => MessageRouter.BuildGeometryHeader(
                1L, "x", "Walls", "Base wall", 3, 1, Origin(), new float[] { 0, 0, 0 }));
        }

        [Fact]
        public void BuildGeometryHeader_NullName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => MessageRouter.BuildGeometryHeader(
                1L, null, "Walls", "Base wall", 3, 1, Origin(), new float[] { 0, 0, 0, 1 }));
        }
    }
}
