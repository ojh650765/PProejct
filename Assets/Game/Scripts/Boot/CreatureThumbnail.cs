using System;
using System.Collections.Generic;
using PokeLab.Cinematics;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// A creature's picture, for any flat UI that wants one.
    ///
    /// <b>Why this exists rather than <c>ICreatureArtRegistry.GetPortrait</c>.</b> That
    /// interface is the obvious answer and it returns null everywhere outside a battle. The
    /// registry that implements it properly, <see cref="CreatureArtRegistry"/>, is built from a
    /// <see cref="CreatureArtCatalog"/> ScriptableObject that <b>does not exist in this
    /// project</b> — `Tools/Poké Lab/Art/Rebuild Creature Art Catalog` has never been run, so
    /// there is no asset. The only thing that ever registers the interface is
    /// <c>BattleArena</c>, and it registers <c>DexDisplayHeights</c>, a stub that answers
    /// heights and returns null for every sprite. On the title screen nothing registers it at
    /// all. The `*_portrait.png` files are real, but they live in
    /// Assets/Game/Art/Sprites/Creatures/, outside any Resources folder, so nothing can load
    /// them at runtime either.
    ///
    /// The symptom was the gacha reveal printing a creature's name over empty space, which is
    /// what the user noticed: 포켓몬 이름만 출력되는 경우에 sprite도 나왔으면 좋겠는데.
    ///
    /// <b>What this uses instead.</b> The one resolution path in the project known to work:
    /// the GAME species id → <c>sprite_manifest.json</c> → <c>Resources/PokeLabSprites</c>,
    /// through <see cref="CreatureSpriteLibrary"/>. It is how every creature in a battle gets
    /// its picture and how the starter cards get theirs. The frame taken is cell 0 of the front
    /// idle sheet — the same cut <c>StarterPresenter.FrontSprite</c> makes, lifted here so that
    /// the gacha, the VS board and anything after them share one implementation instead of a
    /// third and fourth copy of the arithmetic.
    ///
    /// A front sheet is arguably better than a portrait for these screens anyway: it is the
    /// whole creature, at the size the battle draws it, rather than a cropped head.
    ///
    /// Cached per species for the lifetime of the process. The sprites reference textures that
    /// Resources owns, so nothing here needs unloading.
    /// </summary>
    public static class CreatureThumbnail
    {
        private static readonly Dictionary<int, Sprite> Cache = new Dictionary<int, Sprite>(64);

        /// <summary>
        /// The species whose cached Sprite this class MADE, rather than loaded.
        ///
        /// The two kinds have opposite disposal rules and mixing them up is a real bug in each
        /// direction. A sliced-atlas thumbnail comes from Sprite.Create and belongs to nobody,
        /// so dropping the handle leaks it and it has to be Destroyed. A portrait is a Resources
        /// asset that the engine owns; Destroying one removes it from the whole session, and the
        /// next screen that asks for it gets null.
        /// </summary>
        private static readonly HashSet<int> Owned = new HashSet<int>();

        /// <summary>Species id to Resources path, from portrait_manifest.json. Null until read.</summary>
        private static Dictionary<int, string> s_portraits;

        [Serializable]
        private sealed class PortraitEntry
        {
            public int speciesId;
            public string resource;
        }

        [Serializable]
        private sealed class PortraitManifest
        {
            public PortraitEntry[] portraits;
        }

        /// <summary>
        /// Reads the portrait index once.
        ///
        /// An empty table is a valid outcome and not an error worth shouting about: a build
        /// without the portraits still runs, it just draws creatures from the battle sheets the
        /// way it always did.
        /// </summary>
        private static Dictionary<int, string> Portraits()
        {
            if (s_portraits != null) return s_portraits;
            s_portraits = new Dictionary<int, string>(64);

            var asset = Resources.Load<TextAsset>("portrait_manifest");
            if (asset == null) return s_portraits;

            PortraitManifest manifest = null;
            try { manifest = JsonUtility.FromJson<PortraitManifest>(asset.text); }
            catch (Exception ex)
            {
                Debug.LogWarning("[Art] portrait_manifest.json did not parse: " + ex.Message +
                                 ". UI creatures will come from the battle sheets instead.");
            }

            if (manifest?.portraits == null) return s_portraits;

            foreach (var entry in manifest.portraits)
                if (entry != null && entry.speciesId > 0 && !string.IsNullOrEmpty(entry.resource))
                    s_portraits[entry.speciesId] = entry.resource;

            return s_portraits;
        }

        /// <summary>
        /// The front view of a species, or null when the manifest has no usable entry.
        ///
        /// Null is a legitimate answer and callers must draw around it — a name with no picture
        /// is a degraded card, and a blank rectangle where a picture should be is a broken one.
        /// </summary>
        public static Sprite Front(int speciesId)
        {
            if (Cache.TryGetValue(speciesId, out var cached)) return cached;

            var sprite = Build(speciesId);

            if (sprite == null)
            {
                Debug.LogWarning($"[Art] No front sprite for game species {speciesId}; the card " +
                                 "will show its name without a picture. Entries belong in " +
                                 "sprite_manifest.json under the GAME id — not the national dex " +
                                 "number — with the texture under Resources/PokeLabSprites.");
            }

            Cache[speciesId] = sprite;
            return sprite;
        }

        /// <summary>
        /// Forgets every thumbnail, and destroys the Sprite objects that held them.
        ///
        /// <b>This is the half that actually frees memory.</b> Each entry is a Sprite made with
        /// Sprite.Create over a shared atlas texture, so while the entry lives the atlas is
        /// referenced — and <c>Resources.UnloadUnusedAssets</c> frees nothing that is
        /// referenced. Clearing the dictionary alone is not enough either: Sprite.Create mints a
        /// new UnityEngine.Object that belongs to nobody, and dropping the last managed handle to
        /// it leaks it rather than collecting it. Both have to go, in that order.
        ///
        /// Only safe when nothing on screen is drawing one. <see cref="MemoryRelief"/> calls this
        /// on a single-mode scene load, where every canvas is being torn down anyway; calling it
        /// under a live menu would blank the pictures on it. Rebuilding is cheap — one
        /// Sprite.Create against a texture that is usually still resident.
        /// </summary>
        public static void Clear()
        {
            foreach (var pair in Cache)
            {
                // Only what this class minted. A portrait is the engine's; destroying one would
                // take it out of the session for everybody.
                if (pair.Value != null && Owned.Contains(pair.Key))
                    UnityEngine.Object.Destroy(pair.Value);
            }

            Cache.Clear();
            Owned.Clear();
        }

        private static Sprite Build(int speciesId)
        {
            // The 512 px render first, and this is the whole reason this class changed.
            //
            // A battle sheet's cell is 96x96, and 96 px is every pixel the player has ever been
            // given -- which is fine on a billboard across an arena and falls apart on a gacha
            // card four hundred pixels tall. The user put it plainly: 확대했을 때 화질 나쁜게
            // 너무 티나. These portraits are the same creatures at 512, so the UI is drawing
            // roughly twenty-eight times the pixels it had.
            //
            // It is also most of a memory fix. Slicing a sheet meant every species the UI had
            // ever shown held its multi-megabyte atlas open forever, through a Sprite this class
            // cached and never released -- five gacha pulls in a row is thirty atlases that
            // nothing can free, and that is what the OOM reports were. A portrait is a single
            // 512 texture the UI can be the only owner of.
            if (Portraits().TryGetValue(speciesId, out var resource))
            {
                var portrait = Resources.Load<Sprite>(resource);
                if (portrait != null) return portrait;

                Debug.LogWarning($"[Art] portrait_manifest.json points species {speciesId} at " +
                                 $"'{resource}', which Resources.Load could not find. Falling " +
                                 "back to the battle sheet.");
            }

            var library = CreatureSpriteLibrary.Shared;
            var entry = library?.Entry(speciesId);
            if (entry == null) return null;

            var sheet = entry.frontAnim != null && entry.frontAnim.IsUsable ? entry.frontAnim : null;
            var texture = library.Texture(sheet != null ? sheet.texture : entry.front);
            if (texture == null) return null;

            var columns = Mathf.Max(1, sheet != null ? sheet.columns : 1);
            var rows = Mathf.Max(1, sheet != null ? sheet.rows : 1);
            var cell = sheet != null ? sheet.CellAt(0) : 0;

            var cellWidth = texture.width / columns;
            var cellHeight = texture.height / rows;
            if (cellWidth <= 0 || cellHeight <= 0) return null;

            var column = cell % columns;
            var row = cell / columns;

            // Row 0 is the TOP of the sheet, and a Sprite's rect counts up from the bottom.
            // Getting this backwards yields a picture of a different frame rather than an
            // error, which is why it is spelled out here and in StarterPresenter.
            var rect = new Rect(column * cellWidth, texture.height - (row + 1) * cellHeight,
                cellWidth, cellHeight);

            var sprite = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), cellHeight);
            sprite.name = "~Thumb_" + speciesId;
            Owned.Add(speciesId);
            return sprite;
        }
    }
}
