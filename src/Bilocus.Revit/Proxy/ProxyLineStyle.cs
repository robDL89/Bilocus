// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using Autodesk.Revit.DB;

namespace Bilocus.Revit.Proxy
{
    // The proxy line style: a dedicated Lines subcategory called
    // "Bilocus Proxy".
    //
    // It exists to make proxies distinguishable in the drawing. Without it,
    // they would be model lines identical to those drawn by the user in every
    // view: they could not be turned off per view, could not be excluded from
    // printing, and it would not be obvious at a glance what is scaffolding
    // and what is project. ProxySchema's mark answers "who created it" for
    // the code; this subcategory answers the same question for the eye.
    //
    // NO METHOD IN HERE OPENS A TRANSACTION, on purpose. Creating the
    // subcategory needs one, but it must be the same transaction in which the
    // caller creates the proxies: two transactions for a single user action
    // would mean two entries in the undo history, and a Ctrl+Z that seems to
    // work but leaves half the work in the document.
    public static class ProxyLineStyle
    {
        // Blender orange. Not decoration: it is the color that tells you where
        // that line comes from without having to query it. Applied only at
        // creation time, never reapplied afterwards: if the user decides to
        // recolor it, the bridge must not put it back the way it likes it on
        // every resend.
        private const byte ColorRed = 232;
        private const byte ColorGreen = 126;
        private const byte ColorBlue = 34;

        // The GraphicsStyle to assign to CurveElement.LineStyle, creating the
        // subcategory if it does not exist yet in the document.
        //
        // Must be called INSIDE an already open transaction. The check on
        // IsModifiable exists to give a readable message instead of Revit's
        // ModificationOutsideTransactionException, which tells whoever reads
        // the log nothing about which of the command's twenty calls was out of
        // place.
        public static GraphicsStyle GetOrCreate(Document document)
        {
            if (document == null) { throw new ArgumentNullException("document"); }

            GraphicsStyle existing = Find(document);
            if (existing != null) { return existing; }

            if (!document.IsModifiable)
            {
                throw new InvalidOperationException(
                    "The subcategory '" + ProxyNaming.SubcategoryName + "' does not exist yet and "
                    + "creating it requires a transaction already open on the document.");
            }

            Category parent = GetLinesCategory(document);
            if (!parent.CanAddSubcategory)
            {
                throw new InvalidOperationException(
                    "The Lines category of this document does not accept subcategories: the "
                    + "dedicated proxy style cannot be created.");
            }

            // If someone changes the constant to include a character Revit
            // reserves, NewSubcategory throws inside the transaction that is
            // creating the proxies, and the rollback takes everything away
            // along with the wrong name. Better to stop here, with the guilty
            // name in the message.
            if (!ProxyNaming.IsAcceptableCategoryName(ProxyNaming.SubcategoryName))
            {
                throw new InvalidOperationException(
                    "Invalid subcategory name for Revit: '"
                    + ProxyNaming.SubcategoryName + "'");
            }

            Category created = document.Settings.Categories.NewSubcategory(
                parent, ProxyNaming.SubcategoryName);
            created.LineColor = new Color(ColorRed, ColorGreen, ColorBlue);

            GraphicsStyle style = created.GetGraphicsStyle(GraphicsStyleType.Projection);
            if (style == null)
            {
                throw new InvalidOperationException(
                    "The subcategory '" + ProxyNaming.SubcategoryName + "' was created but does "
                    + "not expose a projection style assignable to a model line.");
            }

            return style;
        }

        // The style if the subcategory already exists, null otherwise. Creates
        // nothing and requires no transaction: this is what the removal
        // command needs, which must be able to recognize proxies without
        // writing anything to the document just for having been opened.
        public static GraphicsStyle Find(Document document)
        {
            if (document == null) { return null; }

            Category parent = GetLinesCategory(document);
            Category sub = FindSubcategory(parent, ProxyNaming.SubcategoryName);
            if (sub == null) { return null; }

            return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        }

        private static Category GetLinesCategory(Document document)
        {
            Category lines = Category.GetCategory(document, BuiltInCategory.OST_Lines);
            if (lines == null)
            {
                throw new InvalidOperationException(
                    "The Lines category (OST_Lines) is not available in this document.");
            }

            return lines;
        }

        // Iterates the map instead of querying it by name: CategoryNameMap
        // throws when the name is not there, and "not there" is the normal
        // case the first time the bridge is used on a document.
        private static Category FindSubcategory(Category parent, string name)
        {
            CategoryNameMap subcategories = parent.SubCategories;
            if (subcategories == null) { return null; }

            foreach (Category candidate in subcategories)
            {
                if (candidate == null) { continue; }
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
