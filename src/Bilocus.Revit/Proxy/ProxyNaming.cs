// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;

namespace Bilocus.Revit.Proxy
{
    // The part of the mark that does not touch the Revit API: the schema's
    // identity constants, the subcategory name, and the rules for what a
    // writable obj_id is.
    //
    // Lives in a file separate from ProxySchema for the same reason as
    // ElementNaming in Phase A2: it is included as a linked source from the
    // test project, which compiles WITHOUT the Revit API. This way the rules
    // in here are verified by real tests instead of by a re-read.
    //
    // The concrete gain: the schema's GUID, which must never change again, and
    // the names Revit only accepts if they follow precise rules, become
    // invariants that a test breaks immediately. If they were in
    // ProxySchema.cs they would only be verifiable by launching Revit.
    public static class ProxyNaming
    {
        // WARNING: THIS GUID IS CONSTANT AND MUST NEVER CHANGE AGAIN.
        //
        // It is the identity of the Extensible Storage schema with which the
        // bridge marks the elements it creates in the document. Changing it
        // in a future version would make every proxy created before it
        // invisible: they could no longer be replaced or removed, and they
        // would stay inside project files forever, with nobody knowing what
        // they are or who put them there.
        //
        // It is not a configuration value, it is not regenerable, it is not
        // "to be updated when the version changes". If a second mark is
        // needed for a second purpose, ANOTHER GUID is added next to this one:
        // this one stays.
        //
        // A test pins it to the exact value, on purpose: if someone changes
        // it, the suite goes red before the code ever reaches Revit.
        public const string SchemaGuidText = "b00fa2f7-f2ca-464b-877b-7e4bdec8018e";

        // Schema name. Revit accepts only strings usable as C++ identifiers as
        // schema or field names: ASCII letters, digits (not as the first
        // character) and underscore. Verified against RevitAPI.xml,
        // SchemaBuilder.AcceptableName. So NOT "Bilocus Proxy": the space
        // would make SetSchemaName throw at runtime inside Revit, which is
        // the worst place to find out.
        public const string SchemaName = "BilocusProxy";

        // Vendor identifier. Must be at least 4 characters long and contain
        // only letters, digits or a restricted set of symbols (RevitAPI.xml,
        // SchemaBuilder.VendorIdIsValid). Revit converts it to uppercase
        // anyway before storing it: we write it already uppercase so what is
        // read here is what ends up in the file.
        public const string VendorId = "BILOCUS";

        // The schema's only field: the obj_id of the source Blender object.
        // This is what allows replacing proxies of the same object.
        public const string ObjectIdFieldName = "obj_id";

        // Name of the Lines subcategory. This is NOT a schema name: it is a
        // Revit category name, and the space is legal. It needs to be
        // readable in the Line Styles window, where the user sees it.
        public const string SubcategoryName = "Bilocus Proxy";

        // Characters that Revit rejects in element and category names. Listed
        // here because SubcategoryName is a constant someone will sooner or
        // later change, and Categories.NewSubcategory answers an illegal name
        // with an ArgumentException inside the transaction that is creating
        // the user's proxies: the rollback would take away everything else
        // along with the wrong name.
        private const string IllegalNameCharacters = "\\:{}[]|;<>?`~";

        public static Guid SchemaGuid
        {
            get { return new Guid(SchemaGuidText); }
        }

        // Reproduces SchemaBuilder.AcceptableName's rule. Not a pointless
        // duplication: it runs here inside the tests, where the Revit API
        // does not exist, and it is the only way to verify SchemaName and
        // ObjectIdFieldName without opening Revit.
        public static bool IsAcceptableStorageName(string name)
        {
            if (name == null) { return false; }
            if (name.Length == 0 || name.Length > 247) { return false; }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';
                bool underscore = c == '_';

                if (!letter && !digit && !underscore) { return false; }
                if (i == 0 && digit) { return false; }
            }

            return true;
        }

        // Reproduces SchemaBuilder.VendorIdIsValid's rule.
        public static bool IsAcceptableVendorId(string vendorId)
        {
            if (vendorId == null) { return false; }
            if (vendorId.Length < 4 || vendorId.Length > 253) { return false; }

            const string allowedSymbols = "!\"#&\\()+,.-:;<=>?_`|~";

            for (int i = 0; i < vendorId.Length; i++)
            {
                char c = vendorId[i];
                bool letter = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';

                if (letter || digit) { continue; }
                if (allowedSymbols.IndexOf(c) >= 0) { continue; }

                return false;
            }

            return true;
        }

        // An acceptable Revit category name: not empty and without the
        // characters Revit reserves.
        public static bool IsAcceptableCategoryName(string name)
        {
            if (name == null) { return false; }
            if (name.Trim().Length == 0) { return false; }

            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (IllegalNameCharacters.IndexOf(c) >= 0) { return false; }
                if (char.IsControl(c)) { return false; }
            }

            return true;
        }

        // A writable obj_id: not null, not made of spaces only, without
        // control characters.
        //
        // Control characters are excluded because the obj_id also travels in
        // the JSON header of the bridge's messages: a \0 or a \n inside the
        // mark would produce an identifier that no longer compares equal to
        // itself after a round trip on the wire, and replacing proxies of the
        // same object would stop working without giving an error.
        public static bool IsValidObjectId(string objectId)
        {
            if (objectId == null) { return false; }

            string trimmed = objectId.Trim();
            if (trimmed.Length == 0) { return false; }

            for (int i = 0; i < trimmed.Length; i++)
            {
                if (char.IsControl(trimmed[i])) { return false; }
            }

            return true;
        }

        // The canonical form of the obj_id: the one written into the mark and
        // the one compared against.
        //
        // The trim is not cosmetic. If "Cube " were written and "Cube" were
        // searched for, the replacement would find nothing: resending the same
        // object would create a second copy of the proxies instead of
        // replacing the first, and the document would accumulate overlapping
        // lines without anything signaling an error. Better to normalize once,
        // here.
        public static string NormalizeObjectId(string objectId)
        {
            if (!IsValidObjectId(objectId))
            {
                throw new ArgumentException(
                    "invalid obj_id: must be non-empty and without control characters",
                    "objectId");
            }

            return objectId.Trim();
        }

        // Comparison between an obj_id read from the document and one
        // requested.
        //
        // Deliberately lenient: the stored value comes from a file that could
        // have been touched by anyone (the schema has public write access, see
        // ProxySchema), so here an absurd value must yield "does not match",
        // not an exception in the middle of a loop over every curve in the
        // model.
        //
        // Ordinal comparison, case sensitive: an obj_id is an opaque
        // identifier written by the Blender side (a uuid4 hex today), and
        // two ids that differ only in case are two different objects. A
        // case-insensitive comparison would replace one object's proxies
        // while resending the other.
        public static bool MatchesObjectId(string stored, string requested)
        {
            if (stored == null || requested == null) { return false; }
            return string.Equals(stored.Trim(), requested.Trim(), StringComparison.Ordinal);
        }
    }
}
