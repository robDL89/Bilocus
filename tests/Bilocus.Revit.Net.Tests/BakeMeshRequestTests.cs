// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Bilocus.Revit.Bake;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    // Everything about a bake_mesh that can be checked without opening
    // Revit. Every check that passes through here is a check that must NOT
    // be repeated inside the bake transaction, where failing costs the
    // object.
    public class BakeMeshRequestTests
    {
        private static BakeMeshRequest Parse(string objectId, string name, string category, float[] matrix)
        {
            return BakeMeshRequest.Parse(objectId, name, category, matrix, 5, 2, 7, 3, BakeGolden.Payload());
        }

        [Fact]
        public void Parse_Golden_KeepsIdentityCategoryMatrixAndMesh()
        {
            float[] matrix = BakeGolden.Identity();
            matrix[3] = 2f;

            BakeMeshRequest request = Parse("obj-1", "Cube", "OST_Walls", matrix);

            Assert.Equal("obj-1", request.ObjectId);
            Assert.Equal("Cube", request.Name);
            Assert.Equal("OST_Walls", request.Category);
            Assert.Equal(matrix, request.Matrix);
            Assert.Equal(new int[] { 4, 3 }, request.Mesh.FaceSizes);
            Assert.Equal(new int[] { 0, 0, 1 }, request.Mesh.TriFaces);
            Assert.Equal(0.5f, request.Mesh.Positions[12]);
        }

        // The trim is not cosmetic: it is the form used to write the mark and
        // to look up the element to replace on re-bake.
        [Fact]
        public void Parse_TrimsObjectId()
        {
            BakeMeshRequest request = Parse("  obj-1  ", "Cube", "OST_Walls", BakeGolden.Identity());
            Assert.Equal("obj-1", request.ObjectId);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("obj\0one")]
        [InlineData("obj\none")]
        public void Parse_RejectsUnusableObjectId(string objectId)
        {
            Assert.Throws<ArgumentException>(delegate
            {
                Parse(objectId, "Cube", "OST_Walls", BakeGolden.Identity());
            });
        }

        // The name is a label, not identity: it becomes the DirectShape name
        // and the subject of the error message, and the obj_id is a more
        // useful fallback than an empty string in both places.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Parse_WithoutUsableName_FallsBackToObjectId(string name)
        {
            BakeMeshRequest request = Parse("obj-1", name, "OST_Walls", BakeGolden.Identity());
            Assert.Equal("obj-1", request.Name);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Walls")]
        [InlineData("OST_Walls\n")]
        public void Parse_RejectsMalformedCategory(string category)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                Parse("obj-1", "Cube", category, BakeGolden.Identity());
            });
            Assert.Contains("category", ex.Message);
        }

        // The cheap checks before the payload: a wrong category must not
        // require reading megabytes of geometry to be reported.
        [Fact]
        public void Parse_ChecksTheCategoryBeforeThePayload()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                BakeMeshRequest.Parse("obj-1", "Cube", "Walls", BakeGolden.Identity(), 5, 2, 7, 3, new byte[3]);
            });
            Assert.Contains("category", ex.Message);
        }

        [Fact]
        public void Parse_RejectsMissingMatrix()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                Parse("obj-1", "Cube", "OST_Walls", null);
            });
            Assert.Contains("matrix", ex.Message);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(15)]
        [InlineData(17)]
        public void Parse_RejectsMatrixOfWrongLength(int length)
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                Parse("obj-1", "Cube", "OST_Walls", new float[length]);
            });
            Assert.Contains("matrix", ex.Message);
        }

        // The JSON does not carry NaN, but a number beyond the float32 range
        // (1e39) becomes infinite on cast: that is how an infinity gets here.
        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void Parse_RejectsNonFiniteMatrix(float bad)
        {
            float[] matrix = BakeGolden.Identity();
            matrix[11] = bad;

            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                Parse("obj-1", "Cube", "OST_Walls", matrix);
            });
            Assert.Contains("matrix", ex.Message);
        }

        [Fact]
        public void Parse_PassesPayloadErrorsThrough()
        {
            ArgumentException ex = Assert.Throws<ArgumentException>(delegate
            {
                BakeMeshRequest.Parse("obj-1", "Cube", "OST_Walls", BakeGolden.Identity(), 5, 2, 7, 3, new byte[140]);
            });
            Assert.Contains("140 byte", ex.Message);
        }

        // Phase B2: the Phase B Parse stays, and "accept open solid" is off
        // if nobody asks for it.
        [Fact]
        public void Parse_WithoutAcceptOpen_IsFalse()
        {
            Assert.False(Parse("obj-1", "Cube", "OST_Walls", BakeGolden.Identity()).AcceptOpen);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Parse_WithAcceptOpen_KeepsIt(bool acceptOpen)
        {
            BakeMeshRequest request = BakeMeshRequest.Parse("obj-1", "Cube", "OST_Walls", BakeGolden.Identity(),
                5, 2, 7, 3, BakeGolden.Payload(), acceptOpen);

            Assert.Equal(acceptOpen, request.AcceptOpen);
            Assert.Equal("obj-1", request.ObjectId);
        }

        // The request stays pending until the next drain: a caller that
        // reuses its array must not move the object.
        [Fact]
        public void Parse_CopiesTheMatrix()
        {
            float[] matrix = BakeGolden.Identity();
            BakeMeshRequest request = Parse("obj-1", "Cube", "OST_Walls", matrix);

            matrix[3] = 99f;

            Assert.Equal(0f, request.Matrix[3]);
        }
    }
}
