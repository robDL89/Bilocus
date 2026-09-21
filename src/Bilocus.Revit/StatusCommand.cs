// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bilocus.Revit.Net;

namespace Bilocus.Revit
{
    [Transaction(TransactionMode.Manual)]
    public sealed class StatusCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Current == null)
            {
                TaskDialog.Show("Bilocus", "Add-in not initialized.");
                return Result.Cancelled;
            }

            if (App.Current.BridgeServer == null)
            {
                string reason = App.Current.StartupError;
                TaskDialog.Show("Bilocus", string.IsNullOrEmpty(reason)
                    ? "Server not initialized."
                    : "Server not initialized.\n" + reason);
                return Result.Cancelled;
            }

            string lastError = App.Current.MessageHandler.LastError;
            string text = string.Format(
                "Status: {0}\nLast message: {1}",
                App.Current.BridgeServer.Status,
                App.Current.MessageHandler.LastMessage);

            if (!string.IsNullOrEmpty(lastError))
            {
                text = text + "\nLast handler error: " + lastError;
            }

            // CONTENT errors: the router replies with an error frame and
            // continues, so nothing would be visible from the Revit side.
            // Without this line, a Blender sending malformed headers would
            // simply look like it is not working.
            MessageRouter router = App.Current.MessageHandler.Router;
            if (router.ContentErrorCount > 0)
            {
                text = text + string.Format(
                    "\nRejected messages: {0}, last: {1}",
                    router.ContentErrorCount, router.LastContentError);
            }

            // The cost of UpdateAllOpenViews on the live path. Meant to be read
            // while Blender is dragging an object: it is the number Task 8
            // will use to decide whether to limit itself to the active view.
            MessageHandler handler = App.Current.MessageHandler;
            if (handler.InvalidateCount > 0)
            {
                text = text + string.Format(
                    "\nView invalidation: {0} times, last {1:F1} ms, average {2:F1} ms, max {3:F1} ms",
                    handler.InvalidateCount,
                    handler.LastInvalidateMs,
                    handler.AverageInvalidateMs,
                    handler.MaxInvalidateMs);
            }

            // What the bridge has written into the document. From this phase
            // on it is no longer read-only on the model, and without this line
            // the only way to know would be to go count the lines.
            if (handler.ProxyCreatedTotal > 0 || !string.IsNullOrEmpty(handler.LastProxyResult))
            {
                text = text + string.Format(
                    "\nProxies created this session: {0}\n{1}",
                    handler.ProxyCreatedTotal, handler.LastProxyResult);
            }

            // The same for bake: the bake runs on an ExternalEvent with no
            // dialogs, and the outcome goes to Blender. Whoever is at the
            // Revit machine reads it here, including the skipped faces that
            // do not travel on bake_result.
            if (handler.BakeCreatedTotal > 0 || handler.FamilyBakeCreatedTotal > 0
                || !string.IsNullOrEmpty(handler.LastBakeResult))
            {
                text = text + string.Format(
                    "\nDirectShapes created this session: {0}, families created: {1}\n{2}",
                    handler.BakeCreatedTotal, handler.FamilyBakeCreatedTotal, handler.LastBakeResult);
            }

            // Batches opened by bake_begin and never closed. Discarding them
            // costs nothing, nothing had been written: but a rising number is
            // the only sign of a Blender that loses the connection mid-bake.
            if (router.AbandonedBakeBatches > 0)
            {
                text = text + string.Format(
                    "\nBakes abandoned before bake_end (nothing written): {0}",
                    router.AbandonedBakeBatches);
            }

            // StartupError must be shown even when the socket started: preview
            // registration can fail on its own, and without this branch the
            // reason would stay written in a field nobody reads.
            if (!string.IsNullOrEmpty(App.Current.StartupError))
            {
                text = text + "\nStartup error: " + App.Current.StartupError;
            }

            if (App.Current.PreviewServer == null)
            {
                text = text + "\nPreview: NOT REGISTERED";
            }
            else
            {
                text = text + string.Format(
                    "\nPreview: {0} objects, {1} triangles",
                    App.Current.Store.Objects.Count,
                    App.Current.Store.TotalTriangles);

                // If the geometry does not show up, the reason is here:
                // RenderScene swallows exceptions on purpose, because letting
                // them out during an orbit would pop a dialog dozens of times
                // a second. But swallowing them without showing them anywhere
                // means nothing can be diagnosed.
                if (!string.IsNullOrEmpty(App.Current.PreviewServer.LastError))
                {
                    text = text + "\nLast draw error: "
                        + App.Current.PreviewServer.LastError;
                }
            }

            TaskDialog.Show("Bilocus", text);
            return Result.Succeeded;
        }
    }
}
