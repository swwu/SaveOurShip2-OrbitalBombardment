using RimWorld;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // Attach to a beam ThingDef to enable movement/explosions and tune their parameters
    public class MovingPowerBeamExtension : DefModExtension
    {
        public bool enableMovement = false;
        // Path type: "arc" or "line"
        public string path = "arc";
        public float arcBulgeFactor = 0.3f;          // fraction of distance used as arc bulge amplitude
        public float speedCellsPerSec = 2f;          // movement speed along path (cells/sec)

        public int explosionEveryTicks = 0;          // 0 = disabled; otherwise explode every N ticks
        public float explosionRadius = 1.5f;
        public int explosionDamage = 25;
        public DamageDef explosionDamageDef;         // Optional direct ref
        public string explosionDamageDefName;        // Or defName fallback
    }
}
