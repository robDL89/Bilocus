// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Bake
{
    // The format of a bake category name: ^OST_[A-Za-z0-9_]+$, i.e. the name
    // of a BuiltInCategory value as written by the Blender side.
    //
    // No Revit API, like ProxyNaming: it also compiles in the test project.
    // Here we only decide whether the string HAS THE SHAPE of a category.
    // Whether the category really exists, and whether it is allowed for a
    // DirectShape (DirectShape.IsValidCategoryId), only Revit knows: it is
    // checked by BakeBuilder at bake time and fails only that object, not the
    // message.
    //
    // The early check has a precise reason: the category arrives from the
    // network and ends up in Enum.TryParse<BuiltInCategory>, which also
    // accepts numbers ("-2000011") and comma-separated lists. A string that
    // does not have the OST_ shape stops here, as a content error, instead of
    // becoming a category chosen by chance.
    public static class BakeCategory
    {
        // Generic model: the category of an object the user has not assigned
        // one to.
        public const string DefaultCategory = "OST_GenericModel";

        private const string Prefix = "OST_";

        // Character by character and not Regex.IsMatch, on purpose: in .NET
        // the $ of "^OST_[A-Za-z0-9_]+$" also accepts a trailing \n, and
        // "OST_Walls\n" would pass. The Blender side rejects it, and the two
        // sides must agree on the same string.
        public static bool IsWellFormed(string category)
        {
            if (category == null) { return false; }
            if (category.Length <= Prefix.Length) { return false; }
            if (!category.StartsWith(Prefix, StringComparison.Ordinal)) { return false; }

            for (int i = Prefix.Length; i < category.Length; i++)
            {
                char c = category[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';

                if (!letter && !digit && c != '_') { return false; }
            }

            return true;
        }
    }
}
