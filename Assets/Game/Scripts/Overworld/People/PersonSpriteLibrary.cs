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

    [Serializable]
    internal sealed class PersonManifest
    {
        public string schema;
        public int frameSize = 32;
        public float pixelsPerUnit = 13.714286f;
        public string resourceRoot = "";
        public PersonEntry[] people;
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

            if (manifest?.people == null) return;
            FrameSize = Mathf.Max(1, manifest.frameSize);
            _resourceRoot = manifest.resourceRoot ?? string.Empty;

            foreach (var entry in manifest.people)
            {
                if (entry == null || string.IsNullOrEmpty(entry.key)) continue;
                _byKey[entry.key] = entry;
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

            if (_warned.Add(key))
                Debug.LogWarning($"[People] No art for '{key}'; that character will not be drawn.");
            return null;
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
