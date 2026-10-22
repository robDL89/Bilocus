// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace Bilocus.Revit.Bake
{
    // Writes the BL_ parameters of a bake: type parameters of the family,
    // group Dimensions, informative values that do not drive the geometry.
    // Parameters without the BL_ prefix are never touched; BL_ parameters
    // that did not arrive are deleted, because stale data is worse than
    // missing data (an override cleared, a mesh that opened).
    public static class FamilyParameters
    {
        // Inside an open transaction on the family document.
        public static void Apply(Document familyDoc, IList<BakeParameter> parameters, string typeName)
        {
            FamilyManager manager = familyDoc.FamilyManager;
            EnsureCurrentType(manager, typeName);

            Dictionary<string, BakeParameter> wanted = new Dictionary<string, BakeParameter>();
            foreach (BakeParameter p in parameters) { wanted[p.Name] = p; }

            // A copy: removing while iterating manager.Parameters would
            // invalidate the iterator.
            List<FamilyParameter> existing = new List<FamilyParameter>();
            foreach (FamilyParameter fp in manager.Parameters) { existing.Add(fp); }

            foreach (FamilyParameter fp in existing)
            {
                string name = fp.Definition.Name;
                if (!name.StartsWith(BakeParameter.Prefix, System.StringComparison.Ordinal)) { continue; }

                // Also removed when it no longer has the shape it is
                // written with: added again right below.
                BakeParameter p;
                if (!wanted.TryGetValue(name, out p) || fp.IsInstance
                    || fp.Definition.GetDataType() != SpecOf(p.Kind))
                {
                    manager.RemoveParameter(fp);
                }
            }

            foreach (BakeParameter p in parameters)
            {
                FamilyParameter fp = manager.get_Parameter(p.Name);
                if (fp == null)
                {
                    fp = manager.AddParameter(p.Name, GroupTypeId.Geometry, SpecOf(p.Kind), false);
                }
                manager.Set(fp, ToInternal(p));
            }
        }

        // Inside an open transaction on the project. The re-bake reloads
        // with overwriteParameterValues false, so the user's own values on
        // the project types stay: the BL_ values are written here, and only
        // those, on every type of the family (they describe the geometry,
        // which is the same for all of them).
        public static void WriteToTypes(Document project, Family family, IList<BakeParameter> parameters)
        {
            foreach (ElementId id in family.GetFamilySymbolIds())
            {
                FamilySymbol symbol = project.GetElement(id) as FamilySymbol;
                if (symbol == null) { continue; }
                foreach (BakeParameter p in parameters)
                {
                    Parameter parameter = symbol.LookupParameter(p.Name);
                    if (parameter != null && !parameter.IsReadOnly) { parameter.Set(ToInternal(p)); }
                }
            }
        }

        // A type is needed to hold values. A template may come without
        // one; a family opened with EditFamily has its types but may not
        // have one current. Creating a type when one exists would load a
        // second type into the project.
        private static void EnsureCurrentType(FamilyManager manager, string typeName)
        {
            if (manager.CurrentType != null) { return; }
            foreach (FamilyType type in manager.Types)
            {
                manager.CurrentType = type;
                return;
            }
            manager.NewType(typeName);
        }

        private static ForgeTypeId SpecOf(string kind)
        {
            if (kind == "area") { return SpecTypeId.Area; }
            if (kind == "volume") { return SpecTypeId.Volume; }
            return SpecTypeId.Length;
        }

        private static double ToInternal(BakeParameter p)
        {
            ForgeTypeId unit = p.Kind == "area" ? UnitTypeId.SquareMeters
                : p.Kind == "volume" ? UnitTypeId.CubicMeters : UnitTypeId.Meters;
            return UnitUtils.ConvertToInternalUnits(p.Value, unit);
        }
    }
}
