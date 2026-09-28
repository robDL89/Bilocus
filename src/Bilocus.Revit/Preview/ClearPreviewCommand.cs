// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bilocus.Revit.Preview
{
    // The Revit-side twin of Clear Preview in Blender. Needed because the
    // preview outlives the connection on purpose: closing Blender leaves it
    // in the views, and without this button the only way to get rid of it
    // would be restarting Revit.
    //
    // Nothing is written to the document. Blender resends every object at
    // each Sync, so the next Sync brings the preview back.
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class ClearPreviewCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (App.Current == null || App.Current.Store == null)
            {
                TaskDialog.Show("Bilocus", "Add-in not initialized.");
                return Result.Cancelled;
            }

            App.Current.Store.Clear();

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc != null) { uidoc.UpdateAllOpenViews(); }
            return Result.Succeeded;
        }
    }
}
