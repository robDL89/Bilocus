// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Bilocus.Protocol;
using Xunit;

namespace Bilocus.Protocol.Tests
{
    public class GoldenVectorTests
    {
        private static string VectorPath()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8; i++)
            {
                string candidate = Path.Combine(dir, "tests", "vectors", "frames.json");
                if (File.Exists(candidate)) return candidate;
                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            throw new FileNotFoundException("tests/vectors/frames.json not found walking up from " + AppContext.BaseDirectory);
        }

        private static JsonDocument LoadVectors()
        {
            return JsonDocument.Parse(File.ReadAllText(VectorPath()));
        }

        // MemberData instead of a loop inside a [Fact]: every vector becomes
        // a test of its own, the name shows up in the report and a failure
        // does not hide the vectors after it.
        public static IEnumerable<object[]> Vectors()
        {
            using (JsonDocument doc = LoadVectors())
            {
                List<object[]> rows = new List<object[]>();
                foreach (JsonElement vector in doc.RootElement.GetProperty("vectors").EnumerateArray())
                {
                    rows.Add(new object[]
                    {
                        vector.GetProperty("name").GetString(),
                        vector.GetProperty("header").GetString(),
                        vector.GetProperty("payload_hex").GetString(),
                        vector.GetProperty("frame_hex").GetString()
                    });
                }
                return rows;
            }
        }

        private static byte[] FromHex(string hex)
        {
            byte[] result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return result;
        }

        private static string ToHex(byte[] data)
        {
            char[] digits = "0123456789abcdef".ToCharArray();
            char[] output = new char[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                output[i * 2] = digits[data[i] >> 4];
                output[i * 2 + 1] = digits[data[i] & 0x0F];
            }
            return new string(output);
        }

        [Theory]
        [MemberData(nameof(Vectors))]
        public void EncodedFrameMatchesGoldenVector(string name, string header, string payloadHex, string expectedFrameHex)
        {
            byte[] payload = FromHex(payloadHex);

            MemoryStream buffer = new MemoryStream();
            FrameCodec.Write(buffer, new Frame(header, payload));

            Assert.Equal(expectedFrameHex, ToHex(buffer.ToArray()));

            buffer.Position = 0;
            Frame decoded = FrameCodec.Read(buffer);
            Assert.NotNull(decoded);
            Assert.Equal(header, decoded.Header);
            Assert.Equal(payload, decoded.Payload);
            Assert.False(string.IsNullOrEmpty(name));
        }

        // Without this assertion a Theory over an empty array would pass
        // vacuously, and the vector file could become empty without
        // anything saying so.
        [Fact]
        public void VectorFileIsNotEmpty()
        {
            using (JsonDocument doc = LoadVectors())
            {
                int count = doc.RootElement.GetProperty("vectors").GetArrayLength();
                Assert.True(count >= 5, string.Format("expected at least 5 golden vectors, found {0}", count));
            }
        }

        // The constants are as much a wire contract as the framing: if
        // someone changes them on one side only, the other side's tests
        // must fail.
        [Fact]
        public void ConstantsMatchSharedContract()
        {
            using (JsonDocument doc = LoadVectors())
            {
                JsonElement constants = doc.RootElement.GetProperty("constants");
                Assert.Equal(BridgeConstants.ProtocolVersion, constants.GetProperty("protocol_version").GetInt32());
                Assert.Equal(BridgeConstants.DefaultPort, constants.GetProperty("default_port").GetInt32());
                Assert.Equal(BridgeConstants.DefaultHost, constants.GetProperty("default_host").GetString());
                Assert.Equal(BridgeConstants.MetersPerFoot, constants.GetProperty("meters_per_foot").GetDouble());
                Assert.Equal(BridgeConstants.MaxHeaderBytes, constants.GetProperty("max_header_bytes").GetInt32());
                Assert.Equal(BridgeConstants.MaxPayloadBytes, constants.GetProperty("max_payload_bytes").GetInt32());
            }
        }
    }
}
