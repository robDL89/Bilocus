// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // Exchange with Blender: the two commands that act on the model.
            RibbonPanel exchange = application.CreateRibbonPanel(TabName, "Exchange");

            PushButtonData sendSelectionData = new PushButtonData(
                "BilocusSendSelection",
                "Send Selection" + Environment.NewLine + "to Blender",
                assemblyPath,
                "Bilocus.Revit.Pull.SendSelectionCommand");
            sendSelectionData.ToolTip =
                "Sends the selected Revit elements to Blender.";
            sendSelectionData.LongDescription =
                "The selection is tessellated and arrives in Blender in the FromRevit " +
                "collection, openings included. Sending the same elements again updates " +
                "them in place. Blender must be connected: press Connect in the Bilocus " +
                "panel in Blender first.";
            Decorate(sendSelectionData, "SendSelection");
            exchange.AddItem(sendSelectionData);

            // The mandatory other half of writing: the bridge leaves proxy lines
            // in the document, and without this button the only way to remove
            // them would be selecting them by hand in the model.
            PushButtonData removeProxyData = new PushButtonData(
                "BilocusRemoveProxy",
                "Remove" + Environment.NewLine + "Proxy Lines",
                assemblyPath,
                "Bilocus.Revit.Proxy.RemoveProxyCommand");
            removeProxyData.ToolTip =
                "Deletes all proxy lines created by Bilocus in this document.";
            removeProxyData.LongDescription =
                "Proxy lines are the snappable model lines created from Blender edges " +
                "with Create Proxy. The number of lines is shown before anything is " +
                "deleted, and the operation can be undone with Ctrl+Z. Baked " +
                "DirectShapes and families are not touched: remove them from Blender " +
                "with Remove Bake.";
            Decorate(removeProxyData, "RemoveProxy");
            exchange.AddItem(removeProxyData);

            // Connection state: the only diagnostic left for the user.
            RibbonPanel connection = application.CreateRibbonPanel(TabName, "Connection");

            PushButtonData statusData = new PushButtonData(
                "BilocusStatus",
                "Status",
                assemblyPath,
                "Bilocus.Revit.StatusCommand");
            statusData.ToolTip =
                "Shows whether Bilocus is listening and whether Blender is connected.";
            statusData.LongDescription =
                "Reports the state of the local server, the last message received, " +
                "the objects and triangles in the preview and what Bilocus created in " +
                "this session (proxy lines, DirectShapes, families). If something " +
                "does not work, start here.";
            Decorate(statusData, "Status");
            connection.AddItem(statusData);

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

        // Help page opened with F1 on any Bilocus button.
        public const string HelpUrl = "https://github.com/robDL89/Bilocus#readme";

        // Icons are PNG files embedded in the assembly (Resources\<name>32.png
        // and <name>16.png, drawn by tools/make_icons.py). A missing icon
        // leaves the button without image instead of failing the startup.
        private static void Decorate(PushButtonData data, string iconName)
        {
            data.LargeImage = LoadIcon(iconName + "32");
            data.Image = LoadIcon(iconName + "16");
            data.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpUrl));
        }

        private static ImageSource LoadIcon(string name)
        {
            try
            {
                Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                    "Bilocus.Revit.Resources." + name + ".png");
                if (stream == null) { return null; }
                using (stream)
                {
                    BitmapImage image = new BitmapImage();
                    image.BeginInit();
                    image.StreamSource = stream;
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }
            }
            catch (Exception)
            {
                return null;
            }
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
