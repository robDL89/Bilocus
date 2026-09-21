// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The batch of a bake: which objects Blender announced, which arrived,
    // which are missing. Missing and switched are not accounting details:
    // they are what the Blender panel tells the user after the bridge has
    // written into its document.
    public class BakeBatchTests
    {
        private static List<string> Ids(params string[] ids)
        {
            return new List<string>(ids);
        }

        [Fact]
        public void Constructor_KeepsTheAnnouncedOrder_Normalized()
        {
            BakeBatch batch = new BakeBatch(Ids(" b ", "a", "c"));

            Assert.Equal(new List<string> { "b", "a", "c" }, batch.AnnouncedIds);
        }

        // ---- bake together ----

        [Fact]
        public void Host_IsNormalized_AndMakesTheBatchTogether()
        {
            BakeBatch batch = new BakeBatch(Ids("a", "b"), BakeTarget.Family, " b ");

            Assert.Equal("b", batch.Host);
            Assert.True(batch.Together);
        }

        [Fact]
        public void WithoutHost_TheBatchIsNotTogether()
        {
            BakeBatch batch = new BakeBatch(Ids("a", "b"), BakeTarget.Family);

            Assert.Null(batch.Host);
            Assert.False(batch.Together);
        }

        [Fact]
        public void Host_IsOnlyAllowedForFamilies()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new BakeBatch(Ids("a", "b"), BakeTarget.DirectShape, "a");
            });
        }

        [Fact]
        public void Host_MustBeAnnounced()
        {
            Assert.Throws<ArgumentException>(delegate
            {
                new BakeBatch(Ids("a", "b"), BakeTarget.Family, "c");
            });
        }

        [Fact]
        public void Constructor_RejectsNull()
        {
            Assert.Throws<ArgumentNullException>(delegate { new BakeBatch(null); });
        }

        // A bake of nothing is not a bake: it is a defect of the Blender
        // side, which must stop earlier with "select objects that are in
        // ToRevit".
        [Fact]
        public void Constructor_RejectsAnEmptyList()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate { new BakeBatch(Ids()); });
            Assert.Contains("empty", ex.Message);
        }

        [Fact]
        public void Constructor_AcceptsExactlyTheCap()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < BakeBatch.MaxBakeObjects; i++) { ids.Add("obj-" + i); }

            BakeBatch batch = new BakeBatch(ids);

            Assert.Equal(500, BakeBatch.MaxBakeObjects);
            Assert.Equal(BakeBatch.MaxBakeObjects, batch.AnnouncedIds.Count);
        }

        [Fact]
        public void Constructor_RejectsMoreThanTheCap()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i <= BakeBatch.MaxBakeObjects; i++) { ids.Add("obj-" + i); }

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate { new BakeBatch(ids); });
            Assert.Contains("500", ex.Message);
        }

        // Two objects with the same id would end up on the same DirectShape,
        // and the last one to arrive would silently overwrite the first.
        [Fact]
        public void Constructor_RejectsDuplicates()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new BakeBatch(Ids("a", "b", "a"));
            });
            Assert.Contains("'a'", ex.Message);
        }

        [Fact]
        public void Constructor_RejectsIdsThatAreDuplicatesOnlyAfterNormalization()
        {
            Assert.Throws<ArgumentException>(delegate { new BakeBatch(Ids("a", " a")); });
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("obj\nuno")]
        public void Constructor_RejectsUnusableIds_NamingThePosition(string bad)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new BakeBatch(Ids("ok", bad));
            });
            Assert.Contains("obj_ids[1]", ex.Message);
        }

        [Fact]
        public void AnnouncedIds_CannotBeChangedFromOutside()
        {
            BakeBatch batch = new BakeBatch(Ids("a"));
            Assert.Throws<NotSupportedException>(delegate { batch.AnnouncedIds.Add("b"); });
        }

        [Fact]
        public void NewBatch_HasNoRequestsAndEverythingMissing()
        {
            BakeBatch batch = new BakeBatch(Ids("a", "b"));

            Assert.Empty(batch.Requests);
            Assert.Equal(new List<string> { "a", "b" }, batch.MissingIds);
        }

        [Fact]
        public void Add_RejectsAnIdThatWasNotAnnounced()
        {
            BakeBatch batch = new BakeBatch(Ids("a"));

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                batch.Add(BakeGolden.Request("b", "Cube"));
            });
            Assert.Contains("not announced", ex.Message);
            Assert.Empty(batch.Requests);
        }

        [Fact]
        public void Add_RejectsNull()
        {
            BakeBatch batch = new BakeBatch(Ids("a"));
            Assert.Throws<ArgumentNullException>(delegate { batch.Add(null); });
        }

        // The contract: a second bake_mesh for the same obj_id in the same
        // batch replaces the first. The last arrival wins, the same rule as
        // GeometryStore and proxy requests.
        [Fact]
        public void Add_SecondArrivalOfTheSameId_ReplacesTheFirst()
        {
            BakeBatch batch = new BakeBatch(Ids("a"));

            batch.Add(BakeGolden.Request("a", "First"));
            batch.Add(BakeGolden.Request("a", "Second"));

            Assert.Single(batch.Requests);
            Assert.Equal("Second", batch.Requests[0].Name);
            Assert.Empty(batch.MissingIds);
        }

        // Announcement order and not arrival order: it is the order in which
        // the Blender side listed the objects, and the bake writes them the
        // same way.
        [Fact]
        public void Requests_FollowTheAnnouncementOrder_NotTheArrivalOrder()
        {
            BakeBatch batch = new BakeBatch(Ids("a", "b", "c"));

            batch.Add(BakeGolden.Request("c", "C"));
            batch.Add(BakeGolden.Request("a", "A"));
            batch.Add(BakeGolden.Request("b", "B"));

            List<BakeMeshRequest> requests = batch.Requests;
            Assert.Equal(3, requests.Count);
            Assert.Equal("a", requests[0].ObjectId);
            Assert.Equal("b", requests[1].ObjectId);
            Assert.Equal("c", requests[2].ObjectId);
        }

        [Fact]
        public void MissingIds_AreTheAnnouncedOnesThatNeverArrived_InOrder()
        {
            BakeBatch batch = new BakeBatch(Ids("a", "b", "c", "d"));

            batch.Add(BakeGolden.Request("c", "C"));
            batch.Add(BakeGolden.Request("a", "A"));

            Assert.Equal(new List<string> { "b", "d" }, batch.MissingIds);
        }

        // ---- target (Phase B2) ----

        // The Phase B constructor stays the one used by existing callers, and
        // a batch without a target is a DirectShape bake as before.
        [Fact]
        public void Constructor_WithoutTarget_IsDirectShape()
        {
            Assert.Equal(BakeTarget.DirectShape, new BakeBatch(Ids("a")).Target);
        }

        [Theory]
        [InlineData("directshape")]
        [InlineData("family")]
        public void Constructor_WithTarget_KeepsIt(string target)
        {
            Assert.Equal(target, new BakeBatch(Ids("a"), target).Target);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("all")]
        [InlineData("Family")]
        public void Constructor_RejectsAnUnknownTarget(string target)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate { new BakeBatch(Ids("a"), target); });
            Assert.Contains("target", ex.Message);
        }

        // Each object of a family bake opens and closes a family document:
        // the cap is ten times lower.
        [Fact]
        public void FamilyBatch_AcceptsExactlyTheFamilyCap()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i < BakeBatch.MaxFamilyBakeObjects; i++) { ids.Add("obj-" + i); }

            BakeBatch batch = new BakeBatch(ids, BakeTarget.Family);

            Assert.Equal(50, BakeBatch.MaxFamilyBakeObjects);
            Assert.Equal(50, batch.AnnouncedIds.Count);
        }

        [Fact]
        public void FamilyBatch_RejectsMoreThanTheFamilyCap()
        {
            List<string> ids = new List<string>();
            for (int i = 0; i <= BakeBatch.MaxFamilyBakeObjects; i++) { ids.Add("obj-" + i); }

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                new BakeBatch(ids, BakeTarget.Family);
            });
            Assert.Contains("50", ex.Message);

            // The same 51 pass in a DirectShape batch.
            Assert.Equal(51, new BakeBatch(ids, BakeTarget.DirectShape).AnnouncedIds.Count);
        }

        [Fact]
        public void MaxObjectsFor_GivesTheCapOfEachTarget()
        {
            Assert.Equal(BakeBatch.MaxBakeObjects, BakeBatch.MaxObjectsFor(BakeTarget.DirectShape));
            Assert.Equal(BakeBatch.MaxFamilyBakeObjects, BakeBatch.MaxObjectsFor(BakeTarget.Family));
            Assert.Throws<ArgumentException>(delegate { BakeBatch.MaxObjectsFor("all"); });
        }

        // bake_remove does not use the batch but requires the same obj_ids:
        // the rules live in one place.
        [Fact]
        public void NormalizeObjectIds_AppliesTheSameRulesAsTheConstructor()
        {
            Assert.Equal(new List<string> { "a", "b" }, BakeBatch.NormalizeObjectIds(Ids(" a", "b ")));
            Assert.Throws<ArgumentException>(delegate { BakeBatch.NormalizeObjectIds(Ids()); });
            Assert.Throws<ArgumentException>(delegate { BakeBatch.NormalizeObjectIds(Ids("a", "a")); });
            Assert.Throws<ArgumentNullException>(delegate { BakeBatch.NormalizeObjectIds(null); });
        }
    }
}
