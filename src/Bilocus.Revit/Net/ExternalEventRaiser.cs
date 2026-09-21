// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Autodesk.Revit.UI;

namespace Bilocus.Revit.Net
{
    // Production adapter between BridgeServer and Revit.
    // ExternalEvent.Raise() is the only Revit API method designed to be
    // called from a thread other than the main thread: it exists exactly for
    // this. All the rest of the API stays off-limits to the socket thread.
    public sealed class ExternalEventRaiser : IEventRaiser
    {
        private readonly ExternalEvent _externalEvent;

        public ExternalEventRaiser(ExternalEvent externalEvent)
        {
            if (externalEvent == null) throw new ArgumentNullException("externalEvent");
            _externalEvent = externalEvent;
        }

        public void Raise()
        {
            _externalEvent.Raise();
        }
    }
}
