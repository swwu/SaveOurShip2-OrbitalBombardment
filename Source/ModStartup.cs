using HarmonyLib;
using RimWorld;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    [StaticConstructorOnStartup]
    public static class ModStartup
    {
        static ModStartup()
        {
            var harmony = new Harmony("xannihilusx.saveourship2.orbitalbombardment");
            harmony.PatchAll();
        }
    }
}
