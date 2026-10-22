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
        // Inside an open transaction on the family document. Returns the
        // parameters actually written; note says which BL_ ones were left
        // alone, or is null.
        //
        // The user may build on a BL_ parameter in the family editor. One
        // used in another parameter's formula is never removed or re-typed:
        // RemoveParameter does not throw, it silently empties that formula
        // (Revit 2025, checked live). One with a formula of its own is the
        // user's choice and is not overwritten.
        public static List<BakeParameter> Apply(Document familyDoc, IList<BakeParameter> parameters,
            string typeName, out string note)
        {
            FamilyManager manager = familyDoc.FamilyManager;
            EnsureCurrentType(manager, typeName);

            Dictionary<string, BakeParameter> wanted = new Dictionary<string, BakeParameter>();
            foreach (BakeParameter p in parameters) { wanted[p.Name] = p; }

            // A copy: removing while iterating manager.Parameters would
            // invalidate the iterator.
            List<FamilyParameter> existing = new List<FamilyParameter>();
            foreach (FamilyParameter fp in manager.Parameters) { existing.Add(fp); }

            List<string> left = new List<string>();
            HashSet<string> keptAsIs = new HashSet<string>();
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
                    string user = FormulaUsing(existing, fp);
                    if (user != null)
                    {
                        keptAsIs.Add(name);
                        left.Add(name + " kept, used in the formula of " + user);
                        continue;
                    }
                    manager.RemoveParameter(fp);
                }
            }

            List<BakeParameter> written = new List<BakeParameter>();
            foreach (BakeParameter p in parameters)
            {
                if (keptAsIs.Contains(p.Name)) { continue; }
                FamilyParameter fp = manager.get_Parameter(p.Name);
                if (fp == null)
                {
                    fp = manager.AddParameter(p.Name, GroupTypeId.Geometry, SpecOf(p.Kind), false);
                }
                else if (!string.IsNullOrEmpty(fp.Formula))
                {
                    // IsDeterminedByFormula reads false here (Revit 2025,
                    // checked live), and Set would be accepted and ignored:
                    // the formula text is what tells.
                    left.Add(p.Name + " not written, it has a formula");
                    continue;
                }
                manager.Set(fp, ToInternal(p));
                written.Add(p);
            }

            note = left.Count == 0 ? null : string.Join(", ", left);
            return written;
        }

        // The name of another parameter whose formula uses fp, or null.
        private static string FormulaUsing(IList<FamilyParameter> all, FamilyParameter fp)
        {
            string name = fp.Definition.Name;
            foreach (FamilyParameter other in all)
            {
                if (other.Id == fp.Id) { continue; }
                if (BakeParameter.IsUsedIn(name, other.Formula)) { return other.Definition.Name; }
            }
            return null;
        }

        // Inside an open transaction on the project. The re-bake reloads
        // with overwriteParameterValues false, so the user's own values on
        // the project types stay: the BL_ values are written here, and only
        // those, on every type of the family (they describe the geometry,
        // which is the same for all of them). Pass what Apply wrote: a BL_
        // left alone in the family must be left alone here too.
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
