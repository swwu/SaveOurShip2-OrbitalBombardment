using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // A PowerBeam that can drift randomly and optionally create explosions periodically
    public class MovingPowerBeam : OrbitalStrike
    {
        // Path state
        private Vector3 originCenter;          // initial center (world)
        private Vector3 targetCenter;          // chosen destination (world)
        private float pathLength;              // straight-line length
        private float t;                       // 0..1 progress along path
        private float speedCellsPerTick;       // movement speed
        private bool moveEnabled;              // movement enabled by extension
        private bool arcPath;                  // true = arc, false = line
        private float arcBulge;                // arc amplitude (world units)

        // Explosion state
        private int tickCounter;
        private int explosionEveryTicks;
        private float explosionRadius;
        private int explosionDamage;
        private DamageDef explosionDamageDef;

        // Visual/Fire ring state
        private float beamWidth;               // from CompProperties_OrbitalBeam.width

        // Lava deformation (Odyssey DLC) when strike occurs (e.g., on explosion ticks)
        private bool lavaDeformEnabled;
        private TerrainDef lavaDef;            // cached LavaDeep/LavaShallow def if available
        private IntVec3 lastPaintCell = IntVec3.Invalid; // last painted cell to avoid repeats
        private int paintRadiusCells;          // scaled by beam width

        private const int FiresStartedPerTick = 4;
        private static readonly IntRange FlameDamageAmountRange = new IntRange(65, 100);
        private static readonly IntRange CorpseFlameDamageAmountRange = new IntRange(5, 10);
        private static List<Thing> tmpThings = new List<Thing>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref originCenter, nameof(originCenter));
            Scribe_Values.Look(ref targetCenter, nameof(targetCenter));
            Scribe_Values.Look(ref pathLength, nameof(pathLength));
            Scribe_Values.Look(ref t, nameof(t));
            Scribe_Values.Look(ref speedCellsPerTick, nameof(speedCellsPerTick));
            Scribe_Values.Look(ref moveEnabled, nameof(moveEnabled));
            Scribe_Values.Look(ref arcPath, nameof(arcPath));
            Scribe_Values.Look(ref arcBulge, nameof(arcBulge));
            Scribe_Values.Look(ref tickCounter, nameof(tickCounter));
            Scribe_Values.Look(ref explosionEveryTicks, nameof(explosionEveryTicks));
            Scribe_Values.Look(ref explosionRadius, nameof(explosionRadius));
            Scribe_Values.Look(ref explosionDamage, nameof(explosionDamage));
            Scribe_Defs.Look(ref explosionDamageDef, nameof(explosionDamageDef));
            Scribe_Values.Look(ref lavaDeformEnabled, nameof(lavaDeformEnabled));
            Scribe_Defs.Look(ref lavaDef, nameof(lavaDef));
        }

        // Preferred: explicitly set a terrain to paint while striking (e.g., LavaDeep, LavaShallow)
        public void EnableTerrainDeformation(TerrainDef terrain)
        {
            lavaDef = terrain;
            lavaDeformEnabled = lavaDef != null;
        }

        // Back-compat helper: look up LavaDeep by name if requested
        public void EnableLavaDeformationIfAvailable(bool enable)
        {
            if (!enable)
            {
                lavaDeformEnabled = false;
                lavaDef = null;
                return;
            }
            EnableTerrainDeformation(DefDatabase<TerrainDef>.GetNamedSilentFail("LavaDeep"));
        }

        public override void StartStrike()
        {
            base.StartStrike();

            var ext = def.GetModExtension<MovingPowerBeamExtension>();

            // Cache beam width from the orbital beam comp for scaling visuals
            var ob = def.comps?.Find(c => c is CompProperties_OrbitalBeam) as CompProperties_OrbitalBeam;
            beamWidth = ob != null && ob.width > 0f ? ob.width : 3f;
            paintRadiusCells = Mathf.Max(0, Mathf.RoundToInt(beamWidth * 0.5f));

            // Configure explosions
            if (ext != null && ext.explosionEveryTicks > 0)
            {
                explosionEveryTicks = ext.explosionEveryTicks;
                explosionRadius = Mathf.Max(0.1f, ext.explosionRadius);
                explosionDamage = Mathf.Max(1, ext.explosionDamage);
                explosionDamageDef = ext.explosionDamageDef ?? (!string.IsNullOrEmpty(ext.explosionDamageDefName) ? DefDatabase<DamageDef>.GetNamedSilentFail(ext.explosionDamageDefName) : DamageDefOf.Bomb);
            }
            else
            {
                explosionEveryTicks = 0;
            }

            // Configure movement
            moveEnabled = ext != null && ext.enableMovement;
            if (moveEnabled)
            {
                originCenter = Position.ToVector3Shifted();

                // Choose a path length based on speed and beam duration so it traverses for the full strike
                speedCellsPerTick = Mathf.Max(0.01f, ext.speedCellsPerSec) / 60f;
                float desiredDistance = Mathf.Max(1f, speedCellsPerTick * duration);
                float ang = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                Vector3 candidate = originCenter + new Vector3(Mathf.Cos(ang) * desiredDistance, 0f, Mathf.Sin(ang) * desiredDistance);
                IntVec3 candCell = candidate.ToIntVec3();
                if (!candCell.InBounds(Map))
                {
                    candCell = candCell.ClampInsideMap(Map);
                }
                targetCenter = candCell.ToVector3Shifted();

                pathLength = Vector3.Distance(originCenter, targetCenter);
                arcPath = string.Equals(ext.path, "arc", StringComparison.OrdinalIgnoreCase);
                arcBulge = Mathf.Clamp(ext.arcBulgeFactor, -1f, 1f) * pathLength; // scale bulge with distance
                t = 0f;
            }
        }

        protected override void Tick()
        {
            // Update position first so base PowerBeam effects use the new center this tick
            if (!Destroyed)
            {
                // Movement along path
                if (moveEnabled && speedCellsPerTick > 0f && pathLength > 0.001f)
                {
                    float dt = speedCellsPerTick / pathLength; // normalize speed into t-per-tick
                    t = Mathf.Min(1f, t + dt);

                    Vector3 p;
                    if (arcPath)
                    {
                        // Quadratic Bezier from origin to target with a perpendicular bulge at mid-point
                        Vector3 a = originCenter;
                        Vector3 c = targetCenter;
                        Vector3 mid = (a + c) * 0.5f;
                        Vector3 dir = (c - a); dir.y = 0f;
                        Vector3 perp = new Vector3(-dir.z, 0f, dir.x).normalized;
                        Vector3 b = mid + perp * arcBulge;
                        p = Bezier(a, b, c, t);
                    }
                    else
                    {
                        p = Vector3.Lerp(originCenter, targetCenter, t);
                    }

                    IntVec3 cell = p.ToIntVec3();
                    if (cell.InBounds(Map))
                    {
                        Position = cell;
                        // Paint lava continuously as the beam moves, when entering a new cell
                        if ((OBMod.Settings == null || OBMod.Settings.enableTerrainDeformation) && lavaDeformEnabled && lavaDef != null && cell != lastPaintCell)
                        {
                            PaintLavaArea(cell);
                            lastPaintCell = cell;
                        }
                    }
                }
            }

            base.Tick();
            if (Destroyed) return;
            for (int i = 0; i < FiresStartedPerTick; i++)
            {
                StartRandomFireAndDoFlameDamage();
            }

            tickCounter++;

            // Periodic explosion at current center
            if (explosionEveryTicks > 0 && tickCounter % explosionEveryTicks == 0)
            {
                GenExplosion.DoExplosion(Position, Map, explosionRadius, explosionDamageDef ?? DamageDefOf.Bomb, instigator, explosionDamage, armorPenetration: 0f,
                    weapon: weaponDef, projectile: null, intendedTarget: null, postExplosionSpawnThingDef: null, postExplosionSpawnChance: 0f);

                // If enabled and available, deform the impacted terrain (area) to lava
                if ((OBMod.Settings == null || OBMod.Settings.enableTerrainDeformation) && lavaDeformEnabled && lavaDef != null)
                {
                    var cell = Position;
                    if (cell.InBounds(Map))
                    {
                        PaintLavaArea(cell);
                        lastPaintCell = cell;
                    }
                }
            }
        }

        private void StartRandomFireAndDoFlameDamage()
        {
            IntVec3 c = (from x in GenRadial.RadialCellsAround(base.Position, 15f, useCenter: true)
                         where x.InBounds(base.Map)
                         select x).RandomElementByWeight((IntVec3 x) => 1f - Mathf.Min(x.DistanceTo(base.Position) / 15f, 1f) + 0.05f);
            FireUtility.TryStartFireIn(c, base.Map, Rand.Range(0.1f, 0.925f), instigator);
            tmpThings.Clear();
            tmpThings.AddRange(c.GetThingList(base.Map));
            for (int num = 0; num < tmpThings.Count; num++)
            {
                int num2 = ((tmpThings[num] is Corpse) ? CorpseFlameDamageAmountRange.RandomInRange : FlameDamageAmountRange.RandomInRange);
                Pawn pawn = tmpThings[num] as Pawn;
                BattleLogEntry_DamageTaken battleLogEntry_DamageTaken = null;
                if (pawn != null)
                {
                    battleLogEntry_DamageTaken = new BattleLogEntry_DamageTaken(pawn, RulePackDefOf.DamageEvent_PowerBeam, instigator as Pawn);
                    Find.BattleLog.Add(battleLogEntry_DamageTaken);
                }
                tmpThings[num].TakeDamage(new DamageInfo(DamageDefOf.Flame, num2, 0f, -1f, instigator, null, weaponDef)).AssociateWithLog(battleLogEntry_DamageTaken);
            }
            tmpThings.Clear();
        }

        private static Vector3 Bezier(Vector3 a, Vector3 b, Vector3 c, float t)
        {
            float u = 1f - t;
            return u * u * a + 2f * u * t * b + t * t * c;
        }

        private void PaintLavaArea(IntVec3 center)
        {
            if (Map == null || lavaDef == null) return;
            // If paint radius is 0, set just the center cell
            if (paintRadiusCells <= 0)
            {
                Map.terrainGrid.SetTerrain(center, lavaDef);
                return;
            }
            int r = paintRadiusCells;
            int r2 = r * r;
            for (int dz = -r; dz <= r; dz++)
            {
                for (int dx = -r; dx <= r; dx++)
                {
                    if (dx * dx + dz * dz > r2) continue; // circle mask
                    IntVec3 c = new IntVec3(center.x + dx, 0, center.z + dz);
                    if (c.InBounds(Map))
                    {
                        Map.terrainGrid.SetTerrain(c, lavaDef);
                    }
                }
            }
        }
    }
}
