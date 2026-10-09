using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CEM.Utils;
using MNL;
using OpenTK;

namespace CEM.Client.ZoneExporter
{
  /// <summary>
  /// Zone Boundaries
  /// </summary>
  partial class Zone2Obj
  {

    void ExportBounds()
    {
      // Parse bounds from file
      var stream = ClientData.FindCSV(Zone, "bound.csv");
      if (stream == null)
      {
        Log.Warn("No bound.csv found for zone id " + ZoneID);
        return;
      }

      List<List<Vector2>> zoneBounds = new List<List<Vector2>>();
      using (TextReader reader = new StreamReader(stream)) {
        string input;

        while ((input = reader.ReadLine()) != null)
        {
          if (input.Trim() == string.Empty) continue;
          string[] data = input.Split(',');
          var points = new List<Vector2>();

          // should be x, y, x, y, ...
          Debug.Assert(data.Length % 2 == 0);
          Debug.Assert(data.Length >= 6); // atleast 6 values needed (id, count, x1, y1, x2, y2)?

          // data[1] is the number of points; the x,y pairs follow. The old loop stopped at
          // index cnt instead of 2 + 2 * cnt, so only the first half of every wall was built and
          // bots walked through the rest (2026-10-06 audit: 31,855 of 56,212 crossings open).
          var cnt = int.Parse(data[1]);
          if (data.Length < 2 + cnt * 2)
          {
            Log.Warn($"bound.csv in zone {ZoneID} lists {cnt} points but has {(data.Length - 2) / 2}; using those");
            cnt = (data.Length - 2) / 2;
          }

          for (int i = 0; i < cnt; i++)
          {
            points.Add(new Vector2(float.Parse(data[2 + i * 2], CultureInfo.InvariantCulture) + Zone.XOffset,
              float.Parse(data[3 + i * 2], CultureInfo.InvariantCulture) + Zone.YOffset));
          }

          zoneBounds.Add(points);
        }
      }

      // Generate walls
      float minZ = 0.0f;
      float maxZ = (Zone.OffsetMapScaleFactor * 255 + Zone.TerrainMapScaleFactor * 255) + 1000;
      foreach (var boundary in zoneBounds)
      {
        for (int i = 0; i < boundary.Count - 1; i++)
        {
          MakeBoundingQuad(boundary[i], boundary[i + 1], minZ, maxZ);
        }
      }
    }

    private void MakeBoundingQuad(Vector2 p1, Vector2 p2, float minZ, float maxZ)
    {
      var vertices = new Vector3[4];
      var triangles = new Triangle[2];

      // bottom left
      vertices[0] = new Vector3(p1.X, p1.Y, minZ);
      // top right
      vertices[1] = new Vector3(p2.X, p2.Y, maxZ);
      // top left
      vertices[2] = new Vector3(p1.X, p1.Y, maxZ);
      // bottom right
      vertices[3] = new Vector3(p2.X, p2.Y, minZ);

      triangles[0] = new Triangle(0, 1, 2);
      triangles[1] = new Triangle(0, 3, 1);

      ObjWriter.AddMesh(vertices, triangles);
    }
  }
}
