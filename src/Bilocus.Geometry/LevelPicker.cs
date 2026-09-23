// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;

namespace Bilocus.Geometry
{
    // The level on which to place a bridge family instance: the closest one
    // below the object's origin.
    //
    // The instance's real elevation is the object's origin: FamilyBaker
    // moves the instance there after creating it, and the offset from the
    // level follows. The level, though, is the one the instance stays
    // hosted on:
    // if the user moves Level 1, the furniture on Level 1 must follow it,
    // not stay attached to the Ground Floor with a three-meter offset.
    //
    // No Revit API: FamilyBaker passes the level elevations already in
    // meters and uses the returned index into its own list.
    public static class LevelPicker
    {
        // An object resting exactly on a level sits on that level. Without a
        // tolerance, Revit's feet -> meters conversion and Blender's float32
        // would drop it, at random, onto the level below.
        public const double ToleranceMeters = 1e-6;

        // Index of the level with the highest elevation <= zMeters +
        // tolerance. If none is below (an excavation, a foundation), the one
        // with the lowest elevation: a negative offset is better than no
        // instance at all. Empty list: -1. On a tie the first one wins, so
        // the choice does not depend on algorithm details but only on the
        // order received.
        //
        // Non-finite values: ArgumentException. A NaN is never "below" and
        // never "the lowest", and would slip through silently.
        public static int Pick(IList<double> elevationsMeters, double zMeters)
        {
            if (elevationsMeters == null) throw new ArgumentNullException("elevationsMeters");
            CheckFinite(zMeters, "zMeters");

            int below = -1;
            int lowest = -1;

            for (int i = 0; i < elevationsMeters.Count; i++)
            {
                double elevation = elevationsMeters[i];
                CheckFinite(elevation, "elevationsMeters[" + i + "]");

                // Strictly greater / less: on a tie the first one stays.
                if (elevation <= zMeters + ToleranceMeters
                    && (below < 0 || elevation > elevationsMeters[below]))
                {
                    below = i;
                }
                if (lowest < 0 || elevation < elevationsMeters[lowest])
                {
                    lowest = i;
                }
            }

            return below >= 0 ? below : lowest;
        }

        private static void CheckFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException(name + " is not finite");
            }
        }
    }
}
