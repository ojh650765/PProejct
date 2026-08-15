using PokeLab.Core;

namespace PokeLab.Audio
{
    /// <summary>
    /// Every clip name in one place.
    ///
    /// These strings are the keys in <c>Assets/Game/Audio/audio_manifest.json</c> and in
    /// the generated <see cref="AudioClipCatalog"/>. Nothing else in this assembly spells
    /// a clip name inline, so a rename in the generator is a single edit here plus a
    /// catalogue rebuild -- and a typo becomes a compile error instead of a silent
    /// missing sound at runtime.
    /// </summary>
    public static class AudioIds
    {
        // ---- music -------------------------------------------------------------------
        public const string MusicRouteDay = "Music_Route_Day";
        public const string MusicRouteNight = "Music_Route_Night";
        public const string MusicTownDay = "Music_Town_Day";
        public const string MusicTownNight = "Music_Town_Night";
        public const string MusicCave = "Music_Cave";
        public const string MusicLakeside = "Music_Lakeside";
        public const string MusicBattleWild = "Music_Battle_Wild";
        public const string MusicBattleTrainer = "Music_Battle_Trainer";
        public const string MusicVictoryFanfare = "Music_Victory_Fanfare";
        public const string MusicCaptureSuccess = "Music_Capture_Success";
        public const string MusicEncounterSting = "Music_Encounter_Sting";

        // ---- battle ------------------------------------------------------------------
        public const string BattleSendOut = "SFX_Battle_SendOut";
        public const string BattleRecall = "SFX_Battle_Recall";
        public const string BattleFaint = "SFX_Battle_Faint";
        public const string BattleHpTick = "SFX_Battle_HpTick";
        public const string BattleLowHpWarning = "SFX_Battle_LowHpWarning";
        public const string BattleLevelUp = "SFX_Battle_LevelUp";
        public const string BattleExpGain = "SFX_Battle_ExpGain";
        public const string BattleStatUp = "SFX_Battle_StatUp";
        public const string BattleStatDown = "SFX_Battle_StatDown";

        public const string StatusBurn = "SFX_Status_Burn";
        public const string StatusFreeze = "SFX_Status_Freeze";
        public const string StatusParalysis = "SFX_Status_Paralysis";
        public const string StatusPoison = "SFX_Status_Poison";
        public const string StatusSleep = "SFX_Status_Sleep";

        // ---- moves -------------------------------------------------------------------
        public const string MoveCritical = "SFX_Move_Critical";
        public const string MoveSuperEffective = "SFX_Move_SuperEffective";
        public const string MoveNotVeryEffective = "SFX_Move_NotVeryEffective";

        /// <summary>
        /// The slice ships cues for twelve of the eighteen types. The other six are
        /// remapped onto their closest sibling rather than silently dropped, because a
        /// missing attack sound reads as a bug to a player and a slightly-wrong one does
        /// not. <see cref="ElementType.None"/> falls through to Normal.
        /// </summary>
        public static ElementType ResolveCueType(ElementType type)
        {
            switch (type)
            {
                case ElementType.Ice: return ElementType.Water;
                case ElementType.Bug: return ElementType.Grass;
                case ElementType.Dragon: return ElementType.Psychic;
                case ElementType.Dark: return ElementType.Ghost;
                case ElementType.Steel: return ElementType.Rock;
                case ElementType.Fairy: return ElementType.Psychic;
                case ElementType.None: return ElementType.Normal;
                default: return type;
            }
        }

        public static string MoveCast(ElementType type) =>
            "SFX_Move_" + ResolveCueType(type) + "_Cast";

        public static string MoveImpact(ElementType type) =>
            "SFX_Move_" + ResolveCueType(type) + "_Impact";

        // ---- capture -----------------------------------------------------------------
        public const string CaptureThrow = "SFX_Capture_Throw";
        public const string CaptureAbsorbBeam = "SFX_Capture_AbsorbBeam";
        public const string CaptureBallLand = "SFX_Capture_BallLand";
        public const string CaptureSuccessClick = "SFX_Capture_SuccessClick";
        public const string CaptureBreakOut = "SFX_Capture_BreakOut";

        public static readonly string[] CaptureShakeTicks =
        {
            "SFX_Capture_ShakeTick_01",
            "SFX_Capture_ShakeTick_02",
            "SFX_Capture_ShakeTick_03",
        };

