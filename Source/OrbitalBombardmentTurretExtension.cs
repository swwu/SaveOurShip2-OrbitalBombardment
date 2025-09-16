using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // Attach to a ship turret ThingDef to override orbital bombardment parameters.
    // Example XML usage:
    // <modExtensions>
    //   <li Class="SaveOurShip2_OrbitalBombardment.OrbitalBombardmentTurretExtension">
    //     <travelTimePerTile>45</travelTimePerTile>
    //     <minForcedMissRadius>22</minForcedMissRadius>
    //   </li>
    // </modExtensions>
    public class OrbitalBombardmentTurretExtension : DefModExtension
    {
        // If set, overrides per-tile travel time multiplier (ticks per world-tile of distance)
        public float? travelTimePerTile;
        // If set, overrides the minimum forced miss radius enforced for this turret's orbital strikes
        public float? minForcedMissRadius;
        // Explicit classification of this turret's projectile as a laser-type orbital (affects travel speed & miss radius defaults)
        public bool isLaser = false;
    }
}
