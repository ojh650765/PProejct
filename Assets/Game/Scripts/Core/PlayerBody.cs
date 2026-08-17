using System;

namespace PokeLab.Core
{
    /// <summary>
    /// Which of the two player characters the person playing said they were.
    ///
    /// The opening asks the question — "자네는 남자인가? 아니면 여자인가?" — and the choice
    /// raises <c>profile.body.male</c> or <c>profile.body.female</c>. Nothing listened. The art
    /// for the second character has been in the manifest the whole time, front, back and side,
    /// and every player in the game walked around as the boy regardless of their answer. Asking
    /// a question and discarding the answer is worse than not asking: it reads as a bug in the
    /// one place a new player is guaranteed to look.
    ///
    /// A static in Core because three assemblies need the answer and none of them can see each
    /// other — the world billboard is in Overworld, the battle trainer stage is in Cinematics,
    /// the dialogue portrait is in UI. The authority is the save file; this is the cached copy
    /// that everything reads, refreshed from the profile flag on load.
    /// </summary>
    public static class PlayerBody
    {
        /// <summary>The profile flag this is persisted under.</summary>
        public const string Flag = "profile.body";

        private static bool _female;

        /// <summary>Raised when the answer changes, so anything already drawn can re-key.</summary>
        public static event Action Changed;

        public static bool IsFemale => _female;

        /// <summary>Sprite manifest key for the walking sprite: "player" or "player_f".</summary>
        public static string SpriteKey => _female ? "player_f" : "player";

        /// <summary>
        /// Portrait key for the dialogue box, which files the two under the same names.
        /// Kept separate from <see cref="SpriteKey"/> so the two can diverge without every
        /// caller having to know they currently agree.
        /// </summary>
        public static string PortraitKey => _female ? "player_f" : "player";

        /// <summary>Back view, thrown from — the ball throw shows the player from behind.</summary>
        public static string BackKey => _female ? "player_f_back" : "player_back";

        /// <summary>True when <paramref name="key"/> names either player character.</summary>
        public static bool IsPlayerKey(string key) =>
            key == "player" || key == "player_f"
            || key == "player_back" || key == "player_f_back";

        /// <summary>
        /// Records the answer. Idempotent, because the opening can be replayed by a debug jump
        /// and re-raising Changed for the same value would rebuild every billboard for nothing.
        /// </summary>
        public static void Set(bool female)
        {
            if (_female == female) return;
            _female = female;
            Changed?.Invoke();
        }

        /// <summary>Restores from a saved flag value. Anything but "female" means the boy.</summary>
        public static void Restore(string flagValue) => Set(flagValue == "female");

        /// <summary>The value to write to the save.</summary>
        public static string FlagValue => _female ? "female" : "male";
    }
}
