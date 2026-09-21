// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Revit.Proxy;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // ProxyNaming is the part of the Phase A3 mark that does not touch the
    // Revit API: schema identity and obj_id rules. It is included as a linked
    // source, so it is testable here.
    //
    // The value of these tests is not coverage: it is that they turn three
    // errors red, in half a second, that would otherwise only be discovered
    // inside Revit, in a project file, after already having written.
    public class ProxyNamingTests
    {
        // This is the most important test in the file.
        //
        // The schema GUID is the mark's permanent identity. Changing it would
        // make every proxy created before it invisible: not replaceable, not
        // removable, stuck inside customer files forever. The comment in
        // ProxyNaming says so; this test enforces it.
        //
        // If you are reading this because the test is red: do NOT update the
        // expected value. Fix the constant.
        [Fact]
        public void SchemaGuid_NeverChanges()
        {
            Assert.Equal("b00fa2f7-f2ca-464b-877b-7e4bdec8018e", ProxyNaming.SchemaGuidText);
            Assert.Equal(new Guid("b00fa2f7-f2ca-464b-877b-7e4bdec8018e"), ProxyNaming.SchemaGuid);
        }

        // Revit accepts only C++-style identifiers as schema or field names.
        // A name with a space compiles just fine and fails inside Revit, on
        // the first proxy creation.
        [Fact]
        public void SchemaNames_AreAcceptableToRevit()
        {
            Assert.True(ProxyNaming.IsAcceptableStorageName(ProxyNaming.SchemaName));
            Assert.True(ProxyNaming.IsAcceptableStorageName(ProxyNaming.ObjectIdFieldName));
        }

        [Fact]
        public void VendorId_IsAcceptableToRevit()
        {
            Assert.True(ProxyNaming.IsAcceptableVendorId(ProxyNaming.VendorId));
        }

        // The subcategory name, on the other hand, is a category name: the
        // space is legal, Revit's reserved characters are not.
        [Fact]
        public void SubcategoryName_IsAcceptableToRevit()
        {
            Assert.True(ProxyNaming.IsAcceptableCategoryName(ProxyNaming.SubcategoryName));
            Assert.Equal("Bilocus Proxy", ProxyNaming.SubcategoryName);
        }

        [Theory]
        [InlineData("BilocusProxy")]
        [InlineData("obj_id")]
        [InlineData("_x")]
        [InlineData("a1")]
        public void IsAcceptableStorageName_AcceptsIdentifiers(string name)
        {
            Assert.True(ProxyNaming.IsAcceptableStorageName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Bilocus Proxy")]
        [InlineData("1obj")]
        [InlineData("obj-id")]
        [InlineData("obj.id")]
        public void IsAcceptableStorageName_RejectsWhatRevitRejects(string name)
        {
            Assert.False(ProxyNaming.IsAcceptableStorageName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("ab cd")]
        [InlineData("ab*cd")]
        public void IsAcceptableVendorId_RejectsShortOnesAndSymbolsOutsideTheList(string vendorId)
        {
            Assert.False(ProxyNaming.IsAcceptableVendorId(vendorId));
        }

        [Theory]
        [InlineData("Bilocus Proxy")]
        [InlineData("Proxy 01")]
        public void IsAcceptableCategoryName_AcceptsSpaces(string name)
        {
            Assert.True(ProxyNaming.IsAcceptableCategoryName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Proxy{1}")]
        [InlineData("Proxy:1")]
        [InlineData("Proxy\\1")]
        [InlineData("Proxy|1")]
        public void IsAcceptableCategoryName_RejectsReservedCharacters(string name)
        {
            Assert.False(ProxyNaming.IsAcceptableCategoryName(name));
        }

        [Theory]
        [InlineData("Cube")]
        [InlineData("Cube.001")]
        [InlineData("object with spaces")]
        public void IsValidObjectId_AcceptsBlenderIdentifiers(string objectId)
        {
            Assert.True(ProxyNaming.IsValidObjectId(objectId));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t")]
        public void IsValidObjectId_RejectsEmptyAndSpacesOnly(string objectId)
        {
            Assert.False(ProxyNaming.IsValidObjectId(objectId));
        }

        [Fact]
        public void IsValidObjectId_RejectsControlCharacters()
        {
            // A newline inside the obj_id no longer compares equal to itself
            // after a round trip through the messages' JSON header, and
            // replacing proxies would stop working without giving an error.
            Assert.False(ProxyNaming.IsValidObjectId("Cube\nOther"));
            Assert.False(ProxyNaming.IsValidObjectId("Cube\0"));
        }

        // The defect this test blocks: writing " Cube " into the mark and
        // then searching for "Cube". The replacement would find nothing,
        // resending would create a second copy of the proxies on top of the
        // first, and nothing would signal the error.
        [Fact]
        public void NormalizeObjectId_TrimsOuterSpaces()
        {
            Assert.Equal("Cube", ProxyNaming.NormalizeObjectId("  Cube  "));
            Assert.Equal("Cube", ProxyNaming.NormalizeObjectId("Cube"));
        }

        [Fact]
        public void NormalizeObjectId_DoesNotTouchInnerSpaces()
        {
            Assert.Equal("South Wall", ProxyNaming.NormalizeObjectId(" South Wall "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        [InlineData("Cube\nOther")]
        [InlineData("Cube\0")]
        public void NormalizeObjectId_ThrowsOnUnwritableValues(string objectId)
        {
            Assert.Throws<ArgumentException>(delegate
            {
                ProxyNaming.NormalizeObjectId(objectId);
            });
        }

        // Deliberate distinction, discovered while writing the test above: a
        // newline at the END is outer whitespace and Trim removes it like it
        // would a space. A newline IN THE MIDDLE, instead, breaks the
        // identifier and is not recoverable, so it throws. Rejecting the
        // first case too would mean failing a resend for a newline picked up
        // from a copy-paste, which is severity without gain.
        [Fact]
        public void NormalizeObjectId_ATrailingNewlineIsWhitespaceNotAnError()
        {
            Assert.Equal("Cube", ProxyNaming.NormalizeObjectId("Cube\n"));
            Assert.Equal("Cube", ProxyNaming.NormalizeObjectId("\tCube\r\n"));
        }

        [Fact]
        public void MatchesObjectId_ComparesNormalizedForms()
        {
            Assert.True(ProxyNaming.MatchesObjectId(" Cube ", "Cube"));
            Assert.True(ProxyNaming.MatchesObjectId("Cube", " Cube"));
        }

        // obj_ids come from the names of Blender objects, where "Cube" and
        // "cube" are two different objects. A case-insensitive comparison
        // would delete one object's proxies while resending the other.
        [Fact]
        public void MatchesObjectId_IsCaseSensitive()
        {
            Assert.False(ProxyNaming.MatchesObjectId("Cube", "cube"));
        }

        // The schema has public write access: what is read from the document
        // can be anything. Here the result must be "does not match", not an
        // exception in the middle of a loop over every curve in the model.
        [Fact]
        public void MatchesObjectId_TreatsNullsLeniently()
        {
            Assert.False(ProxyNaming.MatchesObjectId(null, "Cube"));
            Assert.False(ProxyNaming.MatchesObjectId("Cube", null));
            Assert.False(ProxyNaming.MatchesObjectId(null, null));
        }
    }
}
