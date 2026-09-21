// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.IO;
using System.Text;
using Bilocus.Protocol;
using Xunit;

namespace Bilocus.Protocol.Tests
{
    // Stream that returns one byte at a time: reproduces TCP's partial
    // reads. It is the case that breaks any codec written with a single
    // Read().
    public class DripStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        public DripStream(byte[] data) { _data = data; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length) return 0;
            buffer[offset] = _data[_position];
            _position++;
            return 1;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _data.Length; } }
        public override long Position { get { return _position; } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }

    public class FrameCodecTests
    {
        [Fact]
        public void RoundTrip_PreservesHeaderAndPayload()
        {
            Frame original = new Frame("{\"type\":\"hello\"}", new byte[] { 1, 2, 3, 4, 5 });
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, original);
            buffer.Position = 0;

            Frame decoded = FrameCodec.Read(buffer);

            Assert.Equal(original.Header, decoded.Header);
            Assert.Equal(original.Payload, decoded.Payload);
        }

        [Fact]
        public void RoundTrip_EmptyPayload()
        {
            Frame original = new Frame("{\"type\":\"clear\"}", null);
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, original);
            buffer.Position = 0;

            Frame decoded = FrameCodec.Read(buffer);

            Assert.Equal("{\"type\":\"clear\"}", decoded.Header);
            Assert.Empty(decoded.Payload);
        }

        [Fact]
        public void Read_HandlesPartialReads()
        {
            Frame original = new Frame("{\"type\":\"geometry\"}", new byte[] { 9, 8, 7, 6 });
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, original);

            Frame decoded = FrameCodec.Read(new DripStream(buffer.ToArray()));

            Assert.Equal(original.Header, decoded.Header);
            Assert.Equal(original.Payload, decoded.Payload);
        }

        [Fact]
        public void Read_TwoFramesInSequence()
        {
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame("{\"n\":1}", new byte[] { 1 }));
            FrameCodec.Write(buffer, new Frame("{\"n\":2}", new byte[] { 2, 2 }));
            buffer.Position = 0;

            Frame first = FrameCodec.Read(buffer);
            Frame second = FrameCodec.Read(buffer);

            Assert.Equal("{\"n\":1}", first.Header);
            Assert.Equal("{\"n\":2}", second.Header);
            Assert.Equal(2, second.Payload.Length);
        }

        [Fact]
        public void Write_ProducesLittleEndianLengthPrefixes()
        {
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame("ab", new byte[] { 7 }));
            byte[] bytes = buffer.ToArray();

            // 2 bytes of header, then "ab", then 1 byte of payload, then 0x07
            Assert.Equal(new byte[] { 2, 0, 0, 0, 0x61, 0x62, 1, 0, 0, 0, 7 }, bytes);
        }

        [Fact]
        public void Read_TruncatedStream_Throws()
        {
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame("{\"type\":\"hello\"}", new byte[] { 1, 2, 3 }));
            byte[] truncated = new byte[buffer.Length - 2];
            Array.Copy(buffer.ToArray(), truncated, truncated.Length);

            Assert.Throws<EndOfStreamException>(() => FrameCodec.Read(new MemoryStream(truncated)));
        }

        [Fact]
        public void Read_HeaderLongerThanLimit_Throws()
        {
            byte[] bogus = BitConverter.GetBytes((uint)(BridgeConstants.MaxHeaderBytes + 1));
            Assert.Throws<InvalidDataException>(() => FrameCodec.Read(new MemoryStream(bogus)));
        }

        [Fact]
        public void RoundTrip_NonAsciiHeaderSurvivesAsUtf8()
        {
            string header = "{\"name\":\"cubo\\u00e8\"}";
            Frame original = new Frame(header, null);
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, original);
            buffer.Position = 0;

            Assert.Equal(header, FrameCodec.Read(buffer).Header);
        }

        // Fix 1: encoder and decoder must agree on what is valid. A frame
        // over the limit must die at the sender, not on arrival.
        [Fact]
        public void Write_HeaderLongerThanLimit_Throws()
        {
            string oversized = new string('x', BridgeConstants.MaxHeaderBytes + 1);
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => FrameCodec.Write(new MemoryStream(), new Frame(oversized, null)));
            Assert.Contains("header", error.Message);
        }

        [Fact]
        public void Write_PayloadLongerThanLimit_Throws()
        {
            byte[] oversized = new byte[BridgeConstants.MaxPayloadBytes + 1];
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => FrameCodec.Write(new MemoryStream(), new Frame("{}", oversized)));
            Assert.Contains("payload", error.Message);
        }

        // M4: the payload branch on the read side is the one that
        // desynchronizes the stream, and it was not covered by either
        // suite.
        [Fact]
        public void Read_PayloadLongerThanLimit_Throws()
        {
            MemoryStream buffer = new MemoryStream();
            byte[] header = Encoding.UTF8.GetBytes("{}");
            buffer.Write(BitConverter.GetBytes((uint)header.Length), 0, 4);
            buffer.Write(header, 0, header.Length);
            buffer.Write(BitConverter.GetBytes((uint)(BridgeConstants.MaxPayloadBytes + 1)), 0, 4);
            buffer.Position = 0;

            Assert.Throws<InvalidDataException>(() => FrameCodec.Read(buffer));
        }

        // Fix 3: clean closure at a frame boundary, the EXPECTED case when
        // Blender is restarted during a Revit session.
        [Fact]
        public void Read_EmptyStream_ReturnsNull()
        {
            Assert.Null(FrameCodec.Read(new MemoryStream(new byte[0])));
        }

        [Fact]
        public void Read_FrameThenCleanClose_ReturnsFrameThenNull()
        {
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame("{\"n\":1}", new byte[] { 1 }));
            buffer.Position = 0;

            Assert.NotNull(FrameCodec.Read(buffer));
            Assert.Null(FrameCodec.Read(buffer));
        }

        [Fact]
        public void Read_TruncatedLengthPrefix_Throws()
        {
            // fewer than 4 bytes: the peer closed mid-prefix, not at a frame
            // boundary. It is a truncation, not a clean closure.
            Assert.Throws<EndOfStreamException>(
                () => FrameCodec.Read(new MemoryStream(new byte[] { 1, 0 })));
        }

        // Fix 2a: invalid UTF-8 must be fatal, not silently replaced with
        // U+FFFD.
        [Fact]
        public void Read_InvalidUtf8Header_Throws()
        {
            MemoryStream buffer = new MemoryStream();
            buffer.Write(new byte[] { 2, 0, 0, 0 }, 0, 4);
            buffer.Write(new byte[] { 0xC3, 0x28 }, 0, 2); // invalid continuation
            buffer.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
            buffer.Position = 0;

            Assert.Throws<DecoderFallbackException>(() => FrameCodec.Read(buffer));
        }

        // M5: the C# codec returns the header as a raw string and does NOT
        // validate it as JSON. These two cases pin down that contract, which
        // the Python side mirrors with decode_frame.
        [Fact]
        public void Read_EmptyHeader_ReturnsEmptyStringWithoutError()
        {
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame("", null));
            buffer.Position = 0;

            Frame decoded = FrameCodec.Read(buffer);
            Assert.Equal("", decoded.Header);
            Assert.Empty(decoded.Payload);
        }

        [Fact]
        public void Read_MalformedJsonHeader_ReturnsRawStringWithoutError()
        {
            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame("{non json", null));
            buffer.Position = 0;

            Assert.Equal("{non json", FrameCodec.Read(buffer).Header);
        }
    }
}
