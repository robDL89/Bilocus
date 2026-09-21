// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

namespace Bilocus.Revit.Net
{
    // The only point of contact between the socket thread and Revit.
    //
    // It exists for two reasons, not for a taste for abstraction:
    //
    // 1. With this interface, BridgeServer no longer compiles against the
    //    Revit API. The rule "the socket thread never touches the Revit API"
    //    stops being a rule to remember on every reread and becomes a
    //    compiler constraint: the test project compiles BridgeServer.cs with
    //    no reference to RevitAPI at all, so any API call added in there
    //    breaks the build.
    //
    // 2. ExternalEvent is not instantiable outside Revit, and without a way
    //    to substitute it the server would only be verifiable by opening
    //    Revit by hand.
    //
    // The only production implementation is ExternalEventRaiser, which
    // forwards to ExternalEvent.Raise(): the only Revit API method designed
    // to be called from any thread.
    public interface IEventRaiser
    {
        void Raise();
    }
}
