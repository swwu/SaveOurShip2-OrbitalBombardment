using RimWorld;
using UnityEngine;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    public class OBSettings : ModSettings
    {
        public bool enableTerrainDeformation = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enableTerrainDeformation, nameof(enableTerrainDeformation), true);
        }
    }

    public class OBMod : Mod
    {
        public static OBSettings Settings { get; private set; }

        public OBMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<OBSettings>();
        }

        public override string SettingsCategory()
        {
            return "SoS2: Orbital Bombardment";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.GapLine();
            listing.Label("Gameplay");
            listing.Gap(6f);
            listing.CheckboxLabeled(
                label: "Enable terrain deformation (lava trails and impact pools)",
                checkOn: ref Settings.enableTerrainDeformation,
                tooltip: "If disabled, orbital beams and projectile impacts will not alter terrain.");
            listing.End();
        }
    }
}
