using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using SaveOurShip2;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // Patch Building_ShipSensor.ChoseWorldTarget to allow scanning/generating faction base maps when setting is enabled.
    [HarmonyPatch]
    public static class Patch_ShipSensor_AllowFactionBases
    {
        static MethodBase TargetMethod()
        {
            // Find SaveOurShip2.Building_ShipSensor.ChoseWorldTarget(GlobalTargetInfo)
            var type = AccessTools.TypeByName("SaveOurShip2.Building_ShipSensor");
            if (type == null) return null;
            return AccessTools.Method(type, "ChoseWorldTarget", new Type[] { typeof(GlobalTargetInfo) });
        }

        static bool Prefix(object __instance, GlobalTargetInfo target, ref bool __result)
        {
            if (!OBMod.Settings?.allowSensorGenerateFactionBases ?? true) return true; // let original run
            if (!target.IsValid) return true;

            try
            {
                var wo = target.WorldObject;
                if (wo is Settlement settlement)
                {
                    // When targeting a faction base, mimic the same generation path as allowed sites
                    // Access private fields/methods via reflection
                    var type = __instance.GetType();
                    var miDispose = AccessTools.Method(type, "PossiblyDisposeOfObservedMap", new Type[] { });
                    miDispose?.Invoke(__instance, Array.Empty<object>());

                    // observedMap = (MapParent)target.WorldObject;
                    var fiObserved = AccessTools.Field(type, "observedMap");
                    fiObserved?.SetValue(__instance, settlement);

                    // Queue map generation: GetOrGenerateMapUtility.GetOrGenerateMap(tile, def)
                    LongEventHandler.QueueLongEvent(delegate
                    {
                        GetOrGenerateMapUtility.GetOrGenerateMap(settlement.Tile, settlement.def);
                        // Unfog the map and fix faction
                        if (settlement.Map != null)
                        {
                            settlement.Map.fogGrid.ClearAllFog();
                            SOS2MapUtility.FixWorldObjectFaction(settlement.Tile);
                        }
                    }, "GeneratingMap", false, null);

                    __result = true;
                    return false; // skip original
                }
            }
            catch (Exception e)
            {
                Log.Error("[SoS2-OB] Sensor allow-bases patch failed: " + e);
            }
            return true; // default to original behavior otherwise
        }
    }
}
