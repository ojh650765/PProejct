using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Overworld.People
{
    /// <summary>One animation clip: a sequence of frame indices over a sheet.</summary>
    [Serializable]
    public sealed class PersonClip
    {
        public string texture;
        public int columns = 1;
        public int rows = 1;
        public int frames = 1;
        public float fps = 8f;
        public int[] sequence;
        public int[] durationsMs;

        public bool IsValid => sequence != null && sequence.Length > 0 && columns > 0 && rows > 0;

        /// <summary>Seconds frame <paramref name="step"/> of the sequence is held for.</summary>
        public float DurationOf(int step)
        {
            if (durationsMs != null && step < durationsMs.Length && durationsMs[step] > 0)
                return durationsMs[step] / 1000f;
            return fps > 0f ? 1f / fps : 0.125f;
        }
    }

    /// <summary>
    /// One character's art: three drawn views, each with idle, walk and run.
    ///
    /// Side is a single sheet drawn facing one way; the other side is the same sheet
    /// mirrored, which is why <see cref="sideWalksScreenLeft"/> has to be honoured rather
    /// than assumed — get it wrong and the character moonwalks.
    /// </summary>
    [Serializable]
    public sealed class PersonEntry
    {
        public string key;
        public string nameEn;
        public string role;

        [Tooltip("Height of the drawn character in metres — the content, not the frame.")]
        public float displayHeightMetres = 1.6f;

        [Tooltip("Where the feet sit inside the frame, as a fraction of frame height from the bottom.")]
        public float groundOrigin = 0.0625f;

        [Tooltip("Fraction of the frame the character occupies vertically.")]
        public float contentHeight = 0.71875f;

        public bool sideWalksScreenLeft = true;

        public PersonClip frontIdle, frontWalk, frontRun;
        public PersonClip backIdle, backWalk, backRun;
        public PersonClip sideIdle, sideWalk, sideRun;

        /// <summary>
        /// Height of the whole frame in metres. The quad is built to this, not to
        /// <see cref="displayHeightMetres"/>: the sprite's empty margins are part of the
        /// image, and sizing the quad to the content would scale the character up by the
        /// margin and lift their feet off the ground.
        /// </summary>
        public float FrameHeightMetres =>
            contentHeight > 0.001f ? displayHeightMetres / contentHeight : displayHeightMetres;
    }

    /// <summary>
    /// A drawn object rather than a drawn person: the starter briefcase, the player reaching
    /// into it.
    ///
    /// Its own type rather than a <see cref="PersonEntry"/> with the unused views left null,
    /// because a prop is not a character seen from three sides — it has one drawing and one
    /// optional loop, and the manifest names them <c>Idle</c> and <c>Play</c> rather than
    /// front/back/side. <c>JsonUtility</c> matches field names exactly, which is why those two
    /// are capitalised here against the convention: they are the shape of the data, not a
    /// choice made in this file.
    /// </summary>
    [Serializable]
    public sealed class PersonProp
    {
        public string key;
        public string nameEn;

        [Tooltip("Height of the drawn object in metres — the content, not the frame.")]
        public float displayHeightMetres = 1f;

        [Tooltip("Where the object's base sits inside the frame, as a fraction from the bottom.")]
        public float groundOrigin = 0.0625f;

        [Tooltip("Fraction of the frame the object occupies vertically.")]
        public float contentHeight = 0.5f;

        public string texture;
        public PersonClip Idle;
        public PersonClip Play;

        /// <summary>
        /// Height of the whole frame in metres — the quad's size, for
        /// <see cref="PersonEntry.FrameHeightMetres"/>'s reason: the margins are part of the
        /// image, and sizing to the content scales the object up by the margin.
        /// </summary>
        public float FrameHeightMetres =>
            contentHeight > 0.001f ? displayHeightMetres / contentHeight : displayHeightMetres;
    }

    [Serializable]
    internal sealed class PersonManifest
    {
        public string schema;
        public int frameSize = 32;
        public float pixelsPerUnit = 13.714286f;
        public string resourceRoot = "";
        public PersonEntry[] people;
        public PersonProp[] props;
    }

    /// <summary>
    /// Resolves a person key — "player", "rival", "gardener" — to sheets and textures.
    ///
    /// The people manifest asked for this by name: <c>CreatureSpriteLibrary</c> is keyed on
    /// species id and people have no species id, so the two cannot share a reader however
    /// similar the JSON looks.
    ///
    /// Loading is through <c>Resources.Load</c>, which is why the art lives under
    /// <c>Assets/Game/Art/Sprites/Resources/</c>. Everything degrades quietly to null and
    /// the caller draws nothing rather than throwing, because a missing sheet should cost
    /// one character, not the scene.
    /// </summary>
    public sealed class PersonSpriteLibrary
    {
        public const string ManifestResourceName = "people_manifest";

        private static PersonSpriteLibrary s_shared;

        private readonly Dictionary<string, PersonEntry> _byKey =
            new Dictionary<string, PersonEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PersonProp> _props =
            new Dictionary<string, PersonProp>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Texture2D> _textures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _resourceRoot = string.Empty;

        public int FrameSize { get; private set; } = 32;
        public int Count => _byKey.Count;

        public static PersonSpriteLibrary Shared => s_shared ??= Load();

        /// <summary>Drops the cache so re-imported art is picked up.</summary>
        public static void Reset() => s_shared = null;

        private static PersonSpriteLibrary Load()
        {
            var library = new PersonSpriteLibrary();
            var text = Resources.Load<TextAsset>(ManifestResourceName);
            if (text == null)
            {
                Debug.LogWarning(
                    $"[People] No '{ManifestResourceName}' in any Resources folder, so every " +
                    "person in the world is an invisible collider. The manifest ships at " +
                    "Assets/Game/Art/Sprites/Resources/people_manifest.json.");
                return library;
            }
            library.Ingest(text.text);
            return library;
        }

        private void Ingest(string json)
        {
            PersonManifest manifest;
            try { manifest = JsonUtility.FromJson<PersonManifest>(json); }
            catch (Exception e)
            {
                Debug.LogWarning($"[People] The manifest would not parse: {e.Message}");
                return;
            }

            if (manifest == null) return;
            FrameSize = Mathf.Max(1, manifest.frameSize);
            _resourceRoot = manifest.resourceRoot ?? string.Empty;

            foreach (var entry in manifest.people ?? Array.Empty<PersonEntry>())
            {
                if (entry == null || string.IsNullOrEmpty(entry.key)) continue;
                _byKey[entry.key] = entry;
            }

            foreach (var prop in manifest.props ?? Array.Empty<PersonProp>())
            {
                if (prop == null || string.IsNullOrEmpty(prop.key)) continue;
                _props[prop.key] = prop;
            }
        }

        /// <summary>
        /// Finds a character, trying the key then a small set of aliases.
        ///
        /// The level layout names people by role — "Stallholder", "GateKeeper" — while the
        /// art is keyed by the drawn archetype. Rather than require the two vocabularies to
        /// match, which would couple a level rebuild to an art rebuild, unknown keys map
        /// onto the nearest drawn character and the world stays populated.
        /// </summary>
        public PersonEntry Find(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_byKey.TryGetValue(key, out var entry)) return entry;

            foreach (var alias in Aliases(key))
                if (_byKey.TryGetValue(alias, out entry))
                    return entry;

            // Level names say where a character is as well as who they are —
            // Trainer_Route_Youngster, Trainer_Cave_Hiker — while the art is keyed on the
            // archetype alone. Walking in from the right drops the placement and keeps the
            // person, so a trainer moved from the route to the lake keeps their sprite
            // instead of silently losing it.
            // A trailing instance number is placement too: npc_professor_01 and
            // NPC_House_02 both name one of something, and the art is drawn per role.
            var tail = key.LastIndexOf('_');
            if (tail > 0 && int.TryParse(key.Substring(tail + 1), out _))
            {
                var withoutIndex = key.Substring(0, tail);
                if (_byKey.TryGetValue(withoutIndex, out entry)) return entry;
                key = withoutIndex;
            }

            var trimmed = key;
            while (true)
            {
                var cut = trimmed.IndexOf('_');
                if (cut < 0 || cut + 1 >= trimmed.Length) break;
                trimmed = trimmed.Substring(cut + 1);

                if (_byKey.TryGetValue(trimmed, out entry)) return entry;
                foreach (var alias in Aliases(trimmed))
                    if (_byKey.TryGetValue(alias, out entry))
                        return entry;
            }

            if (_warned.Add(key))
                Debug.LogWarning($"[People] No art for '{key}'; that character will not be drawn.");
            return null;
        }

        /// <summary>
        /// Finds a drawn prop — "briefcase", "player_takes_ball".
        ///
        /// No aliasing and no fallback, unlike <see cref="Find"/>. A person the level names by
        /// a role it invented should still be drawn as the nearest archetype; a prop is asked
        /// for by name by exactly one scene, and quietly handing back a different object is
        /// worse than drawing nothing.
        /// </summary>
        public PersonProp Prop(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_props.TryGetValue(key, out var prop)) return prop;

            if (_warned.Add("prop:" + key))
                Debug.LogWarning($"[People] No prop '{key}' in the manifest; it will not be drawn.");
            return null;
        }

        /// <summary>
        /// Every art key this speaker key could mean, best first.
        ///
        /// Exposed because the dialogue box has to make the same journey. A speaker is
        /// "npc_gate_01"; the world sprite for him is filed under "townsman", and the box's
        /// portrait is filed under "townsman" too — but the box was resolving only the prefix
        /// and the trailing number, landing on "gate", and drawing the walking sprite because
        /// no portrait is filed under a place name. Two lookups that disagree about who
        /// somebody is will always disagree about what to draw.
        /// </summary>
        public static IEnumerable<string> ArtKeysFor(string key)
        {
            if (string.IsNullOrEmpty(key)) yield break;
            yield return key;
            foreach (var alias in Aliases(key)) yield return alias;
        }

        private static IEnumerable<string> Aliases(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "stallholder": case "merchant": case "market": yield return "shopkeeper"; break;
                case "gatekeeper": case "guard": yield return "townsman"; break;
                case "kid": yield return "child"; break;
                case "girl": yield return "lass"; break;
                case "boy": yield return "youngster"; break;
                case "oak": case "prof": yield return "professor"; break;

                // The cast names some speakers by where they stand rather than by what they
                // are — npc_gate_01, npc_market_01 — and cast.json carries the real mapping
                // in its own spriteKey field. These three are that mapping, so a place name
                // still finds the person who works there.
                case "gate": yield return "townsman"; break;
                case "garden": yield return "gardener"; break;
                default: yield break;
            }
        }

        public Texture2D Texture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_textures.TryGetValue(path, out var cached)) return cached;

            var full = string.IsNullOrEmpty(_resourceRoot) ? path : _resourceRoot + "/" + path;
            var texture = Resources.Load<Texture2D>(full);
            if (texture == null && _warned.Add(full))
                Debug.LogWarning($"[People] Texture '{full}' is not under a Resources folder.");

            _textures[path] = texture;
            return texture;
        }
    }
}
