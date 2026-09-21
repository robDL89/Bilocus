// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using Bilocus.Protocol;

namespace Bilocus.Revit.Net
{
    // Queue shared between the socket thread and Revit's main thread.
    // The socket thread writes, the ExternalEvent reads. No other rule.
    public sealed class MessageQueue
    {
        private readonly Queue<Frame> _items = new Queue<Frame>();
        private readonly object _gate = new object();

        public void Enqueue(Frame frame)
        {
            lock (_gate) { _items.Enqueue(frame); }
        }

        public List<Frame> DrainAll()
        {
            List<Frame> drained = new List<Frame>();
            lock (_gate)
            {
                while (_items.Count > 0) { drained.Add(_items.Dequeue()); }
            }
            return drained;
        }
    }
}
