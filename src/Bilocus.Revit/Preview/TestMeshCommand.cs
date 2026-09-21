// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Roberto Dolfini

using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Bilocus.Geometry;

namespace Bilocus.Revit.Preview
{
    // Injects a synthetic cube into the GeometryStore, without going through
    // the network. Used to separate drawing problems from network ones: if
    // the cube shows up, DirectContext3D works and the defect is upstream.
    [Transaction(TransactionMode.Manual)]
    public sealed class TestMeshCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            if (uidoc == null || !(uidoc.ActiveView is View3D))
            {
                TaskDialog.Show("Bilocus", "Open a 3D view.");
                return Result.Cancelled;
            }

            if (App.Current == null || App.Current.Store == null)
            {
                TaskDialog.Show("Bilocus", "Store not initialized.");
                return Result.Cancelled;
            }

            // 2-meter cube, centered on the origin
            float[] positions = new float[]
            {
                -1,-1,-1,  1,-1,-1,  1, 1,-1,  -1, 1,-1,
                -1,-1, 1,  1,-1, 1,  1, 1, 1,  -1, 1, 1
            };
            float[] normals = new float[24];
            for (int v = 0; v < 8; v++)
            {
                double length = Math.Sqrt(3.0);
                normals[v * 3] = (float)(positions[v * 3] / length);
                normals[v * 3 + 1] = (float)(positions[v * 3 + 1] / length);
                normals[v * 3 + 2] = (float)(positions[v * 3 + 2] / length);
            }
            int[] indices = new int[]
            {
                0,2,1, 0,3,2,  4,5,6, 4,6,7,
                0,1,5, 0,5,4,  1,2,6, 1,6,5,
                2,3,7, 2,7,6,  3,0,4, 3,4,7
            };

            List<MeshChunk> chunks = MeshChunker.Split(positions, normals, indices);
            App.Current.Store.Upsert(
                "__test__", "Test Cube", chunks,
                new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 },
                new float[] { 1f, 0.4f, 0f, 1f });

            uidoc.UpdateAllOpenViews();
            TaskDialog.Show("Bilocus",
                "Test cube injected: 2 meters per side on the internal origin.\n" +
                "If it does not show up, the problem is in rendering, not in the network.");
            return Result.Succeeded;
        }
    }
}
