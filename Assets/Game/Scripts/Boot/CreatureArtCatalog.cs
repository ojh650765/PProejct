using System;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Serialized species-to-art mapping, built from
    /// <c>Assets/Game/Art/Creatures/creature_manifest.json</c> by the editor tool.
    ///
    /// An asset rather than a runtime JSON read because the FBX files live outside
    /// Resources and Addressables: only a serialized reference gets them into a build.
    /// Regenerating the catalogue after an art rebuild is a one-click operation.
    /// </summary>
    [CreateAssetMenu(menuName = "Poké Lab/Creature Art Catalog", fileName = "CreatureArtCatalog")]
    public sealed class CreatureArtCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public int SpeciesId;
            public string NameEn;
            public GameObject Model;
            public Sprite Portrait;

            /// <summary>Authored height in metres. Drives camera framing and HP bar placement.</summary>
            public float DisplayHeight = 1f;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();

        public IReadOnlyList<Entry> Entries => entries;

        public void Replace(List<Entry> newEntries) => entries = newEntries;
    }

    /// <summary>
    /// <see cref="ICreatureArtRegistry"/> over a <see cref="CreatureArtCatalog"/>.
    ///
    /// Every lookup is survivable: a missing species returns null and a default height
    /// rather than throwing, because the presentation layers are contractually required to
    /// fall back to a placeholder. A hard failure here would take down a whole battle over
    /// one unbuilt model.
    /// </summary>
    public sealed class CreatureArtRegistry : ICreatureArtRegistry
    {
        private readonly Dictionary<int, CreatureArtCatalog.Entry> _byId =
            new Dictionary<int, CreatureArtCatalog.Entry>();

        private readonly HashSet<int> _warned = new HashSet<int>();

        public CreatureArtRegistry(CreatureArtCatalog catalog)
        {
            if (catalog == null) return;
            foreach (var entry in catalog.Entries)
            {
                if (entry == null || entry.SpeciesId <= 0) continue;
                _byId[entry.SpeciesId] = entry;
            }
        }

        public int Count => _byId.Count;

        public GameObject GetCreaturePrefab(int speciesId) =>
            TryGet(speciesId, out var e) ? e.Model : null;

        public Sprite GetPortrait(int speciesId) =>
            TryGet(speciesId, out var e) ? e.Portrait : null;

        public float GetDisplayHeight(int speciesId) =>
            TryGet(speciesId, out var e) && e.DisplayHeight > 0f ? e.DisplayHeight : 1f;

        private bool TryGet(int speciesId, out CreatureArtCatalog.Entry entry)
        {
            if (_byId.TryGetValue(speciesId, out entry)) return true;

            // Warn once per species. Repeating this every frame during a battle would bury
            // the console in the exact situation where you need to read it.
            if (_warned.Add(speciesId))
                Debug.LogWarning($"[CreatureArt] No art for species {speciesId}. " +
                                 "Falling back to the placeholder rig. Rebuild the catalogue " +
                                 "from Tools > Poké Lab > Art > Rebuild Creature Art Catalog.");
            return false;
        }
    }
}
