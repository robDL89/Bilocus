// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Bilocus.Geometry;
using Bilocus.Protocol;

namespace Bilocus.Revit.Preview
{
    // GPU buffers of a single chunk. Built once and reused across frames:
    // rebuilding them on every RenderScene would kill the frame rate.
    //
    // Two colors, chosen in the Blender panel for the whole preview (see
    // GeometryStore.FaceColor / EdgeColor): faces and feature edges. What is
    // drawn depends on the view's display style:
    //   Wireframe    edges only
    //   Hidden line  flat faces (no lighting) + edges
    //   other styles lit faces + edges
    // Lit triangles need Revit's lights, which wireframe and hidden line do
    // not apply: drawn there they come out black. Hence the second, flat
    // colored vertex buffer.
    public sealed class GpuChunk
    {
        private int _vertexCount;
        private int _triangleCount;
        private int _lineCount;

        // Lit faces: position + normal + color.
        private VertexBuffer _litVertices;
        private VertexFormat _litFormat;
        private EffectInstance _litEffect;

        // Flat faces and edges: position + color. Two vertex buffers because
        // the color is stored per vertex.
        private VertexBuffer _flatFaceVertices;
        private VertexBuffer _edgeVertices;
        private VertexFormat _flatFormat;
        private EffectInstance _flatEffect;

        private IndexBuffer _triangleIndices;
        private IndexBuffer _lineIndices;

        public bool IsValid()
        {
            return _litVertices != null && _litVertices.IsValid()
                && _flatFaceVertices != null && _flatFaceVertices.IsValid()
                && _triangleIndices != null && _triangleIndices.IsValid()
                && (_lineCount == 0
                    || (_edgeVertices != null && _edgeVertices.IsValid()
                        && _lineIndices != null && _lineIndices.IsValid()));
        }

        // Positions arrive in METERS and get converted to feet here, because
        // feet are Revit's internal unit and the wire speaks meters.
        public void Build(MeshChunk chunk, ColorWithTransparency faceColor, ColorWithTransparency edgeColor)
        {
            _vertexCount = chunk.VertexCount;
            _triangleCount = chunk.TriangleCount;

            if (_vertexCount > MeshChunker.MaxVerticesPerChunk)
            {
                // Not paranoia: exceeding the limit does NOT raise exceptions,
                // Revit truncates silently. If this fires, the split has a bug.
                throw new InvalidOperationException(string.Format(
                    "chunk of {0} vertices over the limit of {1}",
                    _vertexCount, MeshChunker.MaxVerticesPerChunk));
            }

            XYZ[] positions = new XYZ[_vertexCount];
            double scale = BridgeConstants.FeetPerMeter;
            for (int v = 0; v < _vertexCount; v++)
            {
                positions[v] = new XYZ(
                    chunk.Positions[v * 3] * scale,
                    chunk.Positions[v * 3 + 1] * scale,
                    chunk.Positions[v * 3 + 2] * scale);
            }

            _litFormat = new VertexFormat(VertexFormatBits.PositionNormalColored);
            _litEffect = new EffectInstance(VertexFormatBits.PositionNormalColored);
            _flatFormat = new VertexFormat(VertexFormatBits.PositionColored);
            _flatEffect = new EffectInstance(VertexFormatBits.PositionColored);

            _litVertices = BuildLitVertices(chunk, positions, faceColor);
            _flatFaceVertices = BuildFlatVertices(positions, faceColor);
            _triangleIndices = BuildTriangleIndices(chunk);

            // Feature edges are computed per chunk, in meters: a mesh split
            // into several chunks also shows lines along the seams, a small
            // price that only very large meshes pay.
            int[] lines = MeshEdges.FeatureLines(chunk.Positions, chunk.Indices, MeshEdges.DefaultAngleDegrees);
            _lineCount = lines.Length / 2;
            if (_lineCount > 0)
            {
                _edgeVertices = BuildFlatVertices(positions, edgeColor);
                _lineIndices = BuildLineIndices(lines);
            }
        }

