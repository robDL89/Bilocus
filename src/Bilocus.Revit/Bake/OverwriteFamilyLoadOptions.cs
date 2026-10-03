// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using Autodesk.Revit.DB;

namespace Bilocus.Revit.Bake
{
    // The answers to LoadFamily when the family is already in the project:
    // the Phase B2 re-bake ALWAYS reloads over the existing version.
    //
    // No dialog and no choice for the user, on purpose: it runs on an
    // ExternalEvent triggered by the network, and the overload with
    // RevitUIFamilyLoadOptions would open Revit's prompts halfway through
    // the bake. Whoever presses "Bake Family" has already decided to update.
    //
    // overwriteParameterValues false: the parameter values the user filled
    // in on the project's types stay. The bridge's family only carries
    // geometry and category, and overwriting them would add nothing except
    // the risk of losing work done by hand.
    public sealed class OverwriteFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return true;
        }

        // The bridge's families do not nest shared families: the branch
        // exists for the interface. If one day the user nests one in the
        // editor, the version of the family being loaded wins, consistent
        // with the "reload over" of the previous method.
        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = false;
            return true;
        }
    }

    // The answers to LoadFamily for a NEW bridge family: never overwrite.
    // The name was chosen free just before, so a family found with it means
    // something else owns that name, and overwriting it would replace the
    // geometry of every one of its instances. Observed in the field: the
    // user's family, not the bridge's, ended up with the baked solid.
    public sealed class KeepExistingFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = false;
            return false;
        }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Project;
            overwriteParameterValues = false;
            return false;
        }
    }
}
