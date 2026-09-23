// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.UI;
using Bilocus.Protocol;
using Bilocus.Revit.Bake;
using Bilocus.Revit.Preview;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Net
{
    // Runs on Revit's main thread: here, and only here, is the Revit API usable.
    //
    // It is a shell, on purpose. All the dispatch logic lives in MessageRouter,
    // which does not touch the Revit API and is therefore verifiable without
    // opening Revit. What remains here is exactly what cannot be moved out:
    // reading the document's version and title, and invalidating the views.
    public sealed class MessageHandler : IExternalEventHandler
    {
        private readonly MessageQueue _queue;
        private readonly GeometryStore _store;
        private readonly MessageRouter _router;

        private long _lastSeenRevision;

        public BridgeServer Server;
        public string LastMessage = "no message";
        public string LastError = "";

        // Cost of UpdateAllOpenViews, see InvalidateViews. These are fields
        // and not a log because they need to be readable from the Status
        // button while the bridge is running, without external tools.
        public int InvalidateCount;
        public double LastInvalidateMs;
        public double MaxInvalidateMs;
        public double TotalInvalidateMs;

        // Outcome of the last proxy creation and total lines created in this
        // session. The bridge writes into the document from this phase on:
        // the Status button is the only place where you can read what it
        // wrote, without having to go count the lines in the model.
        public string LastProxyResult = "";
        public int ProxyCreatedTotal;

        // Same for Phase B bake: the summary of the last bake or the last
        // removal, and the NEW DirectShapes written in this session. New
        // means created or recreated because of a category change; updated
        // ones keep their ElementId and are not one more element in the
        // document.
        public string LastBakeResult = "";
        public int BakeCreatedTotal;

        // Phase B2: the NEW families loaded by the family bake in this
        // session. Kept separate from DirectShapes because they are elements
        // of a different nature, and summing them would give a number that
        // does not correspond to anything in the model.
        public int FamilyBakeCreatedTotal;

        public MessageRouter Router { get { return _router; } }

        public double AverageInvalidateMs
        {
            get { return InvalidateCount == 0 ? 0 : TotalInvalidateMs / InvalidateCount; }
        }

        public MessageHandler(MessageQueue queue, GeometryStore store)
        {
            if (queue == null) throw new ArgumentNullException("queue");
            if (store == null) throw new ArgumentNullException("store");
            _queue = queue;
            _store = store;
            _router = new MessageRouter(store);
        }

        public void Execute(UIApplication app)
        {
            // An exception escaping an IExternalEventHandler is handled by
            // Revit in an unspecified way and can even reach a crash: the
            // handler keeps everything to itself and writes it into a state
            // readable from the Status button, as already done in OnStartup.
            try
            {
                Dispatch(app);
            }
            catch (Exception ex)
            {
                LastError = string.Format("{0}: {1}", ex.GetType().Name, ex.Message);
            }
        }

        private void Dispatch(UIApplication app)
        {
            HostInfo host = ReadHostInfo(app);
            List<Frame> frames = _queue.DrainAll();

            foreach (Frame frame in frames)
            {
                LastMessage = frame.Header;
                try
                {
                    _router.Handle(frame, SendToClient, host);
                }
                catch (Exception ex)
                {
                    // The router handles all content errors by itself
                    // (DESIGN 5.1): if something reaches this point it is a
                    // defect of ours, not of the message. It is still isolated
                    // to the single frame, otherwise a bug on one object would
                    // silently lose all the others already drained from the
                    // queue, and view invalidation would be skipped too.
                    LastError = string.Format(
                        "dispatch failed: {0}: {1}", ex.GetType().Name, ex.Message);
                }
            }

            // After the drain, not inside it: at this point dispatch of all
            // frames is done, so two proxy_edges arrived in the same round
            // have already been merged into a single request per obj_id. And
            // this is the only point where a transaction can be opened,
            // because it is Revit's main thread in a valid API context.
            RunPendingProxies(app);

            // After the proxies and for the same reason: here bake_end has
            // already closed the batches that arrived in this drain, and we
            // are on the main thread in a valid API context.
            RunPendingBakes(app);

            InvalidateViewsIfChanged(app);
        }

        // Proxy creation: the first point where the bridge writes into the
        // document starting from a network message.
        //
        // The separation of the two error kinds shows here. A malformed
        // payload never reaches this point: it has already been exhausted in
        // the router, with an error frame and the bridge moving on. What can
        // fail here is the write itself, and these are things that concern
        // Revit and the document - read-only document, family instead of
        // project, transaction already open, rejected commit. They must be
        // reported back to whoever pressed the button, who is on the other
        // side of the wire, and left readable in the Status button for
        // whoever is at the Revit machine.
        //
        // No TaskDialog, on purpose: this method runs on an ExternalEvent
        // triggered by the network, and a modal dialog popping up while the
        // user is in the middle of a Revit command is worse than the problem
        // it would report.
        private void RunPendingProxies(UIApplication app)
        {
            if (!_router.HasPendingProxies) { return; }

            List<ProxyEdgeRequest> requests = _router.TakePendingProxies();

            // Document is fully qualified instead of using an Autodesk.Revit.DB
            // using: that namespace exposes a Frame type that collides with
            // the protocol's Frame, which this file sends over the socket a
            // few lines below.
            UIDocument uidoc = app.ActiveUIDocument;
            Autodesk.Revit.DB.Document document = uidoc == null ? null : uidoc.Document;

            foreach (ProxyEdgeRequest request in requests)
            {
                ProxyBuildResult result;
                try
                {
                    result = ProxyBuilder.Build(document, request);
                }
                catch (Exception ex)
                {
                    // ProxyBuilder already keeps to itself everything it
                    // knows how to handle: if something reaches this point it
                    // is a defect of ours. Isolated to the single request,
                    // otherwise one sick obj_id would silently lose the
                    // others already taken from the list.
                    result = ProxyBuildResult.Failed(
                        request, ex.GetType().Name + ": " + ex.Message);
                }

                LastProxyResult = result.BuildSummaryText();

                // The outcome ALWAYS goes back to Blender, whether it
                // succeeded or not. Without this frame, whoever pressed the
                // button - on the other side of the wire - would only
                // receive something when it goes wrong: success would be
                // silence, indistinguishable from a message lost along the
                // way. There is no TaskDialog to make up for it, on purpose,
                // for the reason written above this method.
                SendToClient(new Frame(MessageRouter.BuildProxyResult(result), null));

                // Only if the transaction went through: on a failure the
                // rollback has already taken away the counted curves, and
                // summing them here would make the Status button say there
                // is stuff in the document that is not there.
                if (result.Succeeded)
                {
                    ProxyCreatedTotal += result.CreatedCount;
                }
                else
                {
                    LastError = "proxy creation failed: " + result.FailureReason;
                    SendToClient(new Frame(
                        MessageRouter.BuildError(
                            "proxy '" + request.Name + "': " + result.FailureReason),
                        null));
                }
            }
        }

        // Phase B bake and its removal, following the RunPendingProxies
        // pattern: takes what the router set aside, writes, and sends the
        // outcome to whoever pressed the button in Blender.
        //
        // REMOVALS FIRST, THEN BAKES. The router keeps them in two lists and
        // the arrival order between the two is lost (see MessageRouter): the
        // choice is made here. A bake and a removal in the same drain mean
        // Revit was busy - a dialog open, a long command - while both buttons
        // were pressed in Blender. If the chosen order is the wrong one the
        // damage is not symmetric: with the removal last, a bake just
        // requested would get deleted without Revit telling anyone anything;
        // with the bake last, at worst an extra DirectShape stays in the
        // document, visible and removable with another "Remove Bake". And
        // "remove, then redo the bake" to start clean is a gesture people
        // make; the opposite is not.
        //
        // bake_result is ALWAYS sent, whether it succeeded or not, for the
        // reason written above RunPendingProxies: without it, success would
        // be silence. On a failure the error frame is also sent, as for proxies.
        private void RunPendingBakes(UIApplication app)
        {
            if (!_router.HasPendingBakeRemovals && !_router.HasPendingBakes) { return; }

            List<List<string>> removals = _router.TakePendingBakeRemovals();
            List<BakeBatch> batches = _router.TakePendingBakes();

            // Fully qualified because of the Frame collision, see
            // RunPendingProxies.
            UIDocument uidoc = app.ActiveUIDocument;
            Autodesk.Revit.DB.Document document = uidoc == null ? null : uidoc.Document;

            foreach (List<string> objectIds in removals)
            {
                BakeResult result;
                try
                {
                    result = BakeRemover.Remove(document, objectIds);
                }
                catch (Exception ex)
                {
                    // BakeRemover already keeps to itself what it knows how
                    // to handle: this is a defect of ours, isolated to the
                    // single request.
                    result = BakeResult.FailedRemoval(objectIds, ex.GetType().Name + ": " + ex.Message);
                }

                ReportBakeResult(result);
            }

            if (batches.Count == 0) { return; }

            // The planarity threshold: a tenth of the tolerance with which
            // Revit considers two vertices coincident, in meters. Deliberately
            // conservative: TessellatedFace's internal tolerance is not
            // documented, and a polygon kept whole for too little is worse
            // than one triangulated out of caution.
            // Read at every drain, like the document title: it costs nothing.
            double planarToleranceMeters =
                app.Application.VertexTolerance * BridgeConstants.MetersPerFoot * 0.1;

            foreach (BakeBatch batch in batches)
            {
                BakeResult result;
                try
                {
                    // Phase B2: the batch's target picks who writes. The
                    // router only accepts directshape and family, so the
                    // "otherwise" branch is the Phase B bake and the meaning
                    // of a bake_begin without a target.
                    result = batch.Target == BakeTarget.Family
                        ? FamilyBaker.Build(document, batch, planarToleranceMeters)
                        : BakeBuilder.Build(document, batch, planarToleranceMeters);
                }
                catch (Exception ex)
                {
                    // As above: a sick batch must not silently lose the ones
                    // already taken from the list.
                    result = BakeResult.Failed(batch, ex.GetType().Name + ": " + ex.Message);
                }

                ReportBakeResult(result);
            }
        }

        private void ReportBakeResult(BakeResult result)
        {
            bool removal = result.Action == BakeResult.ActionRemove;

            LastBakeResult = result.BuildSummaryText();
            SendToClient(new Frame(MessageRouter.BuildBakeResult(result), null));

            bool family = result.Target == BakeTarget.Family;

            if (result.Succeeded)
            {
                // Only once the write is confirmed: on a failure the rollback
                // has already taken away what would have been counted.
                if (!removal && family)
                {
                    FamilyBakeCreatedTotal += result.CreatedCount;
                }
                else if (!removal)
                {
                    BakeCreatedTotal += result.CreatedCount + result.RecreatedCount;
                }
                return;
            }

            string label = removal ? "bake removal" : (family ? "family bake" : "DirectShape bake");
            string message = result.BuildMessage();
            LastError = label + " failed: " + message;
            SendToClient(new Frame(MessageRouter.BuildError(label + ": " + message), null));
        }

        private bool SendToClient(Frame frame)
        {
            BridgeServer server = Server;
            if (server == null) { return false; }
            return server.Send(frame);
        }

        // Revit version and open document title: the only two pieces of data
        // for hello_ack that the router cannot work out on its own. Read on
        // every drain, not once at startup, because the active document changes.
        private static HostInfo ReadHostInfo(UIApplication app)
        {
            string docTitle = "";
            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc != null && uidoc.Document != null)
            {
                docTitle = uidoc.Document.Title;
            }
            return new HostInfo(app.Application.VersionNumber, docTitle);
        }

        // The only Revit API call on the live 30 Hz path, and nobody knows
        // its cost yet: it sits in its own method, with the counters next to
        // it, for two reasons.
        //
        // Measurable: LastInvalidateMs, MaxInvalidateMs and AverageInvalidateMs
        // end up in the Status button, so the cost can be read from the field
        // while Blender is dragging an object, which is the only condition
        // where the measurement means anything. A benchmark outside Revit
        // would say nothing.
        //
        // Replaceable: if the measurement justifies it, only the
        // body of this method changes (invalidate only the active view, or skip
        // drains that are too close together) without touching anything else.
        //
        // The comparison on the revision is what avoids redrawing when the
        // queue only contained messages that do not change the store, for
        // example a hello or a transform on a nonexistent object.
        private void InvalidateViewsIfChanged(UIApplication app)
        {
            if (_store.Revision == _lastSeenRevision) { return; }

            UIDocument uidoc = app.ActiveUIDocument;
            if (uidoc == null)
            {
                // No document open: there is nothing to invalidate and the
                // revision is NOT consumed, so the first drain with a
                // document open redraws whatever had arrived before.
                return;
            }

            _lastSeenRevision = _store.Revision;

            long start = Stopwatch.GetTimestamp();
            uidoc.UpdateAllOpenViews();
            double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

            InvalidateCount++;
            LastInvalidateMs = elapsedMs;
            TotalInvalidateMs += elapsedMs;
            if (elapsedMs > MaxInvalidateMs) { MaxInvalidateMs = elapsedMs; }
        }

        public string GetName() { return "Bilocus message handler"; }
    }
}
