// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.DirectContext3D;
using Bilocus.Geometry;
using Bilocus.Protocol;

namespace Bilocus.Revit.Preview
{
    // GPU buffer of a single chunk. Built once and reused across frames:
    // rebuilding them on every RenderScene would kill the frame rate.
    public sealed class GpuChunk
    {
        private VertexBuffer _vertexBuffer;
        private IndexBuffer _indexBuffer;
        private VertexFormat _format;
        private EffectInstance _effect;

        private int _vertexCount;
        private int _triangleCount;

        public bool IsValid()
        {
            return _vertexBuffer != null && _vertexBuffer.IsValid()
                && _indexBuffer != null && _indexBuffer.IsValid();
        }

        // Positions arrive in METERS and get converted to feet here, because
        // feet are Revit's internal unit and the wire speaks meters.
        public void Build(MeshChunk chunk, ColorWithTransparency color)
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

            _format = new VertexFormat(VertexFormatBits.PositionNormalColored);
            _effect = new EffectInstance(VertexFormatBits.PositionNormalColored);

            double scale = BridgeConstants.FeetPerMeter;

            int floats = VertexPositionNormalColored.GetSizeInFloats() * _vertexCount;
            _vertexBuffer = new VertexBuffer(floats);
            bool mapped = false;
            try
            {
                _vertexBuffer.Map(floats);
                mapped = true;

                VertexStreamPositionNormalColored stream =
                    _vertexBuffer.GetVertexStreamPositionNormalColored();

                for (int v = 0; v < _vertexCount; v++)
                {
                    XYZ position = new XYZ(
                        chunk.Positions[v * 3] * scale,
                        chunk.Positions[v * 3 + 1] * scale,
                        chunk.Positions[v * 3 + 2] * scale);

                    XYZ normal = new XYZ(
                        chunk.Normals[v * 3],
                        chunk.Normals[v * 3 + 1],
                        chunk.Normals[v * 3 + 2]);

                    stream.AddVertex(new VertexPositionNormalColored(position, normal, color));
                }

                _vertexBuffer.Unmap();
                mapped = false;
            }
            finally
            {
                if (mapped) { try { _vertexBuffer.Unmap(); } catch (Exception) { } }
            }

            int shorts = IndexTriangle.GetSizeInShortInts() * _triangleCount;
            _indexBuffer = new IndexBuffer(shorts);
            mapped = false;
            try
            {
                _indexBuffer.Map(shorts);
                mapped = true;

                IndexStreamTriangle stream = _indexBuffer.GetIndexStreamTriangle();
                for (int t = 0; t < _triangleCount; t++)
                {
                    stream.AddTriangle(new IndexTriangle(
                        chunk.Indices[t * 3],
                        chunk.Indices[t * 3 + 1],
                        chunk.Indices[t * 3 + 2]));
                }

                _indexBuffer.Unmap();
                mapped = false;
            }
            finally
            {
                if (mapped) { try { _indexBuffer.Unmap(); } catch (Exception) { } }
            }
        }

        public void Draw()
        {
            DrawContext.FlushBuffer(
                _vertexBuffer, _vertexCount,
                _indexBuffer, _triangleCount * 3,
                _format, _effect,
                PrimitiveType.TriangleList,
                0, _triangleCount);
        }
    }
}
