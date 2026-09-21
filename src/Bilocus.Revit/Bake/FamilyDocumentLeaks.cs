// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using System.Text;

namespace Bilocus.Revit.Bake
{
    // The family documents the bake opens, that must not stay open at the
    // end of the batch.
    //
    // FamilyBaker closes every family document in a finally, right after
    // LoadFamily. But if Close(false) fails, the document stays open in
    // memory and nobody sees it: it has no window, and the user cannot close
    // it. It has the family's title, so the next re-bake would mistake it
    // for an editor opened by the user and would ask to close an editor that
    // is not there.
    //
    // For this reason the bake registers here the documents IT opens, and at
    // the end of the batch closes again those still open. Only its own: a
    // family document opened by the user never enters the list, so it can
    // never be closed, losing their changes.
    //
    // No Revit API: "is it still open?" and "close it" arrive as functions,
    // so the logic and the Note text can be tested without Revit.
    public sealed class FamilyDocumentLeaks<TDocument> where TDocument : class
    {
        public const string ClosedNowPrefix = "family documents left open and closed now: ";
        public const string NotClosedPrefix =
            "family documents left open and NOT closed, they will free up only by restarting Revit: ";

        private readonly List<KeyValuePair<TDocument, string>> _opened =
            new List<KeyValuePair<TDocument, string>>();

        public int Count
        {
            get { return _opened.Count; }
        }

        // objectName is the name of the Blender object: in the Note it tells
        // the user which object is being talked about, something the
        // document's title ("Family1") does not do.
        public void Register(TDocument document, string objectName)
        {
            if (document == null) { throw new ArgumentNullException("document"); }
            _opened.Add(new KeyValuePair<TDocument, string>(document, objectName ?? ""));
        }

        // Closes again the registered documents that turn out to be still
        // open. close returns null if the document closed, otherwise the
        // reason. An exception from either function only concerns that
        // document: the others are still tried. Returns the Note, or null if
        // nothing was left open. Afterwards, the list is empty.
        public string Sweep(Func<TDocument, bool> isStillOpen, Func<TDocument, string> close)
        {
            if (isStillOpen == null) { throw new ArgumentNullException("isStillOpen"); }
            if (close == null) { throw new ArgumentNullException("close"); }

            List<string> closedNow = new List<string>();
            List<string> notClosed = new List<string>();

            foreach (KeyValuePair<TDocument, string> entry in _opened)
            {
                string label = "'" + entry.Value + "'";

                bool open;
                try
                {
                    open = isStillOpen(entry.Key);
                }
                catch (Exception ex)
                {
                    notClosed.Add(label + " (state unreadable: " + Describe(ex) + ")");
                    continue;
                }

                if (!open) { continue; }

                string failure;
                try
                {
                    failure = close(entry.Key);
                }
                catch (Exception ex)
                {
                    failure = Describe(ex);
                }

                if (failure == null) { closedNow.Add(label); }
                else { notClosed.Add(label + " (" + failure + ")"); }
            }

            _opened.Clear();
            return BuildNote(closedNow, notClosed);
        }

        private static string BuildNote(List<string> closedNow, List<string> notClosed)
        {
            if (closedNow.Count == 0 && notClosed.Count == 0) { return null; }

            StringBuilder note = new StringBuilder();
            if (closedNow.Count > 0)
            {
                note.Append(ClosedNowPrefix).Append(string.Join(", ", closedNow.ToArray()));
            }
            if (notClosed.Count > 0)
            {
                if (note.Length > 0) { note.Append("; "); }
                note.Append(NotClosedPrefix).Append(string.Join(", ", notClosed.ToArray()));
            }
            return note.ToString();
        }

        private static string Describe(Exception ex)
        {
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
