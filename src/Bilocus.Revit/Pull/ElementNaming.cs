// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System.Text;

namespace Bilocus.Revit.Pull
{
    // How an element pulled from Revit is named and what color it arrives
    // with in Blender. No Revit API in here on purpose: the caller extracts
    // the category and the type name, this file only decides how they are
    // composed. It is therefore the only part of tessellation that is unit
    // testable, and is included as a linked source from the test project.
    public static class ElementNaming
    {
        // Used when the element has no category: happens with some system
        // elements and with imported geometry.
        public const string FallbackCategory = "Element";

        // "<category> - <type> [<id>]", with the separator disappearing along
        // with the type when the type is absent. Without this care you get
        // names like " -  [123]", which in a Blender outliner are unreadable
        // and, worse, indistinguishable from each other.
        public static string BuildName(string category, string typeName, long elementId)
        {
            string cleanCategory = Clean(category);
            string cleanType = Clean(typeName);

            if (cleanCategory == null) { cleanCategory = FallbackCategory; }

            StringBuilder name = new StringBuilder();
            name.Append(cleanCategory);
            if (cleanType != null)
            {
                name.Append(" - ");
                name.Append(cleanType);
            }
            name.Append(" [");
            name.Append(elementId);
            name.Append("]");

            return name.ToString();
        }

        // The unique color of elements imported from Revit, RGBA 0..1. A
        // muted blue-gray: neutral enough not to be distracting, but with r,
        // g and b different from each other, so it does not get confused with
        // Blender's default material gray. It helps recognize at a glance
        // what is reference and what is being modeled.
        //
        // A new array on every call: if it were shared, a caller that
        // modifies it would change the color of every subsequent element.
        public static float[] DefaultColor()
        {
            return new float[] { 0.45f, 0.55f, 0.65f, 1.0f };
        }

        // Null when the value is absent or made only of spaces: in Revit an
        // unfilled parameter often comes back as an empty or whitespace
        // string, not as null, and both cases must end up on the same branch.
        private static string Clean(string value)
        {
            if (value == null) { return null; }
            string trimmed = value.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
