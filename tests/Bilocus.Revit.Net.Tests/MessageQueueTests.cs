// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using System.Threading;
using Bilocus.Protocol;
using Xunit;

namespace Bilocus.Revit.Net.Tests
{
    public class MessageQueueTests
    {
        [Fact]
        public void DrainAllEmptiesTheQueueAndKeepsTheOrder()
        {
            MessageQueue queue = new MessageQueue();
            queue.Enqueue(new Frame("a", null));
            queue.Enqueue(new Frame("b", null));

            List<Frame> first = queue.DrainAll();
            Assert.Equal(2, first.Count);
            Assert.Equal("a", first[0].Header);
            Assert.Equal("b", first[1].Header);

            Assert.Empty(queue.DrainAll());
        }

        [Fact]
        public void EnqueueFromAnotherThreadDoesNotLoseFrames()
        {
            // The real case: the socket thread enqueues while the main
            // thread drains. No frame must disappear and none must arrive twice.
            MessageQueue queue = new MessageQueue();
            const int total = 2000;

            Thread producer = new Thread(delegate ()
            {
                for (int i = 0; i < total; i++) { queue.Enqueue(new Frame(i.ToString(), null)); }
            });
            producer.Start();

            List<Frame> drained = new List<Frame>();
            while (drained.Count < total)
            {
                drained.AddRange(queue.DrainAll());
            }
            producer.Join();
            drained.AddRange(queue.DrainAll());

            Assert.Equal(total, drained.Count);
            for (int i = 0; i < total; i++)
            {
                Assert.Equal(i.ToString(), drained[i].Header);
            }
        }
    }
}
