using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using SaveOurShip2;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace SaveOurShip2_OrbitalBombardment
{
    // Holds per-turret queued bombardment target so we can drive firing from turret Tick.
    internal static class OrbitalBombardmentState
    {
        private struct Target
        {
            public Map map;
            public IntVec3 cell;
        }
        private static readonly Dictionary<Building_ShipTurret, Target> targets = new Dictionary<Building_ShipTurret, Target>();

        public static void SetTarget(Building_ShipTurret turret, Map targetMap, IntVec3 targetCell)
        {
            if (turret == null || targetMap == null) return;
            targets[turret] = new Target { map = targetMap, cell = targetCell };
        }
        public static bool TryGet(Building_ShipTurret turret, out Map map, out IntVec3 cell)
        {
            if (turret != null && targets.TryGetValue(turret, out var t))
            {
                map = t.map; cell = t.cell; return true;
            }
            map = null; cell = IntVec3.Invalid; return false;
        }
        public static void Clear(Building_ShipTurret turret)
        {
            if (turret != null) targets.Remove(turret);
        }
        public static bool HasTarget(Building_ShipTurret turret)
        {
            return turret != null && targets.ContainsKey(turret);
        }
    }

    [HarmonyPatch(typeof(Building_ShipTurret), "Tick")]
    public static class Patch_ShipTurret_Tick_Bombardment
    {
        public static void Postfix(Building_ShipTurret __instance)
        {
            // Auto-end any lingering redirect session once the burst is over
            if (OrbitalRedirectSession.Active && OrbitalRedirectSession.SourceTurret == __instance)
            {
                var v = __instance.AttackVerb;
                if (v == null || v.state != VerbState.Bursting)
                {
                    OrbitalRedirectSession.End();
                }
            }
            // Only act if a bombardment target is queued
            if (!OrbitalBombardmentState.TryGet(__instance, out var targetMap, out var targetCell)) return;

            // Respect basic availability similar to Tick(): avoid protected IsStunned; use public Active flag instead
            if (!__instance.Spawned) { return; }
            if (!__instance.Active) { return; }
            if (__instance.GunCompEq?.PrimaryVerb == null) { OrbitalBombardmentState.Clear(__instance); return; }
            if (__instance.PlayerControlled && __instance.holdFire) { OrbitalBombardmentState.Clear(__instance); return; }
            if (__instance.PointDefenseMode) { OrbitalBombardmentState.Clear(__instance); return; }
            if (!__instance.AttackVerb.Available()) { OrbitalBombardmentState.Clear(__instance); return; }

            // If on cooldown or mid-burst, let normal cycle progress
            if (__instance.AttackVerb.state == VerbState.Bursting) { return; }
            if (__instance.burstCooldownTicksLeft > 0) { return; }

            // Queue once, then clear to avoid repeated volleys
            OrbitalBombardmentState.Clear(__instance);

            // Prepare verb targeting. For spinal weapons, match BeginBurst straight-fire to exact edge based on rotation.
            var verb = __instance.AttackVerb as Verb_LaunchProjectileShip;
            IntVec3 edgeCell;
            var map = __instance.Map;
            if (__instance.spinalComp != null)
            {
                // Mirror lines 576–583 of BeginBurst: straight to edge based on barrel rotation
                byte rot = __instance.Rotation.AsByte;
                if (rot == 0) // north
                    edgeCell = new IntVec3(__instance.Position.x, 0, map.Size.z - 1);
                else if (rot == 1) // east
                    edgeCell = new IntVec3(map.Size.x - 1, 0, __instance.Position.z);
                else if (rot == 2) // south
                    edgeCell = new IntVec3(__instance.Position.x, 0, 1);
                else // west
                    edgeCell = new IntVec3(1, 0, __instance.Position.z);
            }
            else
            {
                // Non-spinal: approximate MapEdgeCell(5) with a small deviation based on ship heading
                const int miss = 5;
                int mx = __instance.Position.x;
                int mz = __instance.Position.z;
                int dx = Rand.RangeInclusive(-miss, miss);
                int dz = Rand.RangeInclusive(-miss, miss);
                var comp = __instance.mapComp;
                if (((comp.EngineRot == 0 && comp.Heading != -1) || (comp.EngineRot == 2 && comp.Heading == -1))) // north
                    edgeCell = new IntVec3(Mathf.Clamp(mx + dx, 0, map.Size.x - 1), 0, map.Size.z - 1);
                else if ((comp.EngineRot == 1 && comp.Heading != -1) || (comp.EngineRot == 3 && comp.Heading == -1)) // east
                    edgeCell = new IntVec3(map.Size.x - 1, 0, Mathf.Clamp(mz + dz, 0, map.Size.z - 1));
                else if ((comp.EngineRot == 2 && comp.Heading != -1) || (comp.EngineRot == 0 && comp.Heading == -1)) // south
                    edgeCell = new IntVec3(Mathf.Clamp(mx + dx, 0, map.Size.x - 1), 0, 0);
                else // west
                    edgeCell = new IntVec3(0, 0, Mathf.Clamp(mz + dz, 0, map.Size.z - 1));
            }
            if (verb != null)
            {
                verb.shipTarget = new LocalTargetInfo(edgeCell);
            }

            // Replicate BeginBurst resource/payment side effects before casting
            // Power check and battery draw
            var powerComp = __instance.powerComp;
            if (powerComp != null && powerComp.PowerNet != null)
            {
                float need = __instance.EnergyToFire;
                float available = powerComp.PowerNet.CurrentStoredEnergy();
                if (available < need) return; // not enough power, abort
                foreach (var bat in powerComp.PowerNet.batteryComps)
                {
                    float draw = Mathf.Min(need * bat.StoredEnergy / available, bat.StoredEnergy);
                    bat.DrawPower(draw);
                }
            }

            // Heat check and application
            var heatComp = __instance.heatComp;
            if (heatComp != null && heatComp.Props.heatPerPulse > 0)
            {
                if (!heatComp.AddHeatToNetwork(__instance.HeatToFire)) return; // cannot vent/apply heat
            }

            // Ammo/fuel consumption
            var fuelComp = __instance.fuelComp;
            if (fuelComp != null)
            {
                if (fuelComp.Fuel <= 0f) return;
                fuelComp.ConsumeFuel(1);
            }

            // SFX
            heatComp?.Props?.singleFireSound?.PlayOneShot(__instance);

            // Start redirect session for this turret/target
            OrbitalRedirectSession.Begin(__instance, targetMap, targetCell);

            // Use the turret’s normal burst initiation path akin to BeginBurst() gating
            // Aim at a local edge cell and try to cast one burst
            var localTarget = new LocalTargetInfo(edgeCell);
            bool cast = __instance.AttackVerb.TryStartCastOn(localTarget, false, true, false);
        }
    }

    internal static class RegisterRedirectHelper
    {
        public static bool ShouldRedirect(Building_ShipTurret turret)
        {
            if (turret == null) return false;
            // Only redirect for player-owned turrets; enemy ship turrets should behave normally.
            if (turret.Faction == null || !turret.Faction.IsPlayer) return false;
            return OrbitalRedirectSession.Active || OrbitalBombardmentState.HasTarget(turret);
        }

        public static void CreateWorldObject(Verb_LaunchProjectileShip verb, Building_ShipTurret turret, ThingDef spawnProjectile, IntVec3 burstLoc, float? missRadiusOpt = null, int? accBoostOpt = null)
        {
            var targetMap = OrbitalRedirectSession.TargetMap;
            var targetCell = OrbitalRedirectSession.TargetCell;
            var sourceTile = turret.Map.Parent.Tile;
            var targetTile = targetMap.Parent.Tile;
            bool isLaser = turret.def?.GetModExtension<OrbitalBombardmentTurretExtension>()?.isLaser ?? false;

            OrbitalBombardmentManager.Instance?.Enqueue(
                sourceTile,
                targetTile,
                targetMap,
                targetCell,
                spawnProjectile,
                missRadiusOpt ?? verb.verbProps.ForcedMissRadius,
                accBoostOpt ?? (turret.heatComp?.myNet?.AccuracyBoost ?? 0),
                burstLoc,
                isLaser,
                turret);
        }
    }

    // Session-scoped state: when non-null, any RegisterProjectile will be redirected into an orbital world object.
    internal static class OrbitalRedirectSession
    {
        public static Building_ShipTurret SourceTurret { get; private set; }
        public static Map TargetMap { get; private set; }
        public static IntVec3 TargetCell { get; private set; }
        public static bool Active => SourceTurret != null && TargetMap != null;

        public static void Begin(Building_ShipTurret turret, Map targetMap, IntVec3 targetCell)
        {
            SourceTurret = turret;
            TargetMap = targetMap;
            TargetCell = targetCell;
        }

        public static void End()
        {
            SourceTurret = null;
            TargetMap = null;
        }
    }

    [HarmonyPatch(typeof(Verb_LaunchProjectileShip), nameof(Verb_LaunchProjectileShip.RegisterProjectile))]
    public static class Patch_RegisterProjectile_ToWorldObject
    {
        // Prevent normal ship-combat registration and create a traveling world object instead when session is active.
        public static bool Prefix(Verb_LaunchProjectileShip __instance, Building_ShipTurret turret, LocalTargetInfo target, ThingDef spawnProjectile, IntVec3 burstLoc)
        {
            // Only redirect if this projectile belongs to the exact source turret currently in session
            bool should = RegisterRedirectHelper.ShouldRedirect(turret);
            // Try to resolve owning turret from the verb up-front for recovery
            Building_ShipTurret ownerFromVerbEarly = null;
            try
            {
                var fieldsEarly = __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var f in fieldsEarly)
                {
                    if (typeof(Building_ShipTurret).IsAssignableFrom(f.FieldType))
                    {
                        ownerFromVerbEarly = f.GetValue(__instance) as Building_ShipTurret;
                        if (ownerFromVerbEarly != null) break;
                    }
                }
            }
            catch { }

            if (!should || turret != OrbitalRedirectSession.SourceTurret)
            {
                // Try to resolve owning turret from the verb itself
                try
                {
                    Building_ShipTurret ownerFromVerb = ownerFromVerbEarly;
                    // 1) Look for any private field of type Building_ShipTurret
                    if (ownerFromVerb == null)
                    {
                        var fields = __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        foreach (var f in fields)
                        {
                            if (typeof(Building_ShipTurret).IsAssignableFrom(f.FieldType))
                            {
                                ownerFromVerb = f.GetValue(__instance) as Building_ShipTurret;
                                if (ownerFromVerb != null) break;
                            }
                        }
                    }
                    // 2) Try Verb base 'Caster' property and walk up holders
                    if (ownerFromVerb == null)
                    {
                        var casterProp = typeof(Verb).GetProperty("Caster", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        var caster = casterProp?.GetValue(__instance) as Thing;
                        Thing holder = caster;
                        int hops = 0;
                        while (holder != null && hops++ < 6)
                        {
                            if (holder is Building_ShipTurret bst)
                            {
                                ownerFromVerb = bst; break;
                            }
                            holder = holder.ParentHolder as Thing;
                        }
                    }
                    // Recovery A: if verb's owner matches the active session's turret, redirect immediately using the active session
                    if (ownerFromVerb != null && ownerFromVerb == OrbitalRedirectSession.SourceTurret && OrbitalRedirectSession.Active)
                    {
                        try
                        {
                            RegisterRedirectHelper.CreateWorldObject(__instance, ownerFromVerb, spawnProjectile, burstLoc, null, null);
                            return false;
                        }
                        catch (Exception e3)
                        {
                            Log.Error("[SoS2-OB] Recovery redirect (active session) failed: " + e3);
                        }
                    }

                    // Recovery B: if we have an owner turret and it has a queued bombardment target, start a new session and redirect now
                    if (ownerFromVerb != null && OrbitalBombardmentState.TryGet(ownerFromVerb, out var recMap, out var recCell))
                    {
                        OrbitalRedirectSession.Begin(ownerFromVerb, recMap, recCell);
                        // Redirect this projectile immediately
                        try
                        {
                            var tgtTile = OrbitalRedirectSession.TargetMap?.Parent?.Tile;
                            RegisterRedirectHelper.CreateWorldObject(__instance, ownerFromVerb, spawnProjectile, burstLoc, null, null);
                        }
                        catch (Exception e2)
                        {
                            Log.Error("[SoS2-OB] Recovery redirect failed: " + e2);
                        }
                        return false; // skip original
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[SoS2-OB] Recovery redirect failed: " + ex);
                }
                // Additional defensive: inspect verb internal fields when we are NOT redirecting; cancel invalid bursts to avoid NRE spam
                try
                {
                    if (__instance != null && turret != null)
                    {
                        var type = __instance.GetType();
                        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        bool missingShipCombatContext = false;
                        foreach (var f in fields)
                        {
                            object val = null;
                            try { val = f.GetValue(__instance); } catch { }
                            if (val == null)
                            {
                                if (f.Name == "maneuver" || f.Name == "tool" || f.Name == "controlGroup")
                                    missingShipCombatContext = true;
                            }
                        }

                        // If this burst is occurring outside ship-combat context and we could not recover/redirect, cancel it to prevent NRE spam.
                        if (missingShipCombatContext)
                        {
                            try
                            {
                                var fState = fields.FirstOrDefault(fi => fi.Name == "state");
                                var fBurst = fields.FirstOrDefault(fi => fi.Name == "burstShotsLeft");
                                if (fState != null && fBurst != null)
                                {
                                    fBurst.SetValue(__instance, 0);
                                    fState.SetValue(__instance, VerbState.Idle);
                                    return false; // skip original to avoid NRE
                                }
                            }
                            catch (Exception ex2)
                            {
                                Log.Error("[SoS2-OB] Cancel invalid burst failed: " + ex2);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[SoS2-OB] Reflection failed: " + ex);
                }
                return true; // proceed with original
            }

            try
            {
                RegisterRedirectHelper.CreateWorldObject(__instance, turret, spawnProjectile, burstLoc, null, null);
                // For lasers: coalesce to one beam per burst; for non-lasers: keep session active to capture all shots
                bool isLaser = turret.def?.GetModExtension<OrbitalBombardmentTurretExtension>()?.isLaser ?? false;
                if (isLaser)
                {
                    OrbitalRedirectSession.End();
                }
            }
            catch (Exception e)
            {
                Log.Error($"[SoS2-OrbitalBombardment] Failed to redirect RegisterProjectile: {e}");
            }

            // skip original registration so it doesn't spawn on ship target map
            return false;
        }
    }
}