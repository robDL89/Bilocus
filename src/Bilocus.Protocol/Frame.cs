// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Protocol
{
    public sealed class Frame
    {
        public string Header { get; private set; }

        // WARNING: exposes the internal array, the codec does NOT make a
        // copy of it. Whoever reads a Frame must not modify its payload:
        // the mutation would be invisible to the rest of the system.
        // The choice is deliberate. In Phase A the geometry payloads are
        // megabyte-sized float arrays that pass through the codec exactly
        // once, in a linear producer-consumer flow: a defensive copy would
        // cost a memcpy on every frame, and with the live transform at 30 Hz
        // it would be felt. If one day a Frame needed to be cached or
        // shared across threads, then a ReadOnlyMemory<byte> is what is
        // needed.
        public byte[] Payload { get; private set; }

        public Frame(string header, byte[] payload)
        {
            if (header == null) throw new ArgumentNullException("header");
            Header = header;
            Payload = payload == null ? new byte[0] : payload;
        }
    }
}
