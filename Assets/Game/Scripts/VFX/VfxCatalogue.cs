using System;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Maps a VfxKey string to something playable.
    ///
    /// Two sources, in priority order:
    ///   1. A prefab override assigned in the inspector. Anything the integrator or
    ///      a later art pass wants to hand-author wins here.
    ///   2. The built-in procedural recipe libraries. These cover every key the
    ///      battle engine can emit, so an unassigned catalogue is fully functional
    ///      rather than merely non-crashing.
    ///
    /// That ordering is the point: the engine can start emitting MoveExecutedEvents
    /// with VfxKeys today and every one of them resolves to something on screen,
    /// while leaving a clean upgrade path for hand-authored effects later.
    /// </summary>
    [CreateAssetMenu(menuName = "PokeLab/VFX Catalogue", fileName = "VfxCatalogue")]
    public sealed class VfxCatalogue : ScriptableObject
    {
        [Serializable]
        public struct PrefabEntry
        {
            [Tooltip("The VfxKey the battle engine emits, or a library key such as " +
                     "\"move.fire.impact\".")]
            public string Key;
            public GameObject Prefab;
        }

        [Tooltip("Hand-authored overrides. Anything listed here beats the built-in recipe.")]
        [SerializeField] private PrefabEntry[] prefabOverrides = Array.Empty<PrefabEntry>();

        [Tooltip("Turn off only to test what the game looks like with prefabs alone.")]
        [SerializeField] private bool useBuiltInLibrary = true;

        private Dictionary<string, GameObject> prefabLookup;
        private Dictionary<string, VfxRecipe> recipes;

        /// <summary>Every recipe key the catalogue can serve. Diagnostics and tests.</summary>
        public IEnumerable<string> RecipeKeys
        {
            get
            {
                EnsureBuilt();
                return recipes.Keys;
            }
        }

        private void EnsureBuilt()
        {
            if (recipes != null) return;

            recipes = new Dictionary<string, VfxRecipe>(StringComparer.OrdinalIgnoreCase);
            if (useBuiltInLibrary)
            {
                MoveVfxLibrary.AddAll(recipes);
                BattleStateVfxLibrary.AddAll(recipes);
                AmbientVfxLibrary.AddAll(recipes);
            }

            prefabLookup = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            if (prefabOverrides != null)
            {
                for (int i = 0; i < prefabOverrides.Length; i++)
                {
                    var entry = prefabOverrides[i];
                    if (string.IsNullOrWhiteSpace(entry.Key) || entry.Prefab == null) continue;
                    prefabLookup[entry.Key.Trim()] = entry.Prefab;
                }
            }
        }

        public bool TryGetPrefab(string key, out GameObject prefab)
        {
            EnsureBuilt();
            prefab = null;
            return !string.IsNullOrEmpty(key) && prefabLookup.TryGetValue(key, out prefab);
        }

        public bool TryGetRecipe(string key, out VfxRecipe recipe)
        {
            EnsureBuilt();
            recipe = null;
            return !string.IsNullOrEmpty(key) && recipes.TryGetValue(key, out recipe);
        }

        /// <summary>
        /// The resolution the presenter actually uses. A move may ship a bespoke
        /// VfxKey; if it is unknown we fall back to the move's element type, which
        /// is always defined. That is why an unfinished move list still looks right.
        /// </summary>
        public bool Resolve(string vfxKey, ElementType type, VfxPhase phase,
                            out string resolvedKey, out GameObject prefab, out VfxRecipe recipe)
        {
            EnsureBuilt();
            resolvedKey = null;
            prefab = null;
            recipe = null;

            // A bespoke key may be phase-qualified already ("flamethrower.impact")
            // or bare ("flamethrower"), so try both before giving up on it.
            if (!string.IsNullOrWhiteSpace(vfxKey))
            {
                string trimmed = vfxKey.Trim();
                string phaseSuffix = "." + phase.ToString().ToLowerInvariant();
                if (TryResolveExact(trimmed + phaseSuffix, out resolvedKey, out prefab, out recipe)) return true;
                if (TryResolveExact(trimmed, out resolvedKey, out prefab, out recipe)) return true;
            }

            // Fall back to the element type. ElementType.None is possible on a status
            // move with no key, so treat it as Normal rather than returning nothing.
            ElementType resolved = type == ElementType.None ? ElementType.Normal : type;
            return TryResolveExact(MoveVfxLibrary.KeyFor(resolved, phase),
                                   out resolvedKey, out prefab, out recipe);
        }

        private bool TryResolveExact(string key, out string resolvedKey,
                                     out GameObject prefab, out VfxRecipe recipe)
        {
            recipe = null;
            resolvedKey = key;
            if (prefabLookup.TryGetValue(key, out prefab)) return true;
            if (recipes.TryGetValue(key, out recipe)) return true;

            resolvedKey = null;
            return false;
        }

        /// <summary>
        /// Builds a catalogue in memory. Used when nobody assigned one, so the VFX
        /// system works in a bare scene with no setup at all.
        /// </summary>
        public static VfxCatalogue CreateRuntimeDefault()
        {
            var catalogue = CreateInstance<VfxCatalogue>();
            catalogue.name = "PL_RuntimeVfxCatalogue";
            catalogue.hideFlags = HideFlags.HideAndDontSave;
            catalogue.useBuiltInLibrary = true;
            catalogue.EnsureBuilt();
            return catalogue;
        }
    }
}
