// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Text;

namespace Bilocus.Revit.Bake
{
    // The name of the family created by the bake: BL_<object name> (Phase
    // B2, decision 3).
    //
    // The name is a label, not identity: the link to the Blender object is
    // the obj_id mark, and renaming the object does not create a new
    // family. But it is a label that Revit checks: it rejects certain
    // characters and names already taken, and a rejected name fails the
    // object INSIDE the family document's transaction. Here a name Revit
    // accepts is prepared, where a test can check it without opening Revit.
    //
    // No Revit API: the names of the families present are collected by
    // FamilyBaker and passed to MakeUnique.
    public static class FamilyNaming
    {
        public const string Prefix = "BL_";

        // For an object without a usable name: "BL_object" is better than
        // "BL_", which says nothing in the project browser.
        public const string FallbackName = "BL_object";

        // Revit does not document a cap but very long names make the browser
        // and schedules unreadable; 120 leaves room for the uniqueness
        // suffix without touching the meaning of the name.
        public const int MaxLength = 120;

        // The characters Revit forbids in family names, plus the backtick.
        private const string Forbidden = "\\/:*?\"<>|{}[];~`";

        // "BL_" + name, with forbidden and control characters replaced by
        // '_', edge spaces trimmed, length capped at MaxLength.
        //
        // The trim happens BEFORE the substitution: a trailing newline in
        // the name is leftover, not a character to turn into a visible "_".
        public static string BuildName(string objectName)
        {
            string trimmed = objectName == null ? "" : objectName.Trim();
            if (trimmed.Length == 0) { return FallbackName; }

            StringBuilder name = new StringBuilder(Prefix.Length + trimmed.Length);
            name.Append(Prefix);
            foreach (char c in trimmed)
            {
                name.Append(char.IsControl(c) || Forbidden.IndexOf(c) >= 0 ? '_' : c);
            }

            return Truncate(name.ToString(), MaxLength);
        }

        // The name if no family already uses it, otherwise name_2, name_3...
        // the first free one. Case-insensitive comparison, as Revit does: a
        // "bl_cube" already loaded also makes "BL_Cube" taken.
        //
        // The suffix does not push the name past MaxLength: the base is
        // shortened instead.
        public static string MakeUnique(string name, ICollection<string> taken)
        {
            if (name == null) throw new ArgumentNullException("name");
            if (taken == null) throw new ArgumentNullException("taken");

            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string existing in taken)
            {
                if (existing != null) { used.Add(existing); }
            }

            if (!used.Contains(name)) { return name; }

            // Always terminates: taken names are finite in number.
            for (int index = 2; ; index++)
            {
                string suffix = "_" + index;
                string candidate = Truncate(name, MaxLength - suffix.Length) + suffix;
                if (!used.Contains(candidate)) { return candidate; }
            }
        }

        // Truncates to maxLength without splitting a surrogate pair in half:
        // half a pair is an invalid string, which Revit would reject or
        // mangle. Also removes trailing spaces left uncovered by the cut.
        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength) { return value; }

            int length = maxLength;
            if (char.IsHighSurrogate(value[length - 1])) { length--; }
            return value.Substring(0, length).TrimEnd();
        }
    }
}
