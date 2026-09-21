// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Reflection;
using Autodesk.Revit.DB.ExternalService;
using Autodesk.Revit.UI;
using Bilocus.Revit.Net;
using Bilocus.Revit.Preview;

namespace Bilocus.Revit
{
    public sealed class App : IExternalApplication
    {
        public const string TabName = "Bilocus";

        // Access point for the ribbon commands: an IExternalCommand is
        // instantiated by Revit and has no reference to the IExternalApplication.
        public static App Current;

        private GeometryStore _store;
        private PreviewServer _previewServer;
        private BridgeServer _bridgeServer;
        private MessageHandler _messageHandler;
        private ExternalEvent _externalEvent;

        // Filled in only if the server never even came into existence: in that
        // case there is no BridgeServer.Status to write the reason into.
        public string StartupError = "";

        public GeometryStore Store { get { return _store; } }
        public PreviewServer PreviewServer { get { return _previewServer; } }
        public BridgeServer BridgeServer { get { return _bridgeServer; } }
        public MessageHandler MessageHandler { get { return _messageHandler; } }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // the tab already exists: normal case if another add-in created it
            }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, "Diagnostics");
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            PushButtonData data = new PushButtonData(
                "BilocusPing",
                "Ping",
                assemblyPath,
                "Bilocus.Revit.PingCommand");
            data.ToolTip = "Check that the add-in is loaded";
            panel.AddItem(data);

            PushButtonData testMeshData = new PushButtonData(
                "BilocusTestMesh",
                "Test Mesh",
                assemblyPath,
                "Bilocus.Revit.Preview.TestMeshCommand");
            testMeshData.ToolTip =
                "Injects a synthetic cube into the preview, without going through the network";
            panel.AddItem(testMeshData);

            PushButtonData statusData = new PushButtonData(
                "BilocusStatus",
                "Status",
                assemblyPath,
                "Bilocus.Revit.StatusCommand");
            statusData.ToolTip = "Shows the TCP server status and the last message received";
            panel.AddItem(statusData);

            PushButtonData sendSelectionData = new PushButtonData(
                "BilocusSendSelection",
                "Send Selection to Blender",
                assemblyPath,
                "Bilocus.Revit.Pull.SendSelectionCommand");
            sendSelectionData.ToolTip =
                "Tessellates the current selection and sends it to the connected Blender client";
            panel.AddItem(sendSelectionData);

            // The mandatory other half of writing: from this phase on the bridge
            // leaves elements in the document, and without this button the only
            // way to remove them would be selecting them by hand in the model.
            PushButtonData removeProxyData = new PushButtonData(
                "BilocusRemoveProxy",
                "Remove Proxy",
                assemblyPath,
                "Bilocus.Revit.Proxy.RemoveProxyCommand");
            removeProxyData.ToolTip =
                "Deletes all lines created by Bilocus in this document, after confirmation";
            panel.AddItem(removeProxyData);

            // The DC3D server registration is guarded: if an exception escapes
            // OnStartup, Revit disables the whole add-in and even the ribbon
            // that would help diagnose the problem is lost. The failure reason
            // ends up in StartupError, so the Status button shows it.
            // Outside the try: the store is not just the preview's, MessageHandler
            // writes to it too. If it stayed inside, a failed DC3D registration
            // would leave it null and would also take down message dispatch,
            // which can otherwise work perfectly well on its own.
            _store = new GeometryStore();

            try
            {
                _previewServer = new PreviewServer(_store);

                MultiServerService dc3d = ExternalServiceRegistry.GetService(
                    ExternalServices.BuiltInExternalServices.DirectContext3DService) as MultiServerService;
                if (dc3d != null)
                {
                    dc3d.AddServer(_previewServer);
                    IList<Guid> active = dc3d.GetActiveServerIds();
                    active.Add(_previewServer.GetServerId());
                    dc3d.SetActiveServers(active);
                }
                else
                {
                    StartupError = "DirectContext3DService is not a MultiServerService";
                }
            }
            catch (Exception ex)
            {
                StartupError = string.Format(
                    "preview registration failed: {0}: {1}", ex.GetType().Name, ex.Message);
            }

            // Current must be assigned BEFORE starting the network: if startup
            // fails the Status button must still be able to find the instance
            // to query.
            Current = this;

            MessageQueue queue = new MessageQueue();
            _messageHandler = new MessageHandler(queue, _store);

            try
            {
                // ExternalEvent.Create has as many side effects as the listener:
                // no unguarded work in here, the ribbon is worth more than any
                // piece that might fail.
                _externalEvent = ExternalEvent.Create(_messageHandler);
                _bridgeServer = new BridgeServer(queue, new ExternalEventRaiser(_externalEvent));
                _messageHandler.Server = _bridgeServer;

                // The port might be taken by a second Revit instance or by a
                // leftover hung process. If the exception escaped from here
                // Revit would disable the whole add-in, ribbon included, and
                // the Status button - the only way to diagnose the problem -
                // would be lost too.
                _bridgeServer.Start();
            }
            catch (Exception ex)
            {
                string reason = string.Format(
                    "startup failed: {0}: {1}", ex.GetType().Name, ex.Message);
                if (_bridgeServer != null) { _bridgeServer.Status = reason; }
                else { StartupError = reason; }
            }

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            // Closing Revit must free the port and not leave threads hanging:
            // if the port is still occupied on restart the listener does not
            // start and the bridge is dead.
            if (_bridgeServer != null)
            {
                try { _bridgeServer.Stop(); }
                catch (Exception) { }
                _bridgeServer = null;
            }

            if (_externalEvent != null)
            {
                try { _externalEvent.Dispose(); }
                catch (Exception) { }
                _externalEvent = null;
            }

            Current = null;
            return Result.Succeeded;
        }
    }
}
