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

        private static Sprite Build(int speciesId)
        {
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
            return sprite;
        }
    }
}
