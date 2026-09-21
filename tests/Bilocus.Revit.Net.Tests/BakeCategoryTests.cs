// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The category name format: ^OST_[A-Za-z0-9_]+$, the same as the
    // Blender side. Whether the category really exists, and whether it is
    // allowed for a DirectShape, is decided by Revit at bake time and only
    // fails that object; here we only stop what cannot be an OST_ name.
    public class BakeCategoryTests
    {
        [Fact]
        public void Default_IsGenericModel_AndWellFormed()
        {
            Assert.Equal("OST_GenericModel", BakeCategory.DefaultCategory);
            Assert.True(BakeCategory.IsWellFormed(BakeCategory.DefaultCategory));
        }

        [Theory]
        [InlineData("OST_Walls")]
        [InlineData("OST_StructuralFraming")]
        [InlineData("OST_SpecialityEquipment")]
        [InlineData("OST_A1_b")]
        [InlineData("OST__")]
        public void WellFormedNames_AreAccepted(string category)
        {
            Assert.True(BakeCategory.IsWellFormed(category));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("OST_")]
        [InlineData("ost_Walls")]
        [InlineData("Walls")]
        [InlineData("OST-Walls")]
        [InlineData(" OST_Walls")]
        [InlineData("OST_Walls ")]
        [InlineData("OST_Wa lls")]
        [InlineData("OST_Walls-1")]
        [InlineData("OST_Walls\r\n")]
        [InlineData("OST_Walls\0")]
        [InlineData("OST_Chai\u00e9r")]
        public void MalformedNames_AreRejected(string category)
        {
            Assert.False(BakeCategory.IsWellFormed(category));
        }

        // Regression on the most natural shortcut: with Regex.IsMatch and
        // "^OST_[A-Za-z0-9_]+$", in .NET the $ also accepts a trailing \n and
        // this name would pass. The Blender side rejects it: the two sides
        // must agree on the same thing.
        [Fact]
        public void TrailingNewline_IsRejected()
        {
            Assert.False(BakeCategory.IsWellFormed("OST_Walls\n"));
        }
    }
}
