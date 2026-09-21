// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The family name of a Blender object: BL_<name>. It is only a label
    // (identity is the obj_id mark), but Revit rejects names with certain
    // characters and ones already taken, and a rejected name fails the
    // object inside the transaction, where it costs more.
    public class FamilyNamingTests
    {
        [Fact]
        public void BuildName_PrefixesBL()
        {
            Assert.Equal("BL_Cube", FamilyNaming.BuildName("Cube"));
        }

        [Fact]
        public void BuildName_KeepsSpacesInsideTheName()
        {
            Assert.Equal("BL_North wall.001", FamilyNaming.BuildName("North wall.001"));
        }

        [Theory]
        [InlineData("a\\b", "BL_a_b")]
        [InlineData("a/b", "BL_a_b")]
        [InlineData("a:b", "BL_a_b")]
        [InlineData("a*b", "BL_a_b")]
        [InlineData("a?b", "BL_a_b")]
        [InlineData("a\"b", "BL_a_b")]
        [InlineData("a<b>", "BL_a_b_")]
        [InlineData("a|b", "BL_a_b")]
        [InlineData("{a}", "BL__a_")]
        [InlineData("[a]", "BL__a_")]
        [InlineData("a;b", "BL_a_b")]
        [InlineData("a~b", "BL_a_b")]
        [InlineData("a`b", "BL_a_b")]
        public void BuildName_ReplacesTheCharactersRevitForbids(string objectName, string expected)
        {
            Assert.Equal(expected, FamilyNaming.BuildName(objectName));
        }

        [Fact]
        public void BuildName_ReplacesControlCharacters()
        {
            Assert.Equal("BL_a_b_c", FamilyNaming.BuildName("a\0b\ac"));
        }

        [Fact]
        public void BuildName_TrimsTheEdges()
        {
            Assert.Equal("BL_Cube", FamilyNaming.BuildName("  Cube \t"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void BuildName_WithoutAName_FallsBackOnObject(string objectName)
        {
            Assert.Equal("BL_object", FamilyNaming.BuildName(objectName));
        }

        [Fact]
        public void BuildName_CapsTheLength()
        {
            string name = FamilyNaming.BuildName(new string('x', 500));

            Assert.Equal(FamilyNaming.MaxLength, name.Length);
            Assert.Equal(120, FamilyNaming.MaxLength);
            Assert.StartsWith("BL_xxx", name);
        }

        // Cutting a surrogate pair in half would leave an invalid string,
        // which Revit would reject or save mangled.
        [Fact]
        public void BuildName_DoesNotCutASurrogatePairInHalf()
        {
            string emoji = char.ConvertFromUtf32(0x1F600);
            string objectName = new string('x', FamilyNaming.MaxLength - 4) + emoji + "yyy";

            string name = FamilyNaming.BuildName(objectName);

            Assert.True(name.Length <= FamilyNaming.MaxLength);
            Assert.False(char.IsHighSurrogate(name[name.Length - 1]));
        }

        [Fact]
        public void MakeUnique_AFreeName_StaysAsItIs()
        {
            Assert.Equal("BL_Cube", FamilyNaming.MakeUnique("BL_Cube", new List<string> { "BL_Sphere" }));
        }

        [Fact]
        public void MakeUnique_ATakenName_GetsTheFirstFreeSuffix()
        {
            List<string> taken = new List<string> { "BL_Cube", "BL_Cube_2", "BL_Cube_4" };

            Assert.Equal("BL_Cube_3", FamilyNaming.MakeUnique("BL_Cube", taken));
        }

        // Revit compares family names ignoring case: a "bl_cube" already
        // present also makes "BL_Cube" taken.
        [Fact]
        public void MakeUnique_ComparesIgnoringCase()
        {
            List<string> taken = new List<string> { "bl_cube", "BL_CUBE_2" };

            Assert.Equal("BL_Cube_3", FamilyNaming.MakeUnique("BL_Cube", taken));
        }

        // The suffix must not push the name past the maximum length.
        [Fact]
        public void MakeUnique_KeepsTheSuffixWithinTheMaximumLength()
        {
            string name = FamilyNaming.BuildName(new string('x', 500));

            string unique = FamilyNaming.MakeUnique(name, new List<string> { name });

            Assert.True(unique.Length <= FamilyNaming.MaxLength);
            Assert.EndsWith("_2", unique);
        }

        [Fact]
        public void MakeUnique_RejectsNull()
        {
            Assert.Throws<ArgumentNullException>(delegate { FamilyNaming.MakeUnique(null, new List<string>()); });
            Assert.Throws<ArgumentNullException>(delegate { FamilyNaming.MakeUnique("BL_Cube", null); });
        }
    }
}
