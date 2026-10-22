// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Bake
{
    // One BL_ family parameter of a bake_mesh: a rough dimension of the
    // Blender object, informative, written as a type parameter of the
    // family. No Revit API in here, so it is checked by the tests;
    // FamilyParameters does the writing.
    public sealed class BakeParameter
    {
        public const string Prefix = "BL_";

        public string Name { get; private set; }

        // "length", "area" or "volume": it picks the Revit spec and unit.
        public string Kind { get; private set; }

        // SI units, m, m2 or m3 by Kind.
        public double Value { get; private set; }

        private BakeParameter(string name, string kind, double value)
        {
            Name = name;
            Kind = kind;
            Value = value;
        }

        // Throws ArgumentException on anything a Blender add-on would never
        // send: the router turns it into a "bake_mesh: ..." error for that
        // object. Letters only after the prefix, because the name becomes a
        // family parameter name and the prefix is how stale ones are found.
        public static BakeParameter Create(string name, string kind, double value)
        {
            if (name == null || name.Length <= Prefix.Length
                || !name.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("parameter name '" + name + "' must start with " + Prefix);
            }
            for (int i = Prefix.Length; i < name.Length; i++)
            {
                if (!char.IsLetter(name[i]))
                {
                    throw new ArgumentException(
                        "parameter name '" + name + "' may only contain letters after " + Prefix);
                }
            }
            if (kind != "length" && kind != "area" && kind != "volume")
            {
                throw new ArgumentException(
                    "parameter " + name + ": kind '" + kind + "' is not length, area or volume");
            }
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                throw new ArgumentException("parameter " + name + ": value must be a finite number >= 0");
            }
            return new BakeParameter(name, kind, value);
        }
    }
}
