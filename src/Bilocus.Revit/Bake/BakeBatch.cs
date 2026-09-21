// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Bilocus.Revit.Proxy;

namespace Bilocus.Revit.Bake
{
    // A bake: the objects announced by bake_begin, the bake_mesh that arrived
    // for each of them, and the ones missing.
    //
    // It exists because a bake is ONE operation on the document even though
    // it arrives in several messages: one TransactionGroup, one Ctrl+Z. The
    // router collects the pieces here as they arrive, and only at bake_end
    // does the batch pass to MessageHandler. No Revit API: it also compiles
    // in the test project.
    //
    // The announced ids serve two purposes. Rejecting a bake_mesh that
    // nobody announced, which is a defect of the sender and not an extra
    // object to be written secretly into the document. And counting the
    // missing ones: an announced object that does not arrive (Blender failed
    // to pack it, or its bake_mesh was rejected) comes back in bake_result as
    // missing, instead of disappearing silently.
    public sealed class BakeBatch
    {
        // Cap on the obj_ids of a bake_begin or a bake_remove, the same value
        // as the Blender side.
        //
        // Each object is a transaction inside the bake group, with its own
        // geometry: five hundred is already a selection that is not made by
        // mistake, and beyond that there is a risk of holding Revit still
        // with no way to interrupt it.
        public const int MaxBakeObjects = 500;

        // Cap for a family bake (Phase B2), the same value as the Blender
        // side. Each object opens, fills, loads and closes a family
        // document: it is slow by construction, and fifty is already
        // minutes.
        public const int MaxFamilyBakeObjects = 50;

        private readonly List<string> _announced;
        private readonly HashSet<string> _announcedSet;
        private readonly Dictionary<string, BakeMeshRequest> _arrived =
            new Dictionary<string, BakeMeshRequest>(StringComparer.Ordinal);

        // The Phase B constructor: a DirectShape bake. Kept for existing
        // callers and because it is the meaning of a bake_begin without a
        // target.
        //
        // Raises ArgumentException if the ids do not follow the
        // NormalizeObjectIds rules.
        public BakeBatch(IList<string> announcedIds)
            : this(announcedIds, BakeTarget.DirectShape)
        {
        }

        // target: BakeTarget.DirectShape or BakeTarget.Family, otherwise
        // ArgumentException. The target is checked BEFORE the ids, because it
        // decides the cap used to count them.
        public BakeBatch(IList<string> announcedIds, string target)
            : this(announcedIds, target, null)
        {
        }

        // host: null for a normal bake. Not null = "Bake together": one
        // family with every announced object inside, named after the host
        // (the active object in Blender), placed at the host's origin and
        // identified by the host's obj_id. Only for target family, and the
        // host must be one of the announced ids.
        public BakeBatch(IList<string> announcedIds, string target, string host)
        {
            string parsed;
            if (!BakeTarget.TryParse(target, out parsed))
            {
                throw new ArgumentException(string.Format(
                    "target '{0}' invalid: allowed \"{1}\" and \"{2}\"",
                    target, BakeTarget.DirectShape, BakeTarget.Family));
            }

            Target = parsed;
            _announced = NormalizeObjectIds(announcedIds, MaxObjectsFor(parsed));
            _announcedSet = new HashSet<string>(_announced, StringComparer.Ordinal);

            if (host != null)
            {
                if (parsed != BakeTarget.Family)
                {
                    throw new ArgumentException("host (bake together) is only allowed with target family");
                }
                string normalized = ProxyNaming.NormalizeObjectId(host);
                if (!_announcedSet.Contains(normalized))
                {
                    throw new ArgumentException(string.Format(
                        "host '{0}' is not among the announced obj_ids", normalized));
                }
                Host = normalized;
            }
        }

        // The active object of a "Bake together", or null.
        public string Host { get; private set; }

        public bool Together { get { return Host != null; } }

        // BakeTarget.DirectShape or BakeTarget.Family. Decides who executes
        // the batch: BakeBuilder or FamilyBaker.
        public string Target { get; private set; }

