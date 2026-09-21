// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Geometry;
using Xunit;

namespace Bilocus.Geometry.Tests
{
    // The level on which to place a bridge family instance: the closest one
    // BELOW the object's origin (decision 4 of Phase B2). Revit honors the
    // absolute Z by computing the offset, so a wrong level does not move the
    // instance: it puts it on a level the user does not expect, and they
    // only notice when they delete or move that level.
    public class LevelPickerTests
    {
        private static List<double> Levels(params double[] elevations)
        {
            return new List<double>(elevations);
        }

        [Fact]
        public void PicksTheHighestLevelBelow()
        {
            Assert.Equal(1, LevelPicker.Pick(Levels(0.0, 3.0, 6.0), 4.5));
        }

        // The order of the list does not matter: Revit returns levels in
        // whatever order it wants.
        [Fact]
        public void DoesNotAssumeTheListIsSorted()
        {
            Assert.Equal(0, LevelPicker.Pick(Levels(3.0, 6.0, 0.0, -3.0), 5.9));
        }

        // An object resting exactly on the level sits on that level, not on
        // the one below: hence the tolerance.
        [Fact]
        public void ALevelAtTheSameHeight_CountsAsBelow()
        {
            Assert.Equal(1, LevelPicker.Pick(Levels(0.0, 3.0), 3.0));
            Assert.Equal(1, LevelPicker.Pick(Levels(0.0, 3.0), 3.0 - 5e-7));
        }

        [Fact]
        public void ALevelJustAboveTheTolerance_IsNotBelow()
        {
            Assert.Equal(0, LevelPicker.Pick(Levels(0.0, 3.0), 3.0 - 2e-6));
        }

        // Object below every level (an excavation, a foundation): better the
        // lowest level with a negative offset than no instance.
        [Fact]
        public void NothingBelow_PicksTheLowestLevel()
        {
            Assert.Equal(2, LevelPicker.Pick(Levels(0.0, 3.0, -1.0), -5.0));
        }

        [Fact]
        public void EqualElevations_PickTheFirst()
        {
            Assert.Equal(1, LevelPicker.Pick(Levels(-2.0, 1.0, 1.0), 2.0));
            Assert.Equal(0, LevelPicker.Pick(Levels(1.0, 3.0, 1.0), -4.0));
        }

        [Fact]
        public void EmptyList_ReturnsMinusOne()
        {
            Assert.Equal(-1, LevelPicker.Pick(Levels(), 0.0));
        }

        [Fact]
        public void NullList_IsRejected()
        {
            Assert.Throws<ArgumentNullException>(delegate { LevelPicker.Pick(null, 0.0); });
        }

        // A NaN is never "below" and never "the lowest": it would slip
        // through silently and the choice would depend on its position in
        // the list.
        [Theory]
        [InlineData(double.NaN)]
        [InlineData(double.PositiveInfinity)]
        public void NonFiniteValues_AreRejected(double bad)
        {
            Assert.Throws<ArgumentException>(delegate { LevelPicker.Pick(Levels(0.0), bad); });
            Assert.Throws<ArgumentException>(delegate { LevelPicker.Pick(Levels(0.0, bad), 1.0); });
        }
    }
}
