using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// Resolves a speaker to the illustration shown beside their line.
    ///
    /// The art the project has today is 32px overworld sheets — pixel characters meant to be
    /// four percent of the screen. The dialogue is meant to show a drawn half-body instead, in
    /// the register a modern Korean character-driven game uses, so the two are different assets
    /// with different lifetimes and this looks for the second kind only. Nothing exists yet;
    /// every lookup returns null, the frame collapses, and the box renders exactly as it does
    /// now. That is the designed empty state, not a fallback that got left in.
    ///
    /// Lookup is by <c>PortraitKey</c> — the SpeakerId — with the speaker's *name* accepted as
    /// a second key. The name path exists because the only component that draws a line,
    /// <see cref="DialogueView"/>, is handed a name and not an id: the presenter that owns that
    /// hand-off lives in another assembly and another worker's file. Both the English and the
    /// Korean name are registered for a character, so the picture does not disappear when the
    /// language changes.
    ///
    /// To add the art: one PNG per character in <c>Assets/Game/Art/Sprites/Resources/Portraits/</c>,
    /// named for the SpeakerId — <c>npc_professor_01.png</c>, <c>npc_rival_01.png</c>,
    /// <c>npc_gate_01.png</c>, <c>npc_market_01.png</c>, <c>npc_garden_01.png</c>. Import them
    /// as Sprite (2D and UI). Aspect is preserved and the frame is bottom-anchored, so a
    /// full-height standing illustration is cropped at the waist rather than squashed; roughly
    /// 2:3 portrait, transparent background, at least 512px wide.
    /// </summary>
    public static class DialoguePortraits
    {
        /// <summary>Resources sub-folder the illustrations are loaded from.</summary>
        public const string ResourcePath = "Portraits/";

        private static readonly Dictionary<string, Sprite> _byKey =
            new Dictionary<string, Sprite>(StringComparer.Ordinal);

        // Name to key. Ordinal-ignore-case so an author writing "linden" in one file and
        // "Linden" in another does not silently lose the picture.
        private static readonly Dictionary<string, string> _aliases =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Linden", "npc_professor_01" }, { "린든", "npc_professor_01" },
                { "Kes", "npc_rival_01" },        { "케스", "npc_rival_01" },
                { "Bram", "npc_gate_01" },        { "브람", "npc_gate_01" },
                { "Sela", "npc_market_01" },      { "셀라", "npc_market_01" },
                { "Odell", "npc_garden_01" },     { "오델", "npc_garden_01" },
            };

        /// <summary>
        /// Names a character's illustration, so a speaker added later is drawn without this
        /// file having to know about them at compile time.
        /// </summary>
        public static void Register(string speakerName, string portraitKey)
        {
            if (string.IsNullOrEmpty(speakerName) || string.IsNullOrEmpty(portraitKey)) return;
            _aliases[speakerName] = portraitKey;
        }

        /// <summary>
        /// The illustration for a portrait key or a speaker name, or null when there is none.
        ///
        /// Misses are cached as well as hits. Without that, a character with no art costs a
        /// <see cref="Resources.Load"/> — a synchronous file system probe — on every single
        /// line they speak, and the whole cast has no art.
        /// </summary>
        public static Sprite For(string keyOrName)
        {
            if (string.IsNullOrWhiteSpace(keyOrName)) return null;

            var key = _aliases.TryGetValue(keyOrName, out var mapped) ? mapped : keyOrName;
            if (_byKey.TryGetValue(key, out var cached)) return cached;

            var sprite = Resources.Load<Sprite>(ResourcePath + key);
            _byKey[key] = sprite;
            return sprite;
        }

        /// <summary>Drops the cache so newly imported art appears without a domain reload.</summary>
        public static void Reset() => _byKey.Clear();
    }
}
