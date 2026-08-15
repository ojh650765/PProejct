using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// The editor-free entry point other systems call.
    ///
    /// The cinematics worker needs to fire effects from a Timeline or a transition
    /// coroutine without holding a reference to a scene object it does not own, so
    /// everything here is static, null-safe and silent when the VFX layer is not
    /// present. Every method is a no-op before BattleVfxPresenter has bound itself,
    /// which is the correct behaviour during partial integration: a missing effect
    /// is a cosmetic gap, never a broken transition.
    ///
    /// Add "PokeLab.Vfx" to the calling assembly's asmdef references to use this.
    /// If that is not wanted, the presenter also registers itself on ServiceHub as
    /// IBattleEventListener, which needs no reference at all.
    /// </summary>
    public static class VfxApi
    {
        private static BattleVfxPresenter presenter;
        private static LightingDirector director;

        public static bool IsReady => presenter != null;

        internal static void Bind(BattleVfxPresenter instance) => presenter = instance;

        internal static void Unbind(BattleVfxPresenter instance)
        {
            if (presenter == instance) presenter = null;
        }

        /// <summary>
        /// Resolved lazily rather than on a bind callback, because the director and
        /// the presenter are separate components the integrator may place in either
        /// order, or in different scenes.
        /// </summary>
        private static LightingDirector Director
        {
            get
            {
                if (director == null)
                    director = Object.FindFirstObjectByType<LightingDirector>();
                return director;
            }
        }

        // ---------------------------------------------------------------------
        // Effects
        // ---------------------------------------------------------------------

        /// <summary>Plays a catalogue key at a world position. Returns null if unavailable.</summary>
        public static VfxHandle Play(string key, Vector3 position, float scale = 1f) =>
            presenter != null ? presenter.Play(key, position, Quaternion.identity, scale) : null;

        public static VfxHandle Play(string key, Vector3 position, Quaternion rotation,
                                     float scale = 1f, Transform parent = null) =>
            presenter != null ? presenter.Play(key, position, rotation, scale, parent) : null;

        /// <summary>Plays one phase of an elemental move, with the standard fallback chain.</summary>
        public static VfxHandle PlayMove(string vfxKey, ElementType type, VfxPhase phase,
                                         Vector3 position, Quaternion rotation, float scale = 1f) =>
            presenter != null
                ? presenter.PlayResolved(vfxKey, type, phase, position, rotation, scale)
                : null;

        /// <summary>Stops a persistent effect such as a status aura or an ambient emitter.</summary>
        public static void Stop(VfxHandle handle, bool immediate = false) =>
            presenter?.StopEffect(handle, immediate);

        /// <summary>Tells the presenter which creature view belongs to which side.</summary>
        public static void BindCreatureView(BattleSide side, ICreatureView view) =>
            presenter?.BindView(side, view);

        public static void ClearCreatureViews() => presenter?.ClearViews();

        // ---------------------------------------------------------------------
        // Grading. The transition into the battle grade is part of the encounter
        // cinematic, so the cinematics worker drives it directly rather than waiting
        // for the mode change to propagate.
        // ---------------------------------------------------------------------

        /// <summary>0 for the overworld look, 1 for the full dramatic battle grade.</summary>
        public static void SetBattleIntensity(float weight) => Director?.SetBattleIntensity(weight);

        /// <summary>0 outdoors, 1 for the cave interior look.</summary>
        public static void SetCaveWeight(float weight) => Director?.SetCaveWeight(weight);

        /// <summary>Forces the day cycle to a position. For scripted scenes.</summary>
        public static void SetTimeOfDayProgress(float normalised) => Director?.SetProgress(normalised);

        /// <summary>The blended look currently on screen. Useful for matching UI colours.</summary>
        public static bool TryGetCurrentGrade(out GradeDefinition grade)
        {
            var d = Director;
            if (d == null) { grade = default; return false; }
            grade = d.CurrentGrade;
            return true;
        }

        /// <summary>The canonical colour for an element type, shared with UI and audio.</summary>
        public static Color ColorFor(ElementType type) => MoveVfxLibrary.PrimaryColor(type);
    }
}
