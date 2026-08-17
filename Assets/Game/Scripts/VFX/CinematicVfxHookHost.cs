using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// The bridge between the choreography's vocabulary and the catalogue's.
    ///
    /// The cinematics worker speaks in stage directions — <c>ball_burst</c>,
    /// <c>landing_dust</c>, <c>impact_critical</c> — because that is what a beat is. The VFX
    /// catalogue speaks in recipes — <c>capture.flash</c>, <c>move.ground.impact</c>,
    /// <c>hit.critical</c> — because that is what a particle system is. Neither side should
    /// learn the other's language: the choreography must stay playable against a future
    /// hand-authored effect set, and the catalogue must stay usable from the raw event
    /// stream. So this adapter owns the translation table, registers itself as the
    /// <see cref="ICinematicVfxHook"/> the choreography probes for, and forwards everything
    /// to <see cref="BattleVfxPresenter"/> through <see cref="VfxApi"/>.
    ///
    /// Keys it has never heard of — an engine-supplied <c>MoveExecutedEvent.VfxKey</c>, a
    /// weather id — fall through to the catalogue's own resolution chain, which tries the
    /// key bare, phase-qualified, and finally by element. A key nothing recognises is
    /// silence, never an exception: effects are advisory by contract.
    /// </summary>
    [AddComponentMenu("PokeLab/Cinematic VFX Hook Host")]
    public sealed class CinematicVfxHookHost : MonoBehaviour, ICinematicVfxHook
    {
        /// <summary>
        /// Stage direction to recipe, one line per key in <c>CinematicVfxKeys</c> (plus the
        /// weather ids PlayWeather composes). Each mapping picks the recipe whose motion
        /// matches the beat, not merely whose name is closest:
        ///   * the ball's open/burst/click/break-out are all flashes of the capture family,
        ///     because they are the same light doing four jobs;
        ///   * landing_dust is the ground impact — the one recipe that throws debris low
        ///     and outward, which is what a landing kicks up;
        ///   * dodge_streak is the flying cast: a horizontal smear of air, exactly the
        ///     read a sidestep needs;
        ///   * faint_collapse, stat_up/down, celebrate_sparkle map onto the state family
        ///     built for those very moments in the event-stream presenter.
        /// </summary>
        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "ball_throw_trail", "move.normal.travel" },
                { "ball_burst", BattleStateVfxLibrary.KeyCaptureFlash },
                { "ball_recall", BattleStateVfxLibrary.KeyCaptureBeam },
                { "ball_absorb", BattleStateVfxLibrary.KeyCaptureBeam },
                { "ball_click", BattleStateVfxLibrary.KeyCaptureSuccess },
                { "ball_break_out", BattleStateVfxLibrary.KeyCaptureFlash },
                { "landing_dust", "move.ground.impact" },
                { "impact_generic", "move.normal.impact" },
                { "impact_critical", BattleStateVfxLibrary.KeyCritical },
                { "impact_super_effective", BattleStateVfxLibrary.KeySuperEffective },
                { "dodge_streak", "move.flying.cast" },
                { "faint_collapse", BattleStateVfxLibrary.KeyFaint },
                { "stat_up", BattleStateVfxLibrary.KeyStatUp },
                { "stat_down", BattleStateVfxLibrary.KeyStatDown },
                { "ability_flare", BattleStateVfxLibrary.KeyShield },
                { "celebrate_sparkle", BattleStateVfxLibrary.KeyLevelUp },

                // Weather onsets, staged on the arena's centre by PlayWeather. The ambient
                // library's beds are the closest thing to weather the catalogue has; fog
                // borrows the waterfall mist because a fog recipe does not exist and mist
                // is what fog looks like when it arrives.
                { "weather_rain", AmbientVfxLibrary.KeyRain },
                { "weather_sandstorm", AmbientVfxLibrary.KeySandstorm },
                { "weather_hail", AmbientVfxLibrary.KeySnow },
                { "weather_sun", AmbientVfxLibrary.KeySunbeamDust },
                { "weather_fog", AmbientVfxLibrary.KeyWaterfallMist },
            };

        private BattleVfxPresenter _presenter;

        private void Awake()
        {
            // The Core-facing registration the choreography probes for. The presenter is
            // resolved lazily: this host and the presenter are both composed at runtime and
            // nothing guarantees their order.
            ServiceHub.Register<ICinematicVfxHook>(this);
        }

        private BattleVfxPresenter Presenter
        {
            get
            {
                if (_presenter == null) _presenter = FindAnyObjectByType<BattleVfxPresenter>();
                return _presenter;
            }
        }

        /// <inheritdoc />
        public void PlayVfx(string vfxKey, Vector3 position, Quaternion rotation)
        {
            if (string.IsNullOrEmpty(vfxKey)) return;

            bool mapped = Map.TryGetValue(vfxKey, out string resolved);
            var handle = VfxApi.Play(mapped ? resolved : vfxKey, position, rotation);
            if (handle != null || mapped) return;

            // An unmapped key the catalogue did not know bare — almost always an engine
            // VfxKey arriving through ContactMoment, i.e. an impact frame. Let the
            // catalogue's own fallback chain (phase-qualified key, then element) serve it.
            VfxApi.PlayMove(vfxKey, ElementType.None, VfxPhase.Impact, position, rotation);
        }

        /// <inheritdoc />
        public GameObject AttachVfx(string vfxKey, Transform parent)
        {
            if (string.IsNullOrEmpty(vfxKey) || parent == null) return null;

            // Attached effects are built detached from the pool on purpose: the caller
            // (ProjectileActor, a ball trail) parents the object and later destroys it,
            // and destroying a pooled instance would corrupt the pool. See BuildDetached.
            var presenter = Presenter;
            if (presenter == null) return null;

            string key = Map.TryGetValue(vfxKey, out string resolved) ? resolved : vfxKey;
            return presenter.BuildDetached(key, parent);
        }
    }
}
