using System.Linq;
using UnityEngine;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    public class OrbitalBombardmentMapOverlay : MapComponent
    {
        public OrbitalBombardmentMapOverlay(Map map) : base(map) { }

        public override void MapComponentOnGUI()
        {
            base.MapComponentOnGUI();
            var mgr = OrbitalBombardmentManager.Instance;
            if (mgr == null) return;
            var pendings = mgr.ForMap(map).ToList();
            if (pendings.Count == 0) return;

            // Group entries within 10 seconds (600 ticks) to avoid screen spam
            const int bucketSize = 600;
            var grouped = pendings
                .GroupBy(p => Mathf.FloorToInt(p.ticksRemaining / (float)bucketSize))
                .Select(g => new { Count = g.Count(), MinTicks = g.Min(p => p.ticksRemaining) })
                .OrderBy(e => e.MinTicks)
                .ToList();

            float y = 80f;
            foreach (var g in grouped)
            {
                string text = $"Orbital bombardment incoming: {g.Count} in {FormatETA(g.MinTicks)}";
                var rect = new Rect(15f, y, Text.CalcSize(text).x + 12f, 24f);
                Widgets.Label(rect, text);
                y += 24f;
            }
        }

        private static string FormatETA(int ticks)
        {
            // RimWorld: 60,000 ticks per day; 2,500 per hour; ~41.67 per minute
            if (ticks < 0) ticks = 0;
            int hours = ticks / 2500;
            int rem = ticks % 2500;
            int minutes = Mathf.FloorToInt(rem / 41.667f);
            if (hours > 0)
                return $"{hours}h {minutes}m";
            return $"{minutes}m";
        }
    }
}