        // ---- overworld ---------------------------------------------------------------
        public const string OverworldLedgeHop = "SFX_Overworld_LedgeHop";
        public const string OverworldDoorOpen = "SFX_Overworld_DoorOpen";
        public const string OverworldDoorClose = "SFX_Overworld_DoorClose";
        public const string OverworldItemPickup = "SFX_Overworld_ItemPickup";
        public const string OverworldHeal = "SFX_Overworld_Heal";

        public static readonly string[] GrassRustle =
        {
            "SFX_Overworld_GrassRustle_01",
            "SFX_Overworld_GrassRustle_02",
            "SFX_Overworld_GrassRustle_03",
        };

        /// <summary>Four variants per surface; <see cref="OverworldAudio"/> avoids repeats.</summary>
        public static string Footstep(FootstepSurface surface, int variant) =>
            $"SFX_Foot_{surface}_{(variant % 4) + 1:00}";

        public const int FootstepVariants = 4;

        // ---- scanner -----------------------------------------------------------------
        public const string ScannerBoot = "SFX_Scanner_Boot";
        public const string ScannerScanLoop = "SFX_Scanner_ScanLoop";
        public const string ScannerThreatAlert = "SFX_Scanner_ThreatAlert";
        public const string ScannerRecommendation = "SFX_Scanner_Recommendation";
        public const string ScannerProbabilityUp = "SFX_Scanner_ProbabilityUp";
        public const string ScannerProbabilityDown = "SFX_Scanner_ProbabilityDown";

        public static readonly string[] ScannerDataBlips =
        {
            "SFX_Scanner_DataBlip_01",
            "SFX_Scanner_DataBlip_02",
            "SFX_Scanner_DataBlip_03",
        };

        // ---- ui ----------------------------------------------------------------------
        public const string UiNavigate = "SFX_UI_Navigate";
        public const string UiConfirm = "SFX_UI_Confirm";
        public const string UiCancel = "SFX_UI_Cancel";
        public const string UiError = "SFX_UI_Error";
        public const string UiMenuOpen = "SFX_UI_MenuOpen";
        public const string UiMenuClose = "SFX_UI_MenuClose";
        public const string UiTypewriter = "SFX_UI_Typewriter";

        // ---- ambience layers ---------------------------------------------------------
        public const string AmbBirdsong = "Amb_Birdsong";
        public const string AmbWindGrass = "Amb_WindGrass";
        public const string AmbWindHigh = "Amb_WindHigh";
        public const string AmbWaterLapping = "Amb_WaterLapping";
        public const string AmbWaterfall = "Amb_Waterfall";
        public const string AmbCaveDrips = "Amb_CaveDrips";
        public const string AmbCaveRumble = "Amb_CaveRumble";
        public const string AmbRain = "Amb_Rain";
        public const string AmbNightInsects = "Amb_NightInsects";
        public const string AmbTownMurmur = "Amb_TownMurmur";

        public static readonly string[] Thunder = { "Amb_Thunder_01", "Amb_Thunder_02" };

        /// <summary>The ten looping ambience layers, in the order AmbienceDirector mixes them.</summary>
        public static readonly string[] AmbienceLayers =
        {
            AmbBirdsong, AmbWindGrass, AmbWindHigh, AmbWaterLapping, AmbWaterfall,
            AmbCaveDrips, AmbCaveRumble, AmbRain, AmbNightInsects, AmbTownMurmur,
        };

        // ---- biome ids ---------------------------------------------------------------
        // Matched case-insensitively against GameEvents.BiomeEntered, which is a free
        // string owned by the overworld worker. Unknown biomes fall back to Route.
        public const string BiomeRoute = "route";
        public const string BiomeTown = "town";
        public const string BiomeCave = "cave";
        public const string BiomeLakeside = "lakeside";
    }

    public enum FootstepSurface
    {
        Grass = 0,
        Dirt = 1,
        Stone = 2,
        Wood = 3,
        Water = 4,
    }

    /// <summary>Mixer routing groups. Maps one-to-one onto the groups in GameMixer.mixer.</summary>
    public enum AudioBus
    {
        Master = 0,
        Music = 1,
        Sfx = 2,
        Ambience = 3,
        Ui = 4,
    }
}
