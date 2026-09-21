// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The mode of a bake, as it travels on the wire. The values are the
    // contract with the Blender side: changing one here without changing it
    // there does not break the build, it breaks the bake at runtime.
    public class BakeTargetTests
    {
        [Fact]
        public void Constants_AreTheWireValues()
        {
            Assert.Equal("directshape", BakeTarget.DirectShape);
            Assert.Equal("family", BakeTarget.Family);
            Assert.Equal("all", BakeTarget.All);
        }

        [Theory]
        [InlineData("directshape")]
        [InlineData("family")]
        public void TryParse_AcceptsTheTwoBakeTargets(string value)
        {
            string target;
            Assert.True(BakeTarget.TryParse(value, out target));
            Assert.Equal(value, target);
        }

        // "all" exists only in the bake_result of a removal: Blender cannot
        // request an "all" bake. Uppercase and spaces are not forgiven, as
        // for the category: the two sides must agree on the same string.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("all")]
        [InlineData("Family")]
        [InlineData("DirectShape")]
        [InlineData(" family")]
        [InlineData("family\n")]
        [InlineData("mass")]
        public void TryParse_RejectsAnythingElse(string value)
        {
            string target;
            Assert.False(BakeTarget.TryParse(value, out target));
            Assert.Null(target);
        }
    }
}
