// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    public class BakeParameterTests
    {
        [Fact]
        public void Create_KeepsTheValues()
        {
            BakeParameter p = BakeParameter.Create("BL_Length", "length", 2.4);
            Assert.Equal("BL_Length", p.Name);
            Assert.Equal("length", p.Kind);
            Assert.Equal(2.4, p.Value);
        }

        [Theory]
        [InlineData("Length", "length", 1.0)]
        [InlineData("BL_", "length", 1.0)]
        [InlineData("BL_Len gth", "length", 1.0)]
        [InlineData("BL_Length", "weight", 1.0)]
        [InlineData("BL_Length", "length", -0.1)]
        [InlineData("BL_Length", "length", double.PositiveInfinity)]
        [InlineData("BL_Length", "length", double.NaN)]
        public void Create_RejectsInvalid(string name, string kind, double value)
        {
            Assert.Throws<ArgumentException>(() => BakeParameter.Create(name, kind, value));
        }

        // A degenerate edge measures 0: still a value, not a missing one.
        [Fact]
        public void Create_AcceptsZero()
        {
            Assert.Equal(0.0, BakeParameter.Create("BL_Thickness", "length", 0.0).Value);
        }

        [Theory]
        [InlineData("BL_Length * 2", true)]
        [InlineData("if(BL_Length > 1 m, 1, 0)", true)]
        [InlineData("BL_Width+BL_Length", true)]
        [InlineData("(BL_Length)", true)]
        [InlineData("BL_LengthTotal / 2", false)]
        [InlineData("MyBL_Length * 2", false)]
        [InlineData("BL_Length2 * 2", false)]
        [InlineData("bl_length * 2", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsUsedIn_MatchesWholeNamesOnly(string formula, bool expected)
        {
            Assert.Equal(expected, BakeParameter.IsUsedIn("BL_Length", formula));
        }
    }
}
