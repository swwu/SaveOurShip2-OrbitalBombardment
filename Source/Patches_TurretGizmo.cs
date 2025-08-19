using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using SaveOurShip2;
using UnityEngine;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    // Adds an Orbital Bombardment gizmo directly to player ship turrets; supports multi-select firing
    [HarmonyPatch(typeof(Building_ShipTurret), nameof(Building_ShipTurret.GetGizmos))]
    public static class Patch_ShipTurret_GetGizmos
    {
        public static void Postfix(Building_ShipTurret __instance, ref IEnumerable<Gizmo> __result)
        {
            var list = __result?.ToList() ?? new List<Gizmo>();

            // Only on player turrets, with a usable verb
            bool canFire = __instance.Faction == Faction.OfPlayer
                           && __instance.GunCompEq?.PrimaryVerb != null;

            if (canFire)
            {
                list.Add(new Command_Action
                {
                    defaultLabel = "Orbital Bombardment",
                    defaultDesc = "Select a world tile, then a target on that map, to fire the selected ship turrets via orbital bombardment.",
                    icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", true),
                    action = StartWorldAndLocalTargetingForSelected
                });
            }

            __result = list;
        }

        private static void StartWorldAndLocalTargetingForSelected()
        {
            // Gather selected turrets now; we'll validate again before firing
            var selectedTurrets = Find.Selector.SelectedObjects
                .OfType<Building_ShipTurret>()
                .Where(t => t.Faction == Faction.OfPlayer
                            && t.GunCompEq?.PrimaryVerb != null)
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
                    return OnWorldTileChosen(selectedTurrets, gti);
                }),
                true);
        }

        private static bool OnWorldTileChosen(List<Building_ShipTurret> turrets, GlobalTargetInfo worldTarget)
        {
            if (!worldTarget.IsValid) return false;
            var mp = Find.WorldObjects.MapParentAt(worldTarget.Tile);
            var targetMap = mp?.Map;
            if (targetMap == null) return false;

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
                QueueBombardmentFromSelectedTurrets(turrets, targetMap, localTarget.Cell);
            });

            return true;
        }

    private static void QueueBombardmentFromSelectedTurrets(List<Building_ShipTurret> turrets, Map targetMap, IntVec3 targetCell)
        {
            int fired = 0;
            foreach (var t in turrets)
            {
                if (t == null || t.Map == null) continue;
                if (t.GunCompEq?.PrimaryVerb == null) continue;

        // Queue one volley for this turret via Tick postfix
        OrbitalBombardmentState.SetTarget(t, targetMap, targetCell);
        fired++;
            }

            if (fired == 0)
                Messages.Message("No eligible ship weapons selected to launch bombardment.", MessageTypeDefOf.RejectInput, false);
            else
                Messages.Message($"Orbital bombardment en route: {fired} weapon(s) fired.", MessageTypeDefOf.NeutralEvent, false);
        }
    }
}
