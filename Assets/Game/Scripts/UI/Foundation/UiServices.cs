using System;
using System.Collections.Generic;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// Every cross-system dependency the UI has, resolved through <see cref="ServiceHub"/>
    /// and degraded to something renderable when absent.
    ///
    /// This exists because the UI is built in parallel with all seven other systems, so at
    /// any point during integration none, some, or all of the oracle, engine, profile and
    /// art registry may be registered. Views must never branch on that themselves — a
    /// missing service is not an error state, it is a Tuesday. Everything here answers with
    /// a sensible placeholder instead of throwing, and re-resolves on demand because boot
    /// order registers services after the first UI Awake in some scenes.
    /// </summary>
    public static class UiServices
    {
        private static IPokeLabOracle _oracle;
        private static IBattleEngine _engine;
        private static IPlayerProfile _profile;
        private static ICreatureArtRegistry _art;
        private static ISpeciesRegistry _species;
        private static IMoveRegistry _moves;
        private static IGameFlow _flow;

        /// <summary>The tactical oracle, or null while the intelligence layer is still landing.</summary>
        public static IPokeLabOracle Oracle => Resolve(ref _oracle);
        public static IBattleEngine Engine => Resolve(ref _engine);
        public static IPlayerProfile Profile => Resolve(ref _profile);
        public static ICreatureArtRegistry Art => Resolve(ref _art);
        public static ISpeciesRegistry Species => Resolve(ref _species);
        public static IMoveRegistry Moves => Resolve(ref _moves);
        public static IGameFlow Flow => Resolve(ref _flow);

        private static T Resolve<T>(ref T cached) where T : class
        {
            if (cached != null) return cached;
            ServiceHub.TryGet(out cached);
            return cached;
        }

        /// <summary>Drops cached references. Call alongside <see cref="ServiceHub.Reset"/>.</summary>
        public static void Reset()
        {
            _oracle = null;
            _engine = null;
            _profile = null;
            _art = null;
            _species = null;
            _moves = null;
            _flow = null;
        }

        // ---------------------------------------------------------------- lookups

        /// <summary>
        /// Display name for a creature: nickname, then species name, then a stable
        /// "Species #12" so a list never renders a blank row during integration.
        /// </summary>
        public static string NameOf(CreatureInstance creature)
        {
            if (creature == null) return "—";
            if (!string.IsNullOrWhiteSpace(creature.Nickname)) return creature.Nickname;
            return SpeciesName(creature.SpeciesId);
        }

        /// <summary>
        /// Species display name, falling back to an id when the registry is absent.
        ///
        /// <b>The empty case is checked, not assumed.</b> This used to return
        /// <c>data.DisplayName</c> the moment a row was found, and DisplayName is
        /// <c>Loc.Pick(NameEn, NameKo)</c> — a field, not a computation, so a row whose name
        /// column did not survive the export comes back as an empty string and every screen
        /// drawing it renders a blank where a creature's name should be. That is a much worse
        /// failure than the missing-registry one this method was written for, because it looks
        /// like a layout bug rather than a data one and sends you looking in the wrong file.
        /// A blank is turned into the id and said out loud once, so the next report names the
        /// species instead of the symptom.
        /// </summary>
        public static string SpeciesName(int speciesId)
        {
            var registry = Species;
            if (registry != null && registry.TryGet(speciesId, out var data) && data != null)
            {
                var name = data.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name;
                WarnOnce(speciesId);
            }
            return speciesId > 0 ? "Species #" + speciesId : "Unknown";
        }

        private static readonly HashSet<int> NamelessReported = new HashSet<int>();

        private static void WarnOnce(int speciesId)
        {
            if (!NamelessReported.Add(speciesId)) return;
            Debug.LogWarning($"[UiServices] Species {speciesId} is in the registry with an EMPTY " +
                             "display name, so every list showing it would have drawn a blank. " +
                             "Showing the id instead. Check NameEn/NameKo for that row in " +
                             "species.json.");
        }

        /// <summary>Both types of a species. Returns <see cref="ElementType.None"/> pairs when unknown.</summary>
        public static void TypesOf(int speciesId, out ElementType primary, out ElementType secondary)
        {
            primary = ElementType.None;
            secondary = ElementType.None;
            var registry = Species;
            if (registry != null && registry.TryGet(speciesId, out var data) && data != null)
            {
                primary = data.Type1;
                secondary = data.Type2;
            }
        }

        /// <summary>Base stats for a species, or a zeroed array so the stat spread still draws.</summary>
        public static int[] BaseStatsOf(int speciesId)
        {
            var registry = Species;
            if (registry != null && registry.TryGet(speciesId, out var data) && data?.BaseStats != null)
            {
                return data.BaseStats;
            }
            return EmptyStats;
        }

        private static readonly int[] EmptyStats = new int[StatKinds.BaseCount];

        /// <summary>
        /// Where a species portrait comes from when no art registry answers for it.
        ///
        /// <b>Why this hook exists.</b> The battle screens ask <see cref="ICreatureArtRegistry"/>
        /// for portraits, and nothing in this project registers one with real art -- the arena
        /// installs a stub that answers heights and returns null for every sprite. Meanwhile the
        /// menu screens draw the same creatures perfectly, because they go through the sprite
        /// sheet loader in PokeLab.Boot instead. The two could not simply be joined up: Boot
        /// references this assembly, so this assembly cannot reference Boot back.
        ///
        /// So Boot pushes the resolver in at startup and every screen in the game, including the
        /// battle swap list, draws the creature rather than a type glyph. Left unset -- in a test
        /// fixture, or a scene opened on its own -- the behaviour is exactly what it used to be.
        /// </summary>
        public static Func<int, Sprite> PortraitResolver;

        /// <summary>Portrait sprite, or null. Callers show the type glyph as a stand-in.</summary>
        public static Sprite PortraitOf(int speciesId)
        {
            var art = Art;
            var fromRegistry = art != null ? art.GetPortrait(speciesId) : null;
            if (fromRegistry != null) return fromRegistry;

            var resolver = PortraitResolver;
            return resolver != null ? resolver(speciesId) : null;
        }

        /// <summary>Move definition, or null when the move registry has not landed.</summary>
        public static MoveData MoveOf(string moveId)
        {
            var registry = Moves;
            if (registry != null && !string.IsNullOrEmpty(moveId) && registry.TryGet(moveId, out var move)) return move;
            return null;
        }

        /// <summary>
        /// Move display name that survives a missing registry — the move id is at least
        /// diagnostic, and readable ids like "thunder_wave" tidy up acceptably.
        /// </summary>
        public static string MoveName(string moveId)
        {
            var move = MoveOf(moveId);
            if (move != null) return move.DisplayName;
            if (string.IsNullOrEmpty(moveId)) return "—";
            return Titleise(moveId.Replace('_', ' '));
        }

        /// <summary>
        /// Whether the player has encountered a species. Iterates rather than calling
        /// Contains because the contract exposes <see cref="IReadOnlyCollection{T}"/>, which
        /// has no membership test, and a LINQ Contains would box an enumerator per call.
        /// </summary>
        public static bool HasSeen(int speciesId)
        {
            var seen = Profile?.SeenSpecies;
            if (seen == null) return false;
            foreach (var id in seen)
            {
                if (id == speciesId) return true;
            }
            return false;
        }

        /// <summary>Whether the player has caught a species. Same reasoning as <see cref="HasSeen"/>.</summary>
        public static bool HasCaught(int speciesId)
        {
            var caught = Profile?.CaughtSpecies;
            if (caught == null) return false;
            foreach (var id in caught)
            {
                if (id == speciesId) return true;
            }
            return false;
        }

        /// <summary>Finds a party member by instance id, then by index. Either may be all the caller has.</summary>
        public static CreatureInstance PartyMember(string instanceId, int partyIndex)
        {
            var party = Profile?.Party;
            if (party == null) return null;

            if (!string.IsNullOrEmpty(instanceId))
            {
                for (var i = 0; i < party.Count; i++)
                {
                    if (party[i] != null && party[i].InstanceId == instanceId) return party[i];
                }
            }
            if (partyIndex >= 0 && partyIndex < party.Count) return party[partyIndex];
            return null;
        }

        /// <summary>Title-cases a lowercase identifier for display.</summary>
        public static string Titleise(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var chars = value.ToCharArray();
            var atWordStart = true;
            for (var i = 0; i < chars.Length; i++)
            {
                if (char.IsWhiteSpace(chars[i])) { atWordStart = true; continue; }
                if (atWordStart) { chars[i] = char.ToUpperInvariant(chars[i]); atWordStart = false; }
            }
            return new string(chars);
        }
    }
}
