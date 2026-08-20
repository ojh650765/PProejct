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

        /// <summary>
        /// The title screen's own piece.
        ///
        /// Separate from <see cref="MusicOpeningIntroduction"/> on purpose: that one is the
        /// PROLOGUE — the professor talking over black — and the two were briefly the same
        /// recording. A title screen and a monologue are different moments and the player hears
        /// them back to back.
        /// </summary>
        public const string MusicTitle = "Music_Title";
        public const string MusicEncounterSting = "Music_Encounter_Sting";

        /// <summary>
        /// The hand-supplied opening cue. EpisodeRunner asks for it by this exact string
        /// through the reflection seam (it cannot reference this const), so a rename here
        /// must be mirrored in its OpeningTrack.
        /// </summary>
        public const string MusicOpeningIntroduction = "Music_Opening_Introduction";

        // ---- battle ------------------------------------------------------------------
        public const string BattleSendOut = "SFX_Battle_SendOut";
        public const string BattleRecall = "SFX_Battle_Recall";
        public const string BattleFaint = "SFX_Battle_Faint";
        public const string BattleHpTick = "SFX_Battle_HpTick";
        public const string BattleLowHpWarning = "SFX_Battle_LowHpWarning";
        // Imported with the real SFX library; see Tools/Audio/import_move_sfx.py.
        public const string BattleHealRestore = "SFX_Battle_HealRestore";
        public const string BattleFlee = "SFX_Battle_Flee";
        public const string BattleHeldItem = "SFX_Battle_HeldItem";

        /// <summary>
        /// The clip for one move, by its moves.json id — "SFX_Move_QuickAttack" from
        /// "quick-attack". Every one of the game's 32 moves has a dedicated recording; a move
        /// added later with no file falls back to its element's generated cast/impact pair,
        /// which is quieter than it should be rather than silent.
        /// </summary>
        public static string MoveSfx(string moveId)
        {
            if (string.IsNullOrEmpty(moveId)) return null;
            var parts = moveId.Split(new[] { '-', '_', ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            var name = "SFX_Move_";
            foreach (var part in parts)
                name += char.ToUpperInvariant(part[0]) + part.Substring(1).ToLowerInvariant();
            return name;
        }
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

        // There was a UiLaunch here -- "the sting under a screen committing to something".
        // It named SFX_UI_Launch, no such recording was ever imported, and nothing in the game
        // ever asked for it: the colour wash a menu row plays on its way out is carried by
        // UiConfirm and the wash itself. A constant that resolves to nothing is not a silent
        // no-op, because the catalogue validator walks every id declared on this class and
        // fails the build gate on the ones it cannot find -- which is how it surfaced, as
        // "AudioIds references missing clip 'SFX_UI_Launch'". Declare it again when there is a
        // file to declare.
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
        /// <summary>
        /// Not anywhere. The state the audio layer starts a session in and returns to whenever
        /// a menu is on screen.
        ///
        /// It exists because "no biome" used to be spelled <see cref="BiomeRoute"/>, and a
        /// default is not the same thing as an absence. The login screen and the title are not
        /// places: the world clock ticks under them exactly as it does in the field, both
        /// directors answer TimeOfDayChanged by picking the track and the beds for wherever
        /// they think the player is, and with route as the default that is the route's daytime
        /// theme and its birdsong -- over a menu, before the player has pressed anything. The
        /// user heard it as 로그인 화면과 메인메뉴에서 스토리모드의 배경음악.
        ///
        /// No profile is registered against it, so it selects nothing and mixes to silence.
        /// </summary>
        public const string BiomeNone = "";
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

}
