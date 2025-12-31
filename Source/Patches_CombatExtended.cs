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
    // Compatibility patches for Combat Extended
    // These patches extend the orbital bombardment functionality to work with CE's Building_ShipTurretCE and Verb_ShootShipCE

    // Extended state management to support both Building_ShipTurret and Building_ShipTurretCE
    internal static class OrbitalBombardmentStateCE
    {
        private struct Target
        {
            public Map map;
            public IntVec3 cell;
        }
        // Use object as key to support both Building_ShipTurret and Building_ShipTurretCE
        private static readonly Dictionary<object, Target> targets = new Dictionary<object, Target>();

        public static void SetTarget(object turret, Map targetMap, IntVec3 targetCell)
        {
            if (turret == null || targetMap == null) return;
            targets[turret] = new Target { map = targetMap, cell = targetCell };
        }

        public static bool TryGet(object turret, out Map map, out IntVec3 cell)
        {
            if (turret != null && targets.TryGetValue(turret, out var t))
            {
                map = t.map; cell = t.cell; return true;
            }
            map = null; cell = IntVec3.Invalid; return false;
        }

        public static void Clear(object turret)
        {
            if (turret != null) targets.Remove(turret);
        }

        public static bool HasTarget(object turret)
        {
            return turret != null && targets.ContainsKey(turret);
        }
    }

    // Extended redirect sessions to support both turret types
    internal static class OrbitalRedirectSessionsCE
    {
        private struct Session
        {
            public Map Map;
            public IntVec3 Cell;
            public bool LaserConsumed;
        }

        private static readonly Dictionary<object, Session> sessions = new Dictionary<object, Session>();

        public static bool IsActive(object turret)
        {
            return turret != null && sessions.ContainsKey(turret);
        }

        public static void Begin(object turret, Map targetMap, IntVec3 targetCell)
        {
            if (turret == null || targetMap == null) return;
            sessions[turret] = new Session { Map = targetMap, Cell = targetCell, LaserConsumed = false };
        }

        public static bool TryGet(object turret, out Map map, out IntVec3 cell, out bool laserConsumed)
        {
            if (turret != null && sessions.TryGetValue(turret, out var s))
            {
                map = s.Map;
                cell = s.Cell;
                laserConsumed = s.LaserConsumed;
                return true;
            }
            map = null;
            cell = IntVec3.Invalid;
            laserConsumed = false;
            return false;
        }

        public static void MarkLaserConsumed(object turret)
        {
            if (turret == null) return;
            if (sessions.TryGetValue(turret, out var s))
            {
                s.LaserConsumed = true;
                sessions[turret] = s;
            }
        }

        public static void End(object turret)
        {
            if (turret != null) sessions.Remove(turret);
        }
    }

    // Patch Building_ShipTurretCE.GetGizmos to add orbital bombardment button
    [HarmonyPatch]
    public static class Patch_ShipTurretCE_GetGizmos
    {
        static bool Prepare()
        {
            // Only apply this patch if Combat Extended is loaded
            var ceType = GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Building_ShipTurretCE");
            return ceType != null;
        }

        static MethodBase TargetMethod()
        {
            var ceType = GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Building_ShipTurretCE");
            return ceType?.GetMethod("GetGizmos", BindingFlags.Public | BindingFlags.Instance);
        }

        static void Postfix(object __instance, ref IEnumerable<Gizmo> __result)
        {
            var list = __result?.ToList() ?? new List<Gizmo>();

            // Use reflection to access properties/fields since we don't have direct type access
            var factionProp = __instance.GetType().GetProperty("Faction", BindingFlags.Public | BindingFlags.Instance);
            var gunCompEqProp = __instance.GetType().GetProperty("GunCompEq", BindingFlags.Public | BindingFlags.Instance);
            // GroundDefenseMode is a field, not a property
            var groundDefenseModeField = __instance.GetType().GetField("GroundDefenseMode", BindingFlags.Public | BindingFlags.Instance);

            if (factionProp == null || gunCompEqProp == null || groundDefenseModeField == null) return;

            var faction = factionProp.GetValue(__instance) as Faction;
            var gunCompEq = gunCompEqProp.GetValue(__instance);
            var groundDefenseMode = (bool)(groundDefenseModeField.GetValue(__instance) ?? false);

            // Only show on player turrets, with a usable verb, and not in ground defense mode
            bool canFire = faction == Faction.OfPlayer
                           && gunCompEq != null
                           && !groundDefenseMode;

            if (canFire)
            {
                var primaryVerbProp = gunCompEq.GetType().GetProperty("PrimaryVerb", BindingFlags.Public | BindingFlags.Instance);
                if (primaryVerbProp?.GetValue(gunCompEq) != null)
                {
                    list.Add(new Command_Action
                    {
                        defaultLabel = "Orbital Bombardment",
                        defaultDesc = "Select a world tile, then a target on that map, to fire the selected ship turrets via orbital bombardment.",
                        icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", true),
                        action = () => StartWorldAndLocalTargetingForSelectedCE()
                    });
                }
            }

            __result = list;
        }

        private static void StartWorldAndLocalTargetingForSelectedCE()
        {
            // Try to get Building_ShipTurretCE type
            var ceType = GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Building_ShipTurretCE");
            if (ceType == null) return;

            // Gather selected turrets
            var selectedTurrets = Find.Selector.SelectedObjects
                .Where(obj => ceType.IsAssignableFrom(obj.GetType()))
                .Where(t =>
                {
                    var factionProp = t.GetType().GetProperty("Faction", BindingFlags.Public | BindingFlags.Instance);
                    var gunCompEqProp = t.GetType().GetProperty("GunCompEq", BindingFlags.Public | BindingFlags.Instance);
                    var groundDefenseModeField = t.GetType().GetField("GroundDefenseMode", BindingFlags.Public | BindingFlags.Instance);

                    if (factionProp == null || gunCompEqProp == null || groundDefenseModeField == null) return false;

                    var faction = factionProp.GetValue(t) as Faction;
                    var gunCompEq = gunCompEqProp.GetValue(t);
                    var groundDefenseMode = (bool)(groundDefenseModeField.GetValue(t) ?? false);

                    return faction == Faction.OfPlayer
                           && gunCompEq != null
                           && !groundDefenseMode;
                })
                .ToList();

            if (selectedTurrets.Count == 0)
            {
                Messages.Message("No eligible ship turrets selected.", MessageTypeDefOf.RejectInput, false);
                return;
            }

            // Ensure world view then begin world targeting
            Find.World.renderer.wantedMode = WorldRenderMode.Planet;
            Find.WorldTargeter.BeginTargeting(
                (System.Func<GlobalTargetInfo, bool>)(gti =>
                {
                    if (!gti.IsValid) return false;
                    int tile = gti.Tile;
                    if (tile < 0) return false;
                    var mp = Find.WorldObjects.MapParentAt(tile);
                    if (mp == null || mp.Map == null) return false;
                    return OnWorldTileChosenCE(selectedTurrets, gti);
                }),
                true);
        }

        private static bool OnWorldTileChosenCE(List<object> turrets, GlobalTargetInfo worldTarget)
        {
            if (!worldTarget.IsValid) return false;
            var mp = Find.WorldObjects.MapParentAt(worldTarget.Tile);
            var targetMap = mp?.Map;
            if (targetMap == null) return false;

            // Disallow targeting orbital/space maps
            var biomeName = targetMap.Biome?.defName;
            if (!string.IsNullOrEmpty(biomeName) && biomeName.IndexOf("OuterSpace", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Messages.Message("Orbital bombardment cannot target orbital/space maps.", new GlobalTargetInfo(worldTarget.Tile), MessageTypeDefOf.RejectInput);
                return false;
            }

            // Set current map and jump camera to the center of the destination map
            Current.Game.CurrentMap = targetMap;
            var centerCell = new IntVec3(targetMap.Size.x / 2, 0, targetMap.Size.z / 2);
            CameraJumper.TryJump(new GlobalTargetInfo(centerCell, targetMap), CameraJumper.MovementMode.Pan);

            var tp = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetPawns = false,
                canTargetBuildings = true,
                validator = t => t.Cell.InBounds(targetMap)
            };

            Find.Targeter.BeginTargeting(tp, localTarget =>
            {
                QueueBombardmentFromSelectedTurretsCE(turrets, targetMap, localTarget.Cell);
            });

            return true;
        }

        private static void QueueBombardmentFromSelectedTurretsCE(List<object> turrets, Map targetMap, IntVec3 targetCell)
        {
            int fired = 0;
            foreach (var t in turrets)
            {
                if (t == null) continue;

                var mapProp = t.GetType().GetProperty("Map", BindingFlags.Public | BindingFlags.Instance);
                var gunCompEqProp = t.GetType().GetProperty("GunCompEq", BindingFlags.Public | BindingFlags.Instance);

                if (mapProp == null || gunCompEqProp == null) continue;

                var map = mapProp.GetValue(t) as Map;
                var gunCompEq = gunCompEqProp.GetValue(t);

                if (map == null || gunCompEq == null) continue;

                var primaryVerbProp = gunCompEq.GetType().GetProperty("PrimaryVerb", BindingFlags.Public | BindingFlags.Instance);
                if (primaryVerbProp?.GetValue(gunCompEq) == null) continue;

                // Queue one volley for this turret via Tick postfix
                OrbitalBombardmentStateCE.SetTarget(t, targetMap, targetCell);
                fired++;
            }

            if (fired == 0)
                Messages.Message("No eligible ship weapons selected to launch bombardment.", MessageTypeDefOf.RejectInput, false);
            else
                Messages.Message($"Orbital bombardment en route: {fired} weapon(s) fired.", MessageTypeDefOf.NeutralEvent, false);
        }
    }

    // Patch Building_ShipTurretCE.Tick to handle bombardment queuing
    [HarmonyPatch]
    public static class Patch_ShipTurretCE_Tick_Bombardment
    {
        static bool Prepare()
        {
            return GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Building_ShipTurretCE") != null;
        }

        static MethodBase TargetMethod()
        {
            var ceType = GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Building_ShipTurretCE");
            return ceType?.GetMethod("Tick", BindingFlags.Public | BindingFlags.Instance);
        }

        static void Postfix(object __instance)
        {
            // Auto-end any lingering redirect session once the burst is over
            // Keep session alive during Warmup and Bursting states
            if (OrbitalRedirectSessionsCE.IsActive(__instance))
            {
                var attackVerbPropCheck = __instance.GetType().GetProperty("AttackVerb", BindingFlags.Public | BindingFlags.Instance);
                var verbCheck = attackVerbPropCheck?.GetValue(__instance);
                if (verbCheck != null)
                {
                    // state is a field, not a property - search inheritance hierarchy
                    FieldInfo stateField = null;
                    var stateVerbType = verbCheck.GetType();
                    while (stateVerbType != null && stateField == null)
                    {
                        stateField = stateVerbType.GetField("state", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        stateVerbType = stateVerbType.BaseType;
                    }
                    var stateCheck = stateField?.GetValue(verbCheck);
                    var stateStr = stateCheck?.ToString() ?? "";
                    // Keep session active during Warmup and Bursting
                    if (stateStr != "Warmup" && stateStr != "Bursting")
                    {
                        Log.Message($"[SoS2-OB-CE] Auto-ending session for turret {__instance} (hash: {__instance.GetHashCode()}), verb state: {stateCheck}");
                        OrbitalRedirectSessionsCE.End(__instance);
                    }
                }
            }

            // Only act if a bombardment target is queued
            if (!OrbitalBombardmentStateCE.TryGet(__instance, out var targetMap, out var targetCell)) return;

            // Use reflection to access properties
            var spawnedProp = __instance.GetType().GetProperty("Spawned", BindingFlags.Public | BindingFlags.Instance);
            var activeProp = __instance.GetType().GetProperty("Active", BindingFlags.Public | BindingFlags.Instance);
            var gunCompEqProp = __instance.GetType().GetProperty("GunCompEq", BindingFlags.Public | BindingFlags.Instance);
            var playerControlledProp = __instance.GetType().GetProperty("PlayerControlled", BindingFlags.Public | BindingFlags.Instance);
            var holdFireProp = __instance.GetType().GetProperty("holdFire", BindingFlags.Public | BindingFlags.Instance);
            var pointDefenseModeProp = __instance.GetType().GetProperty("PointDefenseMode", BindingFlags.Public | BindingFlags.Instance);
            var attackVerbProp = __instance.GetType().GetProperty("AttackVerb", BindingFlags.Public | BindingFlags.Instance);
            var burstCooldownTicksLeftProp = __instance.GetType().GetProperty("burstCooldownTicksLeft", BindingFlags.Public | BindingFlags.Instance);
            var mapProp = __instance.GetType().GetProperty("Map", BindingFlags.Public | BindingFlags.Instance);
            var positionProp = __instance.GetType().GetProperty("Position", BindingFlags.Public | BindingFlags.Instance);
            var rotationProp = __instance.GetType().GetProperty("Rotation", BindingFlags.Public | BindingFlags.Instance);
            var spinalCompProp = __instance.GetType().GetProperty("spinalComp", BindingFlags.Public | BindingFlags.Instance);
            var mapCompProp = __instance.GetType().GetProperty("mapComp", BindingFlags.Public | BindingFlags.Instance);
            var powerCompProp = __instance.GetType().GetProperty("powerComp", BindingFlags.Public | BindingFlags.Instance);
            var heatCompProp = __instance.GetType().GetProperty("heatComp", BindingFlags.Public | BindingFlags.Instance);
            var fuelCompProp = __instance.GetType().GetProperty("fuelComp", BindingFlags.Public | BindingFlags.Instance);
            var energyToFireProp = __instance.GetType().GetProperty("EnergyToFire", BindingFlags.Public | BindingFlags.Instance);
            var heatToFireProp = __instance.GetType().GetProperty("HeatToFire", BindingFlags.Public | BindingFlags.Instance);
            var defProp = __instance.GetType().GetProperty("def", BindingFlags.Public | BindingFlags.Instance);

            if (spawnedProp == null || activeProp == null || gunCompEqProp == null) return;

            // Respect basic availability
            if (!(bool)(spawnedProp.GetValue(__instance) ?? false)) return;
            if (!(bool)(activeProp.GetValue(__instance) ?? false)) return;

            var gunCompEq = gunCompEqProp.GetValue(__instance);
            if (gunCompEq == null)
            {
                OrbitalBombardmentStateCE.Clear(__instance);
                return;
            }

            var primaryVerbProp = gunCompEq.GetType().GetProperty("PrimaryVerb", BindingFlags.Public | BindingFlags.Instance);
            if (primaryVerbProp?.GetValue(gunCompEq) == null)
            {
                OrbitalBombardmentStateCE.Clear(__instance);
                return;
            }

            if (playerControlledProp != null && holdFireProp != null)
            {
                if ((bool)(playerControlledProp.GetValue(__instance) ?? false) && (bool)(holdFireProp.GetValue(__instance) ?? false))
                {
                    OrbitalBombardmentStateCE.Clear(__instance);
                    return;
                }
            }

            if (pointDefenseModeProp != null && (bool)(pointDefenseModeProp.GetValue(__instance) ?? false))
            {
                OrbitalBombardmentStateCE.Clear(__instance);
                return;
            }

            var attackVerb = attackVerbProp?.GetValue(__instance);
            if (attackVerb == null)
            {
                OrbitalBombardmentStateCE.Clear(__instance);
                return;
            }

            var availableMethod = attackVerb.GetType().GetMethod("Available", BindingFlags.Public | BindingFlags.Instance);
            if (availableMethod != null && !(bool)(availableMethod.Invoke(attackVerb, null) ?? false))
            {
                OrbitalBombardmentStateCE.Clear(__instance);
                return;
            }

            // If on cooldown or mid-burst, let normal cycle progress
            var stateProp = attackVerb.GetType().GetProperty("state", BindingFlags.Public | BindingFlags.Instance);
            if (stateProp != null && stateProp.GetValue(attackVerb)?.ToString() == "Bursting") return;

            if (burstCooldownTicksLeftProp != null && (int)(burstCooldownTicksLeftProp.GetValue(__instance) ?? 0) > 0) return;

            // Queue once, then clear to avoid repeated volleys
            OrbitalBombardmentStateCE.Clear(__instance);

            // Prepare verb targeting
            var map = mapProp?.GetValue(__instance) as Map;
            var position = (IntVec3)(positionProp?.GetValue(__instance) ?? IntVec3.Invalid);
            var rotation = rotationProp?.GetValue(__instance);

            if (map == null || position == IntVec3.Invalid) return;

            IntVec3 edgeCell;
            var spinalComp = spinalCompProp?.GetValue(__instance);
            if (spinalComp != null)
            {
                // Spinal weapon: straight to edge based on rotation
                byte rot = 0;
                if (rotation != null)
                {
                    var asByteMethod = rotation.GetType().GetMethod("AsByte", BindingFlags.Public | BindingFlags.Instance);
                    if (asByteMethod != null)
                        rot = (byte)(asByteMethod.Invoke(rotation, null) ?? 0);
                }

                if (rot == 0) // north
                    edgeCell = new IntVec3(position.x, 0, map.Size.z - 1);
                else if (rot == 1) // east
                    edgeCell = new IntVec3(map.Size.x - 1, 0, position.z);
                else if (rot == 2) // south
                    edgeCell = new IntVec3(position.x, 0, 1);
                else // west
                    edgeCell = new IntVec3(1, 0, position.z);
            }
            else
            {
                // Non-spinal: approximate MapEdgeCell with deviation
                const int miss = 5;
                int mx = position.x;
                int mz = position.z;
                int dx = Rand.RangeInclusive(-miss, miss);
                int dz = Rand.RangeInclusive(-miss, miss);

                var mapComp = mapCompProp?.GetValue(__instance);
                if (mapComp != null)
                {
                    var engineRotProp = mapComp.GetType().GetProperty("EngineRot", BindingFlags.Public | BindingFlags.Instance);
                    var headingProp = mapComp.GetType().GetProperty("Heading", BindingFlags.Public | BindingFlags.Instance);

                    if (engineRotProp != null && headingProp != null)
                    {
                        var engineRot = (int)(engineRotProp.GetValue(mapComp) ?? 0);
                        var heading = (int)(headingProp.GetValue(mapComp) ?? -1);

                        if (((engineRot == 0 && heading != -1) || (engineRot == 2 && heading == -1))) // north
                            edgeCell = new IntVec3(Mathf.Clamp(mx + dx, 0, map.Size.x - 1), 0, map.Size.z - 1);
                        else if ((engineRot == 1 && heading != -1) || (engineRot == 3 && heading == -1)) // east
                            edgeCell = new IntVec3(map.Size.x - 1, 0, Mathf.Clamp(mz + dz, 0, map.Size.z - 1));
                        else if ((engineRot == 2 && heading != -1) || (engineRot == 0 && heading == -1)) // south
                            edgeCell = new IntVec3(Mathf.Clamp(mx + dx, 0, map.Size.x - 1), 0, 0);
                        else // west
                            edgeCell = new IntVec3(0, 0, Mathf.Clamp(mz + dz, 0, map.Size.z - 1));
                    }
                    else
                    {
                        edgeCell = CellFinder.RandomEdgeCell(map);
                    }
                }
                else
                {
                    edgeCell = CellFinder.RandomEdgeCell(map);
                }
            }

            // Set shipTarget on verb if it's Verb_ShootShipCE
            var verbType = attackVerb.GetType();
            if (verbType.Name == "Verb_ShootShipCE")
            {
                var shipTargetField = verbType.GetField("shipTarget", BindingFlags.Public | BindingFlags.Instance);
                if (shipTargetField != null)
                {
                    shipTargetField.SetValue(attackVerb, new LocalTargetInfo(edgeCell));
                }
            }

            // Replicate BeginBurst resource/payment side effects
            var powerComp = powerCompProp?.GetValue(__instance);
            if (powerComp != null)
            {
                var powerNetProp = powerComp.GetType().GetProperty("PowerNet", BindingFlags.Public | BindingFlags.Instance);
                var powerNet = powerNetProp?.GetValue(powerComp);
                if (powerNet != null)
                {
                    var energyToFire = (float)(energyToFireProp?.GetValue(__instance) ?? 0f);
                    var currentStoredEnergyMethod = powerNet.GetType().GetMethod("CurrentStoredEnergy", BindingFlags.Public | BindingFlags.Instance);
                    var available = (float)(currentStoredEnergyMethod?.Invoke(powerNet, null) ?? 0f);

                    if (available < energyToFire) return; // not enough power

                    var batteryCompsProp = powerNet.GetType().GetProperty("batteryComps", BindingFlags.Public | BindingFlags.Instance);
                    var batteryComps = batteryCompsProp?.GetValue(powerNet) as System.Collections.IEnumerable;
                    if (batteryComps != null)
                    {
                        foreach (var bat in batteryComps)
                        {
                            var storedEnergyProp = bat.GetType().GetProperty("StoredEnergy", BindingFlags.Public | BindingFlags.Instance);
                            var storedEnergy = (float)(storedEnergyProp?.GetValue(bat) ?? 0f);
                            var drawPowerMethod = bat.GetType().GetMethod("DrawPower", BindingFlags.Public | BindingFlags.Instance);
                            if (drawPowerMethod != null && storedEnergy > 0)
                            {
                                float draw = Mathf.Min(energyToFire * storedEnergy / available, storedEnergy);
                                drawPowerMethod.Invoke(bat, new object[] { draw });
                            }
                        }
                    }
                }
            }

            // Heat check and application
            var heatComp = heatCompProp?.GetValue(__instance);
            if (heatComp != null)
            {
                var propsProp = heatComp.GetType().GetProperty("Props", BindingFlags.Public | BindingFlags.Instance);
                var props = propsProp?.GetValue(heatComp);
                if (props != null)
                {
                    var heatPerPulseProp = props.GetType().GetProperty("heatPerPulse", BindingFlags.Public | BindingFlags.Instance);
                    if (heatPerPulseProp != null && (float)(heatPerPulseProp.GetValue(props) ?? 0f) > 0)
                    {
                        var heatToFire = (float)(heatToFireProp?.GetValue(__instance) ?? 0f);
                        var addHeatToNetworkMethod = heatComp.GetType().GetMethod("AddHeatToNetwork", BindingFlags.Public | BindingFlags.Instance);
                        if (addHeatToNetworkMethod != null)
                        {
                            var result = addHeatToNetworkMethod.Invoke(heatComp, new object[] { heatToFire });
                            if (result is bool && !(bool)result) return; // cannot vent/apply heat
                        }
                    }
                }
            }

            // Ammo/fuel consumption
            var fuelComp = fuelCompProp?.GetValue(__instance);
            if (fuelComp != null)
            {
                var fuelProp = fuelComp.GetType().GetProperty("Fuel", BindingFlags.Public | BindingFlags.Instance);
                var fuel = (float)(fuelProp?.GetValue(fuelComp) ?? 0f);
                if (fuel <= 0f) return;

                var consumeFuelMethod = fuelComp.GetType().GetMethod("ConsumeFuel", BindingFlags.Public | BindingFlags.Instance);
                consumeFuelMethod?.Invoke(fuelComp, new object[] { 1f });
            }

            // SFX
            if (heatComp != null)
            {
                var propsProp = heatComp.GetType().GetProperty("Props", BindingFlags.Public | BindingFlags.Instance);
                var props = propsProp?.GetValue(heatComp);
                if (props != null)
                {
                    var singleFireSoundProp = props.GetType().GetProperty("singleFireSound", BindingFlags.Public | BindingFlags.Instance);
                    var sound = singleFireSoundProp?.GetValue(props) as SoundDef;
                    if (sound != null && __instance is Thing thing)
                    {
                        sound.PlayOneShot(new TargetInfo(thing));
                    }
                }
            }

            // Start redirect session for this turret/target
            OrbitalRedirectSessionsCE.Begin(__instance, targetMap, targetCell);
            Log.Message($"[SoS2-OB-CE] Started redirect session for turret {__instance} (hash: {__instance.GetHashCode()}), target map: {targetMap}, cell: {targetCell}");

            // Use the turret's normal burst initiation path
            var localTarget = new LocalTargetInfo(edgeCell);
            
            // Get the 6-parameter TryStartCastOn from base Verb class:
            // TryStartCastOn(LocalTargetInfo castTarg, LocalTargetInfo destTarg, bool surpriseAttack, bool canHitNonTargetPawns, bool preventFriendlyFire, bool nonInterruptingSelfCast)
            var tryStartCastOnMethod = typeof(Verb).GetMethod("TryStartCastOn", 
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new Type[] { typeof(LocalTargetInfo), typeof(LocalTargetInfo), typeof(bool), typeof(bool), typeof(bool), typeof(bool) },
                null);
            
            Log.Message($"[SoS2-OB-CE] Calling TryStartCastOn, method found: {tryStartCastOnMethod != null}");
            
            if (tryStartCastOnMethod != null)
            {
                // Call with: target, target, surpriseAttack=false, canHitNonTargetPawns=true, preventFriendlyFire=false, nonInterruptingSelfCast=false
                var castResult = tryStartCastOnMethod.Invoke(attackVerb, new object[] { localTarget, localTarget, false, true, false, false });
                Log.Message($"[SoS2-OB-CE] TryStartCastOn result: {castResult}");
            }
        }
    }

    // Helper class for redirect logic with CE
    internal static class RegisterRedirectHelperCE
    {
        public static bool ShouldRedirect(object turret)
        {
            if (turret == null) return false;
            return OrbitalRedirectSessionsCE.IsActive(turret);
        }

        public static void CreateWorldObject(object verb, object turret, ThingDef spawnProjectile, IntVec3 burstLoc, float? missRadiusOpt = null, int? accBoostOpt = null)
        {
            Log.Message($"[SoS2-OB-CE] CreateWorldObject called for turret: {turret}, projectile: {spawnProjectile}");
            
            if (!OrbitalRedirectSessionsCE.TryGet(turret, out var targetMap, out var targetCell, out _))
            {
                Log.Warning($"[SoS2-OB-CE] CreateWorldObject called without active session for turret {turret}");
                return;
            }
            Log.Message($"[SoS2-OB-CE] Got session - targetMap: {targetMap}, targetCell: {targetCell}");

            var mapProp = turret.GetType().GetProperty("Map", BindingFlags.Public | BindingFlags.Instance);
            var map = mapProp?.GetValue(turret) as Map;
            if (map == null) return;

            var sourceTile = map.Parent?.Tile ?? -1;
            var targetTile = targetMap.Parent?.Tile ?? -1;

            var defProp = turret.GetType().GetProperty("def", BindingFlags.Public | BindingFlags.Instance);
            var def = defProp?.GetValue(turret) as ThingDef;
            bool isLaser = false;
            if (def != null)
            {
                var ext = def.GetModExtension<OrbitalBombardmentTurretExtension>();
                isLaser = ext?.isLaser ?? false;
            }

            float missRadius = missRadiusOpt ?? 35f;
            if (verb != null)
            {
                var verbPropsProp = verb.GetType().GetProperty("verbProps", BindingFlags.Public | BindingFlags.Instance);
                var verbProps = verbPropsProp?.GetValue(verb);
                if (verbProps != null)
                {
                    var forcedMissRadiusProp = verbProps.GetType().GetProperty("ForcedMissRadius", BindingFlags.Public | BindingFlags.Instance);
                    if (forcedMissRadiusProp != null)
                    {
                        missRadius = (float)(forcedMissRadiusProp.GetValue(verbProps) ?? missRadius);
                    }
                }
            }

            int accBoost = accBoostOpt ?? 0;
            var heatCompProp = turret.GetType().GetProperty("heatComp", BindingFlags.Public | BindingFlags.Instance);
            var heatComp = heatCompProp?.GetValue(turret);
            if (heatComp != null)
            {
                var myNetProp = heatComp.GetType().GetProperty("myNet", BindingFlags.Public | BindingFlags.Instance);
                var myNet = myNetProp?.GetValue(heatComp);
                if (myNet != null)
                {
                    var accuracyBoostProp = myNet.GetType().GetProperty("AccuracyBoost", BindingFlags.Public | BindingFlags.Instance);
                    accBoost = (int)(accuracyBoostProp?.GetValue(myNet) ?? accBoost);
                }
            }

            // Convert Building_ShipTurretCE to Building_ShipTurret if needed
            object turretForManager = turret;
            var turretType = turret.GetType();
            if (turretType.Name == "Building_ShipTurretCE")
            {
                // Try to convert using ToBuilding_ShipTurret method
                var toBuildingShipTurretMethod = turretType.GetMethod("ToBuilding_ShipTurret", BindingFlags.Public | BindingFlags.Instance);
                if (toBuildingShipTurretMethod != null)
                {
                    turretForManager = toBuildingShipTurretMethod.Invoke(turret, null);
                }
                else
                {
                    // Fallback: try to cast if it inherits from Building_ShipTurret
                    var sos2TurretTypeCheck = typeof(Building_ShipTurret);
                    if (sos2TurretTypeCheck.IsAssignableFrom(turretType))
                    {
                        turretForManager = turret;
                    }
                }
            }

             // Only enqueue if we have a valid turret (check via reflection)
             var sos2TurretTypeFinal = typeof(Building_ShipTurret);
             Log.Message($"[SoS2-OB-CE] turretForManager type: {turretForManager?.GetType()?.FullName}, IsInstanceOfType: {sos2TurretTypeFinal.IsInstanceOfType(turretForManager)}");
             if (sos2TurretTypeFinal.IsInstanceOfType(turretForManager))
             {
                 Log.Message($"[SoS2-OB-CE] Enqueuing bombardment! sourceTile: {sourceTile}, targetTile: {targetTile}");
                 OrbitalBombardmentManager.Instance?.Enqueue(
                     sourceTile,
                     targetTile,
                     targetMap,
                     targetCell,
                     spawnProjectile,
                     missRadius,
                     accBoost,
                     burstLoc,
                     isLaser,
                     turretForManager as Building_ShipTurret);
             }
             else
             {
                 Log.Warning($"[SoS2-OB-CE] Could not convert turret {turret} to Building_ShipTurret for orbital bombardment");
             }
        }
    }

    // Patch Verb_ShootShipCE.TryCastShot to redirect for orbital bombardment
    // We patch TryCastShot because when GroundDefenseMode is true, the CE path is used
    // which spawns projectiles directly and never calls RegisterProjectile
    [HarmonyPatch]
    public static class Patch_Verb_ShootShipCE_TryCastShot
    {
        static bool Prepare()
        {
            return GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Verb_ShootShipCE") != null;
        }

        static MethodBase TargetMethod()
        {
            var ceType = GenTypes.GetTypeInAnyAssembly("CombatExtended.Compatibility.SOS2Compat.Verb_ShootShipCE");
            return ceType?.GetMethod("TryCastShot", BindingFlags.Public | BindingFlags.Instance);
        }

        // Use Harmony's ___fieldName convention to access the protected caster field
        static bool Prefix(object __instance, Thing ___caster, ref bool __result)
        {
            var turret = ___caster;
            
            Log.Message($"[SoS2-OB-CE] TryCastShot Prefix called! verb: {__instance?.GetType()?.Name}, turret: {turret} (hash: {turret?.GetHashCode()}), turret type: {turret?.GetType()?.Name}");
            
            if (turret == null)
            {
                Log.Message($"[SoS2-OB-CE] turret is null, proceeding with original");
                return true;
            }
            
            // Check if this turret has an active orbital bombardment session
            bool isActive = OrbitalRedirectSessionsCE.IsActive(turret);
            Log.Message($"[SoS2-OB-CE] Session active check: {isActive}");
            
            if (!isActive)
            {
                return true; // proceed with original
            }
            
            Log.Message($"[SoS2-OB-CE] TryCastShot intercepted for orbital bombardment! turret: {turret}");

            try
            {
                var defProp = turret.GetType().GetProperty("def", BindingFlags.Public | BindingFlags.Instance);
                var def = defProp?.GetValue(turret) as ThingDef;
                bool isLaser = false;
                if (def != null)
                {
                    var ext = def.GetModExtension<OrbitalBombardmentTurretExtension>();
                    isLaser = ext?.isLaser ?? false;
                }
                
                // Get projectile def from verb
                var projectileProp = __instance.GetType().GetProperty("Projectile", BindingFlags.Public | BindingFlags.Instance);
                var spawnProjectile = projectileProp?.GetValue(__instance) as ThingDef;
                
                // Get burst location from turret
                var burstLocProp = turret.GetType().GetProperty("SynchronizedBurstLocation", BindingFlags.Public | BindingFlags.Instance);
                var burstLoc = burstLocProp != null ? (IntVec3)burstLocProp.GetValue(turret) : IntVec3.Invalid;
                
                Log.Message($"[SoS2-OB-CE] Projectile: {spawnProjectile}, isLaser: {isLaser}, burstLoc: {burstLoc}");

                if (isLaser)
                {
                    // Only enqueue once per burst for lasers
                    if (OrbitalRedirectSessionsCE.TryGet(turret, out _, out _, out var laserConsumed) && !laserConsumed)
                    {
                        RegisterRedirectHelperCE.CreateWorldObject(__instance, turret, spawnProjectile, burstLoc, null, null);
                        OrbitalRedirectSessionsCE.MarkLaserConsumed(turret);
                    }
                }
                else
                {
                    // Non-lasers: enqueue each shot
                    RegisterRedirectHelperCE.CreateWorldObject(__instance, turret, spawnProjectile, burstLoc, null, null);
                }
                
                // Skip original - don't spawn local projectiles
                __result = true;
                return false;
            }
            catch (Exception e)
            {
                Log.Error($"[SoS2-OrbitalBombardment-CE] Failed to redirect TryCastShot: {e}");
                // If something goes wrong, fall back to original
                return true;
            }
        }
    }
}

