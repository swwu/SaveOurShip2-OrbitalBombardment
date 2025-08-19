using RimWorld;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // Add this to a projectile or turret ThingDef via <modExtensions> to select a custom PowerBeam ThingDef
    public class OrbitalBeamDefExtension : DefModExtension
    {
        public ThingDef beamDef;       // Direct reference to a ThingDef (preferred)
        public string beamDefName;     // Optional: defName fallback if direct reference isn’t used
    public int? durationTicks;     // Optional: override beam duration in ticks
    }
}
