// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace Bilocus.Revit
{
    [Transaction(TransactionMode.Manual)]
    public sealed class PingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            TaskDialog.Show("Bilocus", "Add-in loaded correctly.");
            return Result.Succeeded;
        }
    }
}
