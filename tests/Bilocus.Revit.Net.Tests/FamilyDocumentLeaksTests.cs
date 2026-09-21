// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // The safety net of the family bake: family documents opened by the
    // batch and left open are closed again at the end of the batch, and the
    // Note says so. A document left open is invisible to the user and blocks
    // the re-bake with a "close the editor" that has no editor to close.
    public class FamilyDocumentLeaksTests
    {
        private sealed class FakeDocument
        {
            public bool Open = true;
            public int CloseCalls;
            public string CloseFailure;
            public Exception CloseThrows;
        }

        private static bool IsOpen(FakeDocument document)
        {
            return document.Open;
        }

        private static string Close(FakeDocument document)
        {
            document.CloseCalls++;
            if (document.CloseThrows != null) { throw document.CloseThrows; }
            if (document.CloseFailure != null) { return document.CloseFailure; }
            document.Open = false;
            return null;
        }

        [Fact]
        public void Sweep_WithNothingRegistered_GivesNoNote()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();

            Assert.Null(leaks.Sweep(IsOpen, Close));
        }

        [Fact]
        public void Sweep_DocumentsAlreadyClosed_AreNotTouched()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            FakeDocument cube = new FakeDocument { Open = false };
            FakeDocument sphere = new FakeDocument { Open = false };
            leaks.Register(cube, "Cube");
            leaks.Register(sphere, "Sphere");

            Assert.Null(leaks.Sweep(IsOpen, Close));
            Assert.Equal(0, cube.CloseCalls);
            Assert.Equal(0, sphere.CloseCalls);
        }

        [Fact]
        public void Sweep_LeftOpen_IsClosedAndNamedByTheBlenderObject()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            FakeDocument cube = new FakeDocument();
            leaks.Register(cube, "Cube");

            string note = leaks.Sweep(IsOpen, Close);

            Assert.Equal("family documents left open and closed now: 'Cube'", note);
            Assert.False(cube.Open);
            Assert.Equal(1, cube.CloseCalls);
        }

        [Fact]
        public void Sweep_CloseRefused_IsReportedAsNotClosed()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            leaks.Register(new FakeDocument { CloseFailure = "Close returned false" }, "Cube");

            string note = leaks.Sweep(IsOpen, Close);

            Assert.Equal(
                "family documents left open and NOT closed, they will free up only by restarting Revit: "
                + "'Cube' (Close returned false)",
                note);
        }

        [Fact]
        public void Sweep_MixedOutcomes_ListClosedFirstThenNotClosed()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            leaks.Register(new FakeDocument { CloseFailure = "Close returned false" }, "Cube");
            leaks.Register(new FakeDocument { Open = false }, "Cone");
            leaks.Register(new FakeDocument(), "Sphere");

            string note = leaks.Sweep(IsOpen, Close);

            Assert.Equal(
                "family documents left open and closed now: 'Sphere'; "
                + "family documents left open and NOT closed, they will free up only by restarting Revit: "
                + "'Cube' (Close returned false)",
                note);
        }

        [Fact]
        public void Sweep_CloseThrowing_DoesNotStopTheOthers()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            FakeDocument cube = new FakeDocument { CloseThrows = new InvalidOperationException("open transaction") };
            FakeDocument sphere = new FakeDocument();
            leaks.Register(cube, "Cube");
            leaks.Register(sphere, "Sphere");

            string note = leaks.Sweep(IsOpen, Close);

            Assert.False(sphere.Open);
            Assert.Contains("closed now: 'Sphere'", note);
            Assert.Contains("'Cube' (InvalidOperationException: open transaction)", note);
        }

        [Fact]
        public void Sweep_UnreadableState_IsReportedWithoutClosing()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            FakeDocument cube = new FakeDocument();
            leaks.Register(cube, "Cube");

            string note = leaks.Sweep(d => { throw new InvalidOperationException("invalid object"); }, Close);

            Assert.Contains("'Cube' (state unreadable: InvalidOperationException: invalid object)", note);
            Assert.Equal(0, cube.CloseCalls);
        }

        [Fact]
        public void Sweep_NeverTouchesDocumentsItDidNotRegister()
        {
            // A family document opened by the user does not go through
            // Register: even if "is it open" always answers yes, it is not
            // closed.
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            FakeDocument userDocument = new FakeDocument();
            FakeDocument bridgeDocument = new FakeDocument { Open = false };
            leaks.Register(bridgeDocument, "Cube");

            Assert.Null(leaks.Sweep(d => true == d.Open, Close));
            Assert.True(userDocument.Open);
            Assert.Equal(0, userDocument.CloseCalls);
        }

        [Fact]
        public void Sweep_EmptiesTheList_SoASecondSweepSaysNothing()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            leaks.Register(new FakeDocument(), "Cube");

            Assert.NotNull(leaks.Sweep(IsOpen, Close));
            Assert.Equal(0, leaks.Count);
            Assert.Null(leaks.Sweep(IsOpen, Close));
        }

        [Fact]
        public void Register_RejectsNull()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();

            Assert.Throws<ArgumentNullException>(() => leaks.Register(null, "Cube"));
        }

        [Fact]
        public void Register_NullName_StillProducesAReadableNote()
        {
            FamilyDocumentLeaks<FakeDocument> leaks = new FamilyDocumentLeaks<FakeDocument>();
            leaks.Register(new FakeDocument(), null);

            Assert.Equal("family documents left open and closed now: ''", leaks.Sweep(IsOpen, Close));
        }
    }
}