        // The object cap for a bake of that target. ArgumentException for a
        // target that is not a bake (including "all").
        public static int MaxObjectsFor(string target)
        {
            string parsed;
            if (!BakeTarget.TryParse(target, out parsed))
            {
                throw new ArgumentException(string.Format("target '{0}' invalid for a bake", target));
            }
            return parsed == BakeTarget.Family ? MaxFamilyBakeObjects : MaxBakeObjects;
        }

        // Normalized, in announcement order. Read-only: the announced ids are
        // exactly those of bake_begin and nothing else.
        public IList<string> AnnouncedIds
        {
            get { return _announced.AsReadOnly(); }
        }

        // The arrived requests, in ANNOUNCEMENT order and not arrival order:
        // it is the order in which the Blender side listed the objects, and
        // the one in which the bake writes them. New list on every read.
        public List<BakeMeshRequest> Requests
        {
            get
            {
                List<BakeMeshRequest> requests = new List<BakeMeshRequest>(_arrived.Count);
                foreach (string id in _announced)
                {
                    BakeMeshRequest request;
                    if (_arrived.TryGetValue(id, out request)) { requests.Add(request); }
                }
                return requests;
            }
        }

        // The announced ids that did not arrive, in announcement order. New
        // list on every read.
        public List<string> MissingIds
        {
            get
            {
                List<string> missing = new List<string>();
                foreach (string id in _announced)
                {
                    if (!_arrived.ContainsKey(id)) { missing.Add(id); }
                }
                return missing;
            }
        }

        // Adds an arrived object.
        //
        // An id that was not announced is rejected with ArgumentException. A
        // second arrival of the same id replaces the first: the last one
        // wins, the same rule as GeometryStore and proxy requests.
        public void Add(BakeMeshRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");

            if (!_announcedSet.Contains(request.ObjectId))
            {
                throw new ArgumentException(string.Format(
                    "obj_id '{0}' not announced in bake_begin", request.ObjectId));
            }

            _arrived[request.ObjectId] = request;
        }

        // The rules on obj_ids, shared by bake_begin and bake_remove:
        // non-empty list, at most MaxBakeObjects, each id normalized by
        // ProxyNaming, no duplicates. Returns the normalized ids, in the
        // received order.
        //
        // Duplicates are checked AFTER normalization: "a" and " a" are the
        // same mark in the document, and two objects with the same mark
        // would end up on the same DirectShape, with the last one silently
        // overwriting the first.
        public static List<string> NormalizeObjectIds(IList<string> objectIds)
        {
            return NormalizeObjectIds(objectIds, MaxBakeObjects);
        }

        // The same rules with a different cap: the family bake has a lower
        // one. bake_remove stays at MaxBakeObjects even for families, because
        // a removal does not open family documents.
        public static List<string> NormalizeObjectIds(IList<string> objectIds, int maxObjects)
        {
            if (objectIds == null) throw new ArgumentNullException("objectIds");

            if (objectIds.Count == 0)
            {
                throw new ArgumentException("obj_ids empty: no object given");
            }

            if (objectIds.Count > maxObjects)
            {
                throw new ArgumentException(string.Format(
                    "obj_ids with {0} objects, over the maximum of {1} per request",
                    objectIds.Count, maxObjects));
            }

            List<string> normalized = new List<string>(objectIds.Count);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < objectIds.Count; i++)
            {
                // The message states the position and not the id: an id full
                // of control characters does not print in a readable way.
                if (!ProxyNaming.IsValidObjectId(objectIds[i]))
                {
                    throw new ArgumentException(string.Format(
                        "obj_ids[{0}] invalid: must be non-empty and without control characters", i));
                }

                string id = ProxyNaming.NormalizeObjectId(objectIds[i]);
                if (!seen.Add(id))
                {
                    throw new ArgumentException(string.Format("obj_id '{0}' repeated in obj_ids", id));
                }
                normalized.Add(id);
            }

            return normalized;
        }
    }
}
