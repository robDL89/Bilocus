// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Bake
{
    // The mode of a bake as it travels on the wire (Phase B2, "Wire contract").
    //
    // No Revit API, like BakeCategory: it also compiles in the test project.
    // The values are the contract with bridge_bake.py, which writes and reads
    // them back strictly: they are constants here, not scattered strings in
    // the builder, because a typo would not cause a compile error but a bake
    // that Blender rejects at runtime.
    public static class BakeTarget
    {
        // The bake of Phase B, and the meaning of a bake_begin without target.
        public const string DirectShape = "directshape";

        // A loadable family per object, with one instance.
        public const string Family = "family";

        // Only in the bake_result of a removal: "Remove bake" takes away
        // everything the bridge created, in both modes. Blender cannot
        // request an "all" bake, which is why TryParse rejects it.
        public const string All = "all";

        // Accepts only DirectShape and Family, exact match: no uppercase nor
        // spaces forgiven, as for the category, because the two sides must
        // agree on the same string. target is the matching constant, null if
        // the value is rejected.
        public static bool TryParse(string value, out string target)
        {
            if (string.Equals(value, DirectShape, StringComparison.Ordinal))
            {
                target = DirectShape;
                return true;
            }
            if (string.Equals(value, Family, StringComparison.Ordinal))
            {
                target = Family;
                return true;
            }

            target = null;
            return false;
        }
    }
}
