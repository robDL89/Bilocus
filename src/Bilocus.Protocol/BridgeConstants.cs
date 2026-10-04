// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

namespace Bilocus.Protocol
{
    public static class BridgeConstants
    {
        public const int ProtocolVersion = 2;
        public const string DefaultHost = "127.0.0.1";

        // 9876 is taken by blender-mcp: do not reuse it.
        public const int DefaultPort = 9877;

        public const double MetersPerFoot = 0.3048;
        public const double FeetPerMeter = 1.0 / MetersPerFoot;

        public const int MaxHeaderBytes = 1048576;
        public const int MaxPayloadBytes = 268435456;
    }
}
