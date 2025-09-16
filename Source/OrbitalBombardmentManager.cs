using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using SaveOurShip2;
using UnityEngine;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    public class OrbitalBombardmentManager : GameComponent
    {
    private const float OrbitalProjectileForcedMissRadius = 35f; // Fallback: higher forced miss for shells/projectiles
    private const float OrbitalLaserForcedMissRadius = 18f; // Fallback: lower forced miss to reflect laser accuracy
    private const float LaserTravelTimePerTile = 10f; // Fallback: faster travel for lasers
    private const float ProjectileTravelTimePerTile = 60f; // Fallback: default travel for projectiles
        private const int LaserMergeWindowTicks = 150; // ~2.5s at 60 TPS; adjust if bursts are longer/shorter

        public class PendingBombardment : IExposable
        {
            public int targetTile;
            public Map targetMap;
            public IntVec3 targetCell;
            public ThingDef projectileDef;
            public float missRadius;
            public bool isLaser;
            public int accBoost;
            public IntVec3 burstLoc;
            public Building_ShipTurret launcherTurret;
            public int ticksRemaining;

            public void ExposeData()
            {
                Scribe_Values.Look(ref targetTile, nameof(targetTile));
                Scribe_References.Look(ref targetMap, nameof(targetMap));
                Scribe_Values.Look(ref targetCell, nameof(targetCell));
                Scribe_Defs.Look(ref projectileDef, nameof(projectileDef));
                Scribe_Values.Look(ref missRadius, nameof(missRadius));
                Scribe_Values.Look(ref isLaser, nameof(isLaser));
                Scribe_Values.Look(ref accBoost, nameof(accBoost));
                Scribe_Values.Look(ref burstLoc, nameof(burstLoc));
                Scribe_References.Look(ref launcherTurret, nameof(launcherTurret));
                Scribe_Values.Look(ref ticksRemaining, nameof(ticksRemaining));
            }
        }

        public static OrbitalBombardmentManager Instance;
        private List<PendingBombardment> active = new List<PendingBombardment>();
        // Post-impact terrain deformation tracking for spawned projectiles
        private struct PendingDeform
        {
            public Thing projectile;
            public TerrainDef tdef;
            public int radius;
            public IntVec3 impactCell;
            public Map map;
        }
        private readonly List<PendingDeform> pendingDeforms = new List<PendingDeform>();
        // Runtime-only state: per-turret merge window for coalescing laser shots into one arrival
        private readonly Dictionary<Building_ShipTurret, int> laserGroupExpireTick = new Dictionary<Building_ShipTurret, int>();

        public OrbitalBombardmentManager(Game game) { Instance = this; }
        public OrbitalBombardmentManager() { Instance = this; }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref active, "active", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit && active == null)
            {
                active = new List<PendingBombardment>();
            }
        }


        public override void GameComponentTick()
        {
            base.GameComponentTick();
            // Process arrivals
            if (active.Any())
            {
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    var b = active[i];
                    if (b.ticksRemaining > 0)
                        b.ticksRemaining--;
                    if (b.ticksRemaining <= 0)
                    {
                        TryArrive(b);
                        active.RemoveAt(i);
                    }
                }
            }

            // Apply terrain deformation for any projectiles that have finished (despawned after impact)
            if (pendingDeforms.Count > 0)
            {
                for (int i = pendingDeforms.Count - 1; i >= 0; i--)
                {
                    var pd = pendingDeforms[i];
                    bool spawned = pd.projectile != null && !pd.projectile.Destroyed && pd.projectile.Spawned;
                    if (spawned)
                    {
                        // Track last known position to paint exactly where it ends up
                        if (pd.projectile.Position.IsValid)
                        {
                            pd.impactCell = pd.projectile.Position;
                            pd.map = pd.projectile.Map ?? pd.map;
                            pendingDeforms[i] = pd; // write back struct
                        }
                        continue;
                    }

                    // Projectile despawned (likely impacted): apply paint at last recorded cell
                    if ((OBMod.Settings == null || OBMod.Settings.enableTerrainDeformation) && pd.map != null && pd.impactCell.IsValid)
                    {
                        PaintTerrainArea(pd.map, pd.impactCell, pd.tdef, pd.radius);
                    }
                    pendingDeforms.RemoveAt(i);
                }
            }
        }

        public void Enqueue(int sourceTile, int targetTile, Map targetMap, IntVec3 targetCell, ThingDef projectileDef, float missRadius, int accBoost, IntVec3 burstLoc, bool isLaser, Building_ShipTurret launcherTurret)
        {
            var dist = Find.WorldGrid.ApproxDistanceInTiles(sourceTile, targetTile);
            // Per-turret override for travel time, else fall back to laser/projectile defaults
            float perTile;
            var turretExt = launcherTurret?.def?.GetModExtension<OrbitalBombardmentTurretExtension>();
            if (turretExt != null && turretExt.travelTimePerTile.HasValue && turretExt.travelTimePerTile.Value > 0f)
            {
                perTile = turretExt.travelTimePerTile.Value;
            }
            else
            {
                perTile = isLaser ? LaserTravelTimePerTile : ProjectileTravelTimePerTile;
            }
            int baseTravelTicks = Mathf.Max(30, (int)(dist * perTile));

            // Establish/extend a short merge window for lasers so early shots don't arrive before later shots enqueue
            int now = Find.TickManager.TicksGame;
            int minArrivalTicks = baseTravelTicks;
            if (isLaser && launcherTurret != null)
            {
                int expire = now + LaserMergeWindowTicks;
                laserGroupExpireTick[launcherTurret] = expire;
                // Enforce floor so arrival cannot occur before the merge window ends
                int floor = expire - now;
                if (floor > minArrivalTicks)
                    minArrivalTicks = floor;
            }

            // DevMode cap applies as a maximum, not minimum
            if (Prefs.DevMode)
                minArrivalTicks = Mathf.Min(minArrivalTicks, 1200); // Removed DevMode cap so in-game ETA reflects real distance & per-tile travel time.

            // If this is a laser, coalesce multiple shots in the same burst into a single pending arrival
            if (isLaser)
            {
                // Merge bursts by turret and target tile only; ignore per-shot differences
                var existing = active.FirstOrDefault(x => x != null
                    && x.isLaser && x.launcherTurret == launcherTurret);
                if (existing != null)
                {
                    // Keep earliest ETA but never before the merge window floor
                    int floor = 0;
                    if (launcherTurret != null && laserGroupExpireTick.TryGetValue(launcherTurret, out var expire))
                    {
                        floor = Math.Max(0, expire - now);
                    }
                    int candidate = minArrivalTicks; // this shot's arrival candidate
                    int earliest = Math.Min(existing.ticksRemaining, candidate);
                    existing.ticksRemaining = Math.Max(earliest, floor);
                    existing.targetMap = targetMap ?? existing.targetMap;
                    existing.targetCell = targetCell; // last target wins
                    return;
                }
            }
            // Determine minimum forced miss radius (per turret override beats class fallback)
            float minMiss;
            if (turretExt != null && turretExt.minForcedMissRadius.HasValue && turretExt.minForcedMissRadius.Value >= 0f)
            {
                minMiss = turretExt.minForcedMissRadius.Value;
            }
            else
            {
                minMiss = (!isLaser) ? OrbitalProjectileForcedMissRadius : OrbitalLaserForcedMissRadius;
            }

            active.Add(new PendingBombardment
            {
                targetTile = targetTile,
                targetMap = targetMap,
                targetCell = targetCell,
                projectileDef = projectileDef,
                // Final miss radius already enforced here (arrival code will now trust this value)
                missRadius = Mathf.Max(missRadius, minMiss),
                isLaser = isLaser,
                accBoost = accBoost,
                burstLoc = burstLoc,
                launcherTurret = launcherTurret,
                ticksRemaining = minArrivalTicks
            });
        }

        public IEnumerable<PendingBombardment> ForMap(Map map)
        {
            int tile = map?.Parent?.Tile ?? -1;
            foreach (var b in active)
            {
                if (b.targetMap == map || (tile >= 0 && b.targetTile == tile))
                    yield return b;
            }
        }

        private void TryArrive(PendingBombardment b)
        {
            try
            {
                var mp = Find.WorldObjects.MapParentAt(b.targetTile);
                var map = b.targetMap ?? mp?.Map;
                if (map == null)
                {
                    Log.Warning($"[SoS2-OB] Arrival aborted: no map at tile {b.targetTile}.");
                    return;
                }

                // Spawn/origin: pick a random cell along the TOP edge so each shot appears from space
                IntVec3 spawnCell = new IntVec3(
                    Rand.Range(0, map.Size.x - 1),
                    0,
                    map.Size.z - 1);

                if (b.isLaser)
                {
                    try
                    {
                        // Scatter for orbital inaccuracy; lasers use a tighter min radius
                        float angleB = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                        // Final miss radius already enforced when queued
                        float minR = b.missRadius;
                        float radiusB = Mathf.Sqrt(Rand.Value) * minR;
                        IntVec3 beamCell = new IntVec3(
                            Mathf.Clamp(Mathf.RoundToInt(b.targetCell.x + radiusB * Mathf.Cos(angleB)), 0, map.Size.x - 1),
                            0,
                            Mathf.Clamp(Mathf.RoundToInt(b.targetCell.z + radiusB * Mathf.Sin(angleB)), 0, map.Size.z - 1));

                        // Resolve custom beam ThingDef from projectile or turret
                        ThingDef beamDef = null;
                        var projExt = b.projectileDef?.GetModExtension<OrbitalBeamDefExtension>();
                        if (projExt != null)
                        {
                            beamDef = projExt.beamDef ?? (!string.IsNullOrEmpty(projExt.beamDefName) ? DefDatabase<ThingDef>.GetNamedSilentFail(projExt.beamDefName) : null);
                        }
                        if (beamDef == null)
                        {
                            var turretExt = b.launcherTurret?.def?.GetModExtension<OrbitalBeamDefExtension>();
                            if (turretExt != null)
                            {
                                beamDef = turretExt.beamDef ?? (!string.IsNullOrEmpty(turretExt.beamDefName) ? DefDatabase<ThingDef>.GetNamedSilentFail(turretExt.beamDefName) : null);
                            }
                        }
                        beamDef ??= ThingDefOf.PowerBeam;

                        var thing = GenSpawn.Spawn(beamDef, beamCell, map);
                        if (thing is OrbitalStrike powerBeam)
                        {
                            powerBeam.instigator = null;
                            // Apply duration override if provided on the extension (projectile or turret)
                            int? durationOverride = null;
                            var projExtDur = b.projectileDef?.GetModExtension<OrbitalBeamDefExtension>();
                            if (projExtDur != null && projExtDur.durationTicks.HasValue)
                                durationOverride = projExtDur.durationTicks.Value;
                            if (!durationOverride.HasValue)
                            {
                                var turretExtDur = b.launcherTurret?.def?.GetModExtension<OrbitalBeamDefExtension>();
                                if (turretExtDur != null && turretExtDur.durationTicks.HasValue)
                                    durationOverride = turretExtDur.durationTicks.Value;
                            }
                            powerBeam.duration = durationOverride ?? 1200;
                            // Odyssey terrain deformation: spinal -> LavaDeep, non-spinal -> LavaShallow (if available)
                            bool isSpinal = b.launcherTurret != null && b.launcherTurret.spinalComp != null;
                            if (thing is MovingPowerBeam mpb)
                            {
                                var deep = DefDatabase<TerrainDef>.GetNamedSilentFail("LavaDeep");
                                var shallow = DefDatabase<TerrainDef>.GetNamedSilentFail("LavaShallow");
                                var choice = isSpinal ? deep : shallow;
                                if (choice != null)
                                {
                                    mpb.EnableTerrainDeformation(choice);
                                }
                            }
                            powerBeam.StartStrike();
                        }
                        else
                        {
                            Log.Warning("[SoS2-OB] Expected PowerBeam thing but got different type.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[SoS2-OB] PowerBeam spawn failed: {ex}");
                    }
                }
                else
                {
                    // Explosive and other projectiles: overhead mortar-like launch
                    var newProjectile = (Projectile)GenSpawn.Spawn(b.projectileDef, spawnCell, map);

                    // Orbital projectiles: keep a consistent forced miss without inflating by range/accuracy
                    // Final spread already enforced when queued
                    float spread = b.missRadius;

                    float angle = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                    float radius = Mathf.Sqrt(Rand.Value) * spread; // sqrt for center bias
                    IntVec3 impactCell = new IntVec3(
                        Mathf.Clamp(Mathf.RoundToInt(b.targetCell.x + radius * Mathf.Cos(angle)), 0, map.Size.x - 1),
                        0,
                        Mathf.Clamp(Mathf.RoundToInt(b.targetCell.z + radius * Mathf.Sin(angle)), 0, map.Size.z - 1));

                    newProjectile.Launch(
                        b.launcherTurret ?? (Thing)null,
                        spawnCell.ToVector3Shifted(),
                        impactCell,
                        impactCell,
                        ProjectileHitFlags.IntendedTarget,
                        equipment: b.launcherTurret);

                    // Odyssey terrain deformation on projectile impact: spinal -> LavaDeep, non-spinal -> LavaShallow
                    bool isSpinal = b.launcherTurret != null && b.launcherTurret.spinalComp != null;
                    var deep = DefDatabase<TerrainDef>.GetNamedSilentFail("LavaDeep");
                    var shallow = DefDatabase<TerrainDef>.GetNamedSilentFail("LavaShallow");
                    var tdef = isSpinal ? deep : shallow;
                    if (tdef != null)
                    {
                        // Register terrain deformation to apply when the projectile despawns (post-impact)
                        int paintR = 0;
                        var projProps = b.projectileDef?.projectile;
                        if (projProps != null && projProps.explosionRadius > 0f)
                        {
                            paintR = Mathf.FloorToInt(projProps.explosionRadius * 0.5f);
                        }
                        RegisterProjectileDeform(newProjectile, tdef, paintR, impactCell, map);
                    }
                }

                Messages.Message("Orbital bombardment impact!", new GlobalTargetInfo(b.targetCell, map), MessageTypeDefOf.ThreatSmall);
            }
            catch (Exception e)
            {
                Log.Error($"[SoS2-OB] Bombardment arrival failed: {e}");
            }
        }

        private static IntVec3 FindClosestEdgeCellLowSpread(Map map, IntVec3 targetCell)
        {
            Rot4 dir = FindProjectileSpawnDirection(map, targetCell);
            if (dir == Rot4.North || dir == Rot4.South)
            {
                int x = targetCell.x + Rand.Range(-map.Size.x / 4, map.Size.x / 4);
                x = Mathf.Clamp(x, 0, map.Size.x - 1);
                int z = dir == Rot4.North ? map.Size.z - 1 : 0;
                return new IntVec3(x, 0, z);
            }
            if (dir == Rot4.West || dir == Rot4.East)
            {
                int z = targetCell.z + Rand.Range(-map.Size.z / 4, map.Size.z / 4);
                z = Mathf.Clamp(z, 0, map.Size.z - 1);
                int x = dir == Rot4.East ? map.Size.x - 1 : 0;
                return new IntVec3(x, 0, z);
            }
            return CellFinder.RandomEdgeCell(map);
        }

        private static Rot4 FindProjectileSpawnDirection(Map map, IntVec3 targetCell)
        {
            if (targetCell.x < map.Size.x / 2 && targetCell.x < targetCell.z && targetCell.x < (map.Size.z) - targetCell.z)
                return Rot4.West;
            if (targetCell.x > map.Size.x / 2 && map.Size.x - targetCell.x < targetCell.z && map.Size.x - targetCell.x < (map.Size.z) - targetCell.z)
                return Rot4.East;
            if (targetCell.z > map.Size.z / 2)
                return Rot4.North;
            return Rot4.South;
        }

        public void RegisterProjectileDeform(Thing projectile, TerrainDef tdef, int radius, IntVec3 impactCell, Map map)
        {
            if (projectile == null || tdef == null || map == null) return;
            pendingDeforms.Add(new PendingDeform
            {
                projectile = projectile,
                tdef = tdef,
                radius = Mathf.Max(0, radius),
                impactCell = impactCell,
                map = map
            });
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