        private VertexBuffer BuildLitVertices(MeshChunk chunk, XYZ[] positions, ColorWithTransparency color)
        {
            int floats = VertexPositionNormalColored.GetSizeInFloats() * _vertexCount;
            VertexBuffer buffer = new VertexBuffer(floats);
            bool mapped = false;
            try
            {
                buffer.Map(floats);
                mapped = true;
                VertexStreamPositionNormalColored stream = buffer.GetVertexStreamPositionNormalColored();
                for (int v = 0; v < _vertexCount; v++)
                {
                    XYZ normal = new XYZ(
                        chunk.Normals[v * 3],
                        chunk.Normals[v * 3 + 1],
                        chunk.Normals[v * 3 + 2]);
                    stream.AddVertex(new VertexPositionNormalColored(positions[v], normal, color));
                }
                buffer.Unmap();
                mapped = false;
            }
            finally
            {
                if (mapped) { try { buffer.Unmap(); } catch (Exception) { } }
            }
            return buffer;
        }

        private VertexBuffer BuildFlatVertices(XYZ[] positions, ColorWithTransparency color)
        {
            int floats = VertexPositionColored.GetSizeInFloats() * _vertexCount;
            VertexBuffer buffer = new VertexBuffer(floats);
            bool mapped = false;
            try
            {
                buffer.Map(floats);
                mapped = true;
                VertexStreamPositionColored stream = buffer.GetVertexStreamPositionColored();
                for (int v = 0; v < _vertexCount; v++)
                {
                    stream.AddVertex(new VertexPositionColored(positions[v], color));
                }
                buffer.Unmap();
                mapped = false;
            }
            finally
            {
                if (mapped) { try { buffer.Unmap(); } catch (Exception) { } }
            }
            return buffer;
        }

        private IndexBuffer BuildTriangleIndices(MeshChunk chunk)
        {
            int shorts = IndexTriangle.GetSizeInShortInts() * _triangleCount;
            IndexBuffer buffer = new IndexBuffer(shorts);
            bool mapped = false;
            try
            {
                buffer.Map(shorts);
                mapped = true;
                IndexStreamTriangle stream = buffer.GetIndexStreamTriangle();
                for (int t = 0; t < _triangleCount; t++)
                {
                    stream.AddTriangle(new IndexTriangle(
                        chunk.Indices[t * 3],
                        chunk.Indices[t * 3 + 1],
                        chunk.Indices[t * 3 + 2]));
                }
                buffer.Unmap();
                mapped = false;
            }
            finally
            {
                if (mapped) { try { buffer.Unmap(); } catch (Exception) { } }
            }
            return buffer;
        }

        private IndexBuffer BuildLineIndices(int[] lines)
        {
            int shorts = IndexLine.GetSizeInShortInts() * _lineCount;
            IndexBuffer buffer = new IndexBuffer(shorts);
            bool mapped = false;
            try
            {
                buffer.Map(shorts);
                mapped = true;
                IndexStreamLine stream = buffer.GetIndexStreamLine();
                for (int l = 0; l < _lineCount; l++)
                {
                    stream.AddLine(new IndexLine(lines[l * 2], lines[l * 2 + 1]));
                }
                buffer.Unmap();
                mapped = false;
            }
            finally
            {
                if (mapped) { try { buffer.Unmap(); } catch (Exception) { } }
            }
            return buffer;
        }

        public void Draw(DisplayStyle style)
        {
            if (style == DisplayStyle.HLR)
            {
                DrawContext.FlushBuffer(
                    _flatFaceVertices, _vertexCount,
                    _triangleIndices, _triangleCount * 3,
                    _flatFormat, _flatEffect,
                    PrimitiveType.TriangleList,
                    0, _triangleCount);
            }
            else if (style != DisplayStyle.Wireframe)
            {
                DrawContext.FlushBuffer(
                    _litVertices, _vertexCount,
                    _triangleIndices, _triangleCount * 3,
                    _litFormat, _litEffect,
                    PrimitiveType.TriangleList,
                    0, _triangleCount);
            }

            if (_lineCount > 0)
            {
                DrawContext.FlushBuffer(
                    _edgeVertices, _vertexCount,
                    _lineIndices, _lineCount * 2,
                    _flatFormat, _flatEffect,
                    PrimitiveType.LineList,
                    0, _lineCount);
            }
        }
    }
}
