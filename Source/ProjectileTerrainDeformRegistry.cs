using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // Tracks projectiles that should deform terrain upon impact/explosion and applies it at the correct time.
    internal static class ProjectileTerrainDeformRegistry
    {
        private struct Entry { public TerrainDef tdef; public int radius; }
        private static readonly Dictionary<Thing, Entry> map = new Dictionary<Thing, Entry>();

        public static void Register(Thing projectile, TerrainDef tdef, int radius)
        {
            if (projectile == null || tdef == null) return;
            map[projectile] = new Entry { tdef = tdef, radius = radius };
        }

        internal static void TryApply(Thing projectile, IntVec3 center, Map mapContext)
        {
            if (projectile == null) return;
            if (!map.TryGetValue(projectile, out var e)) return;
            map.Remove(projectile);
            PaintTerrainArea(mapContext, center, e.tdef, e.radius);
        }

        // Helper: paint circular area of terrain with radius r around center (r <= 0 paints only center)
        private static void PaintTerrainArea(Map map, IntVec3 center, TerrainDef tdef, int r)
        {
            if (map == null || tdef == null) return;
            if (r <= 0)
            {
                if (center.InBounds(map)) map.terrainGrid.SetTerrain(center, tdef);
                return;
            }
            int r2 = r * r;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue;
                    IntVec3 c = new IntVec3(center.x + dx, 0, center.z + dz);
                    if (c.InBounds(map))
                    {
                        map.terrainGrid.SetTerrain(c, tdef);
                    }
                }
            }
        }
    }
}
