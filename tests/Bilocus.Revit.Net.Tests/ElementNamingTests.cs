// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Bilocus.Revit.Pull;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // ElementNaming is the pure part of Phase A2: it decides what an object
    // imported into Blender is called and what color it arrives with. No
    // Revit API, so it is testable here as a linked source.
    public class ElementNamingTests
    {
        [Fact]
        public void BuildName_WithCategoryAndType_PutsEverythingInSequence()
        {
            Assert.Equal("Walls - Base wall 200 [123456]",
                ElementNaming.BuildName("Walls", "Base wall 200", 123456));
        }

        [Fact]
        public void BuildName_WithoutCategory_UsesElement()
        {
            Assert.Equal("Element - Base wall 200 [7]",
                ElementNaming.BuildName(null, "Base wall 200", 7));
        }

        [Fact]
        public void BuildName_WithoutType_AlsoOmitsTheSeparator()
        {
            // The defect this test blocks is " -  [7]": a separator printed
            // around emptiness.
            Assert.Equal("Walls [7]", ElementNaming.BuildName("Walls", null, 7));
        }

        [Fact]
        public void BuildName_WithoutCategoryOrType_KeepsOnlyElementAndId()
        {
            Assert.Equal("Element [7]", ElementNaming.BuildName(null, null, 7));
        }

        [Fact]
        public void BuildName_EmptyStringsCountAsMissing()
        {
            Assert.Equal("Element [7]", ElementNaming.BuildName("", "", 7));
        }

        [Fact]
        public void BuildName_SpacesOnlyCountAsMissing()
        {
            // An unfilled type parameter in Revit often comes back as a
            // string of spaces, not as null.
            Assert.Equal("Element [7]", ElementNaming.BuildName("   ", "\t ", 7));
        }

        [Fact]
        public void BuildName_TrimsOuterSpaces()
        {
            Assert.Equal("Walls - Base wall [7]",
                ElementNaming.BuildName("  Walls  ", " Base wall ", 7));
        }

        [Fact]
        public void BuildName_JsonSensitiveCharactersStayIntact()
        {
            // Quotes and backslashes are escaped by the router's JSON writer:
            // this pins down that BuildName does not touch or lose them.
            Assert.Equal("Generic Models - Type \"A\\B\" [9]",
                ElementNaming.BuildName("Generic Models", "Type \"A\\B\"", 9));
        }

        [Fact]
        public void BuildName_LargeIdDoesNotOverflow()
        {
            Assert.Equal("Walls - Base [4294967296]",
                ElementNaming.BuildName("Walls", "Base", 4294967296L));
        }

        [Fact]
        public void DefaultColor_HasFourComponentsWithFullAlpha()
        {
            float[] color = ElementNaming.DefaultColor();

            Assert.Equal(4, color.Length);
            Assert.Equal(1f, color[3]);
        }

        [Fact]
        public void DefaultColor_StaysInTheZeroOneRange()
        {
            float[] color = ElementNaming.DefaultColor();

            foreach (float component in color)
            {
                Assert.InRange(component, 0f, 1f);
            }
        }

        [Fact]
        public void DefaultColor_IsNotBlendersDefaultGray()
        {
            // The point of the color is to recognize at a glance what comes
            // from Revit. A neutral gray (r == g == b) would be confused with
            // Blender's default material.
            float[] color = ElementNaming.DefaultColor();

            Assert.False(color[0] == color[1] && color[1] == color[2],
                "the default color must not be a neutral gray");
        }

        [Fact]
        public void DefaultColor_ReturnsANewArrayEveryTime()
        {
            // If it always returned the same array, a caller that modifies it
            // would change the color of every subsequent element.
            float[] first = ElementNaming.DefaultColor();
            first[0] = 42f;

            float[] second = ElementNaming.DefaultColor();

            Assert.NotEqual(42f, second[0]);
        }
    }
}
