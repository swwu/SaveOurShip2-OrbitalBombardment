using RimWorld;
using UnityEngine;
using Verse;

namespace SaveOurShip2_OrbitalBombardment
{
    public class OBSettings : ModSettings
    {
        public bool enableTerrainDeformation = true;
        public bool enableBountyPenalty = true;
        public int bountyPerShot = 1;
        public bool allowSensorGenerateFactionBases = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enableTerrainDeformation, nameof(enableTerrainDeformation), true);
            Scribe_Values.Look(ref enableBountyPenalty, nameof(enableBountyPenalty), true);
            Scribe_Values.Look(ref bountyPerShot, nameof(bountyPerShot), 1);
            Scribe_Values.Look(ref allowSensorGenerateFactionBases, nameof(allowSensorGenerateFactionBases), false);
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
            listing.Gap(6f);
            listing.CheckboxLabeled(
                label: "Enable bounty penalty per shot",
                checkOn: ref Settings.enableBountyPenalty,
                tooltip: "Apply a SoS2-style bounty increase for each orbital shot fired at planetside targets. Attacking a trader in SoS2 applies a value of +15, for reference.");
            listing.Gap(4f);
            var bounty = Settings.bountyPerShot;
            listing.IntEntry(ref bounty, ref _tmpBuffer, 1);
            bounty = Mathf.Clamp(bounty, 0, 100);
            Settings.bountyPerShot = bounty;
            listing.Label($"Bounty per shot: {Settings.bountyPerShot}");

            listing.GapLine();
            listing.Label("Ship Sensor");
            listing.Gap(6f);
            listing.CheckboxLabeled(
                label: "Allow ship sensor to generate faction base maps",
                checkOn: ref Settings.allowSensorGenerateFactionBases,
                tooltip: "When enabled, the Advanced Sensor can scan and generate maps for faction settlements, not just the limited list of observeable sites.");
            listing.End();
        }

        // Simple buffer for IntEntry
        private static string _tmpBuffer = "1";
    }
}
