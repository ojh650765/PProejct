using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>One animated sheet: a texture cut into a grid of equal cells.</summary>
    [Serializable]
    public sealed class SpriteSheetInfo
    {
        [Tooltip("Path used to resolve the texture, relative to a Resources root and without an extension.")]
        public string texture;
        [Tooltip("Cells across the sheet.")]
        public int columns = 1;
        [Tooltip("Cells down the sheet.")]
        public int rows = 1;
        [Tooltip("Frames actually used. May be fewer than columns*rows when the last row is short.")]
        public int frames = 1;
        [Tooltip("Authored playback rate.")]
        public float fps = 12f;

        /// <summary>
        /// Playback order as indices into the sheet's cells. The sheet stores only *unique*
        /// cells — the Gen 5 loops repeat heavily, so Machop's 88-frame animation is 21
        /// distinct images — and this restores the real sequence. Empty means "play cells in
        /// storage order", which is what a sheet without deduplication wants.
        /// </summary>
        public int[] sequence;

        /// <summary>
        /// Per-step duration in milliseconds, parallel to <see cref="sequence"/>. The source
        /// animations are not uniform: the cast runs from 50 ms frames to a 1900 ms hold in
        /// Pidgey's loop, so collapsing them to a single frame rate visibly breaks animations
        /// that are currently correct. Empty falls back to <see cref="fps"/>.
        /// </summary>
        public int[] durationsMs;

        /// <summary>True when this describes something that could actually be sampled.</summary>
        public bool IsUsable => !string.IsNullOrEmpty(texture) && columns > 0 && rows > 0 && frames > 0;

        /// <summary>Number of playback steps, which is the sequence length when one is supplied.</summary>
        public int StepCount => sequence != null && sequence.Length > 0 ? sequence.Length : Mathf.Max(1, frames);

        /// <summary>Maps a playback step to the sheet cell it draws.</summary>
        public int CellAt(int step)
        {
            int steps = StepCount;
            int s = ((step % steps) + steps) % steps;
            if (sequence == null || sequence.Length == 0) return s;
            return Mathf.Clamp(sequence[s], 0, Mathf.Max(0, frames - 1));
        }

        /// <summary>How long a step is held, in seconds.</summary>
        public float StepSeconds(int step)
        {
            int steps = StepCount;
            int s = ((step % steps) + steps) % steps;
            if (durationsMs != null && s < durationsMs.Length && durationsMs[s] > 0)
                return durationsMs[s] * 0.001f;
            return 1f / Mathf.Max(0.5f, fps);
        }

        /// <summary>
        /// Tiling and offset for one frame, as the <c>_ST</c> vector a URP shader's
        /// <c>TRANSFORM_TEX</c> expects: <c>(tiling.x, tiling.y, offset.x, offset.y)</c>.
        ///
        /// <paramref name="flip"/> mirrors horizontally by negating the U tiling and shifting
        /// the offset by one cell, which evaluates to <c>u' = offset + width * (1 - u)</c> —
        /// the <c>uv.x = 1 - uv.x</c> the art direction requires, expressed in the one place
        /// every shader already reads. It is emphatically <b>not</b> a negative X scale: an
        /// odd number of negative scale axes inverts winding and flips normal-map handedness,
        /// which on a lit sprite reads as the creature re-lighting itself backwards the
        /// instant it turns around.
        /// </summary>
        public Vector4 FrameST(int frameIndex, bool flip)
        {
            int c = Mathf.Max(1, columns);
            int r = Mathf.Max(1, rows);
            int count = Mathf.Clamp(frames, 1, c * r);
            int i = ((frameIndex % count) + count) % count;

            float w = 1f / c;
            float h = 1f / r;
            int col = i % c;
            int row = i / c;

            // Sheets are laid out top-down for humans and bottom-up for UV space.
            float offsetX = col * w;
            float offsetY = 1f - (row + 1) * h;

            return flip
                ? new Vector4(-w, h, offsetX + w, offsetY)
                : new Vector4(w, h, offsetX, offsetY);
        }
    }

    /// <summary>One species' entry in the sprite manifest.</summary>
    [Serializable]
    public sealed class CreatureSpriteEntry
    {
        public int speciesId;
        public string nameEn;

        [Tooltip("Static front sprite, used when no animated front sheet is present.")]
        public string front;
        [Tooltip("Static back sprite, used when no animated back sheet is present.")]
        public string back;

        [Tooltip("Animated front idle. Shown when the creature faces the camera — the opponent, always.")]
        public SpriteSheetInfo frontAnim;
        [Tooltip("Animated back idle. Shown when the creature faces away — the player's creature, always.")]
        public SpriteSheetInfo backAnim;

        [Tooltip("Fraction of the frame height, measured from the bottom, at which the creature's feet sit. " +
                 "Gen 5 sprites are padded, so this is what stops a creature floating or sinking.")]
        public float groundOrigin;
        [Tooltip("Fraction of the frame height the drawn creature actually occupies, feet to crown. " +
                 "Used to map the sprite onto the registry's display height in metres.")]
        public float contentHeight = 1f;
    }

    /// <summary>
    /// The root object of <c>Assets/Game/Art/Sprites/sprite_manifest.json</c>.
    ///
    /// <b>This worker does not author sprites and does not write that folder.</b> The shape
    /// below is the contract this code reads; where the sprite worker's manifest differs, the
    /// fix belongs here and not there. Everything degrades: a missing manifest, a missing
    /// entry, a missing sheet and a missing texture all fall through to the next-best source
    /// and finally to the procedural placeholder, because during partial integration all four
    /// are missing at once and the battle still has to be reviewable.
    /// </summary>
    [Serializable]
    public sealed class CreatureSpriteManifest
    {
        public string schema;
        [Tooltip("Square source resolution every species shares. Gen 5 is 96; the pivot targets 256.")]
        public int frameSize = 96;
        [Tooltip("Resources sub-path prepended to every texture path in the manifest.")]
        public string resourceRoot = "";
        public CreatureSpriteEntry[] creatures;
    }

    /// <summary>
    /// Resolves a species id to the textures and sheet layouts a
    /// <see cref="CreatureBillboard"/> needs.
    ///
    /// Three sources, tried in order, and the ordering is the whole design:
    ///
    /// <list type="number">
    /// <item><b>A prefab from <c>ICreatureArtRegistry</c>.</b> The shipping path. Whatever the
    /// sprite worker produces, the integrator turns it into a prefab with the textures
    /// serialized on it, and a serialized reference is the only thing that reliably survives
    /// into a player build. If that prefab already carries a <see cref="CreatureBillboard"/>,
    /// this class is not consulted at all.</item>
    /// <item><b>The manifest plus <c>Resources.Load</c>.</b> The integration path, for while
    /// the prefabs do not exist yet. Requires the sprite worker's textures to sit under a
    /// <c>Resources</c> folder, which is why it is second and not first.</item>
    /// <item><b>The registry portrait.</b> A single static frame, no animation. Worse than
    /// either of the above and far better than a grey ellipsoid.</item>
    /// </list>
    /// </summary>
    public sealed class CreatureSpriteLibrary
    {
        /// <summary>The path the sprite worker writes, for editor tooling and error messages.</summary>
        public const string ManifestAssetPath = "Assets/Game/Art/Sprites/sprite_manifest.json";

        /// <summary>Name the manifest is looked up under when it has been placed in a Resources folder.</summary>
        public const string ManifestResourceName = "sprite_manifest";

        private static CreatureSpriteLibrary s_shared;

        private readonly Dictionary<int, CreatureSpriteEntry> _byId = new Dictionary<int, CreatureSpriteEntry>();
        private readonly Dictionary<string, Texture2D> _textures = new Dictionary<string, Texture2D>();
        private readonly HashSet<int> _warned = new HashSet<int>();
        private string _resourceRoot = string.Empty;

        /// <summary>Square source resolution, from the manifest. 0 when no manifest was found.</summary>
        public int FrameSize { get; private set; }

        /// <summary>Number of species the manifest described. 0 means it was absent or unreadable.</summary>
        public int Count => _byId.Count;

        /// <summary>True when a manifest was found and parsed.</summary>
        public bool HasManifest => _byId.Count > 0;

        /// <summary>
        /// The process-wide library. Built once on first use from whatever can be found;
        /// <see cref="Reset"/> discards it so a domain reload or a re-import picks up changes.
        /// </summary>
        public static CreatureSpriteLibrary Shared => s_shared ??= LoadShared();

        /// <summary>Drops the cached library. Call after re-importing sprite art.</summary>
        public static void Reset() => s_shared = null;

        /// <summary>Builds a library from manifest JSON. Exposed so a caller can supply a TextAsset.</summary>
        public static CreatureSpriteLibrary FromJson(string json)
        {
            var library = new CreatureSpriteLibrary();
            library.Ingest(json);
            return library;
        }

        private static CreatureSpriteLibrary LoadShared()
        {
            var library = new CreatureSpriteLibrary();

            // Resources first: it is the only source that works in a player build without a
            // serialized reference, so if it is present it is the one that was set up on purpose.
            var asset = Resources.Load<TextAsset>(ManifestResourceName);
            if (asset != null)
            {
                library.Ingest(asset.text);
                if (library.HasManifest) return library;
            }

#if UNITY_EDITOR
            // In the editor, read the authored file directly. This is what makes the sprite
            // worker's output visible to a Play Mode review the moment it lands, before anyone
            // has moved it into Resources or built a prefab.
            try
            {
                if (System.IO.File.Exists(ManifestAssetPath))
                {
                    library.Ingest(System.IO.File.ReadAllText(ManifestAssetPath));
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CreatureSpriteLibrary] Could not read {ManifestAssetPath}: {ex.Message}");
            }
#endif
            return library;
        }

        private void Ingest(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            CreatureSpriteManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<CreatureSpriteManifest>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CreatureSpriteLibrary] Sprite manifest did not parse: {ex.Message}. " +
                                 "Creature views will fall back to the registry portrait or the placeholder.");
                return;
            }

            if (manifest?.creatures == null) return;

            FrameSize = manifest.frameSize > 0 ? manifest.frameSize : 96;
            _resourceRoot = string.IsNullOrEmpty(manifest.resourceRoot)
                ? string.Empty
                : manifest.resourceRoot.TrimEnd('/') + "/";

            foreach (var entry in manifest.creatures)
            {
                if (entry == null || entry.speciesId <= 0) continue;
                // contentHeight of 0 would divide the world by zero when the quad is sized.
                if (entry.contentHeight <= 0.01f) entry.contentHeight = 1f;
                entry.groundOrigin = Mathf.Clamp01(entry.groundOrigin);
                _byId[entry.speciesId] = entry;
            }
        }

        /// <summary>The manifest entry for a species, or null.</summary>
        public CreatureSpriteEntry Entry(int speciesId)
        {
            if (_byId.TryGetValue(speciesId, out var entry)) return entry;

            // Warn once per species. Repeating this every frame during a battle would bury the
            // console in exactly the situation where you need to read it.
            if (HasManifest && _warned.Add(speciesId))
            {
                Debug.LogWarning($"[CreatureSpriteLibrary] No sprite manifest entry for species {speciesId}. " +
                                 "Falling back to the registry portrait, then to the placeholder.");
            }
            return null;
        }

        /// <summary>
        /// Loads a texture named by the manifest. Only <c>Resources.Load</c> is attempted:
        /// anything else would work in the editor and silently produce a blank creature in a
        /// build, which is the worst of the available failures.
        /// </summary>
        public Texture2D Texture(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_textures.TryGetValue(path, out var cached)) return cached;

            string trimmed = StripExtension(path);
            Texture2D texture = Resources.Load<Texture2D>(_resourceRoot + trimmed);
            if (texture == null && !string.IsNullOrEmpty(_resourceRoot)) texture = Resources.Load<Texture2D>(trimmed);

            _textures[path] = texture;
            return texture;
        }

        private static string StripExtension(string path)
        {
            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');
            return dot > slash && dot > 0 ? path.Substring(0, dot) : path;
        }
    }
}
