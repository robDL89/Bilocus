// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.IO;
using System.Text;

namespace Bilocus.Protocol
{
    // Frame format, little-endian:
    //   [uint32 header length][UTF-8 header][uint32 payload length][payload]
    // The length prefixes are written by hand, byte by byte, instead of
    // with BitConverter, so the format stays little-endian by definition
    // and not by luck of the architecture it runs on.
    public static class FrameCodec
    {
        // throwOnInvalidBytes: the static Encoding.UTF8 silently replaces
        // corrupted bytes with U+FFFD, and on a desynchronized channel it
        // would produce plausible but false headers instead of flagging the
        // problem
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

        // WARNING: Write is NOT atomic. It issues four consecutive writes
        // to the stream and does not own the stream, so it cannot guarantee
        // anything about interleaving. Two threads writing in parallel
        // produce a scrambled frame and permanently desynchronize the
        // receiver. Whoever owns the stream must serialize the writes with
        // a lock, and EVERY write path must go through that same lock:
        // error and shutdown paths included, which are the first to be
        // forgotten.
        //
        // The lengths are validated here, before touching the stream:
        // encoder and decoder must agree on what a valid frame is. A frame
        // over the limit written silently would only be rejected on
        // arrival, and by then the receiver's stream is already lost (see
        // the comment on Read).
        public static void Write(Stream stream, Frame frame)
        {
            if (stream == null) throw new ArgumentNullException("stream");
            if (frame == null) throw new ArgumentNullException("frame");

            byte[] header = Utf8.GetBytes(frame.Header);
            if (header.Length > BridgeConstants.MaxHeaderBytes)
            {
                throw new ArgumentException(string.Format(
                    "header of {0} bytes over the limit of {1} bytes, exceeding by {2} bytes",
                    header.Length,
                    BridgeConstants.MaxHeaderBytes,
                    header.Length - BridgeConstants.MaxHeaderBytes), "frame");
            }
            if (frame.Payload.Length > BridgeConstants.MaxPayloadBytes)
            {
                throw new ArgumentException(string.Format(
                    "payload of {0} bytes over the limit of {1} bytes, exceeding by {2} bytes",
                    frame.Payload.Length,
                    BridgeConstants.MaxPayloadBytes,
                    frame.Payload.Length - BridgeConstants.MaxPayloadBytes), "frame");
            }

            WriteUInt32(stream, (uint)header.Length);
            stream.Write(header, 0, header.Length);
            WriteUInt32(stream, (uint)frame.Payload.Length);
            if (frame.Payload.Length > 0)
            {
                stream.Write(frame.Payload, 0, frame.Payload.Length);
            }
        }

        // Return contract and error contract, for whoever builds the
        // receive loop:
        //
        // - returns null when the peer closed EXACTLY at a frame boundary,
        //   i.e. the very first read of the header's length prefix returns
        //   zero bytes. This is the clean disconnection, the expected case:
        //   Blender can be restarted multiple times during a Revit session.
        //   It is not an error, it should be treated as "the client is
        //   gone, go back to listening".
        //
        // - raises EndOfStreamException if the closure happens mid-frame.
        //
        // - raises InvalidDataException on a length over the limits, and
        //   DecoderFallbackException on a header that is not valid UTF-8.
        //   BOTH ARE TERMINAL FOR THE CONNECTION. When the payload length
        //   is rejected, the payload bytes are still in the socket: the
        //   stream is permanently desynchronized and there is no way to
        //   realign it, because the protocol has no delimiters. The only
        //   correct response is to close the socket. A catch/log/continue
        //   would read garbage forever.
        public static Frame Read(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException("stream");

            byte[] headerPrefix = ReadLengthPrefixOrNull(stream);
            if (headerPrefix == null) return null;

            int headerLength = CheckLength(
                DecodeUInt32(headerPrefix), BridgeConstants.MaxHeaderBytes, "header");
            byte[] header = ReadExactly(stream, headerLength);

            int payloadLength = CheckLength(
                DecodeUInt32(ReadExactly(stream, 4)), BridgeConstants.MaxPayloadBytes, "payload");
            byte[] payload = ReadExactly(stream, payloadLength);

            return new Frame(Utf8.GetString(header), payload);
        }

        private static void WriteUInt32(Stream stream, uint value)
        {
            byte[] raw = new byte[4];
            raw[0] = (byte)(value & 0xFF);
            raw[1] = (byte)((value >> 8) & 0xFF);
            raw[2] = (byte)((value >> 16) & 0xFF);
            raw[3] = (byte)((value >> 24) & 0xFF);
            stream.Write(raw, 0, 4);
        }

        private static uint DecodeUInt32(byte[] raw)
        {
            return (uint)(raw[0] | (raw[1] << 8) | (raw[2] << 16) | (raw[3] << 24));
        }

        // Returns null only if the stream had already ended at the first
        // byte: it is the only point where zero bytes means "clean closure"
        // instead of "truncated frame".
        private static byte[] ReadLengthPrefixOrNull(Stream stream)
        {
            byte[] buffer = new byte[4];
            int offset = 0;
            while (offset < 4)
            {
                int read = stream.Read(buffer, offset, 4 - offset);
                if (read <= 0)
                {
                    if (offset == 0) return null;
                    throw new EndOfStreamException(string.Format(
                        "stream closed after {0} of 4 bytes of the header prefix", offset));
                }
                offset += read;
            }
            return buffer;
        }

        private static int CheckLength(uint value, int max, string what)
        {
            if (value > (uint)max)
            {
                throw new InvalidDataException(string.Format(
                    "{0} length is {1}, over the limit of {2}", what, value, max));
            }
            return (int)value;
        }

        private static byte[] ReadExactly(Stream stream, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException(string.Format(
                        "stream closed after {0} of {1} bytes", offset, count));
                }
                offset += read;
            }
            return buffer;
        }
    }
}
