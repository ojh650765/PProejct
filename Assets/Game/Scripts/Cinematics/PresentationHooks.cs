using System;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// A VFX cue fired from inside a choreographed beat.
    ///
    /// The VFX worker subscribes to the same <see cref="BattleEvent"/> stream we do and
    /// plays its own response independently — that is the whole point of the seam. This
    /// interface exists only for the handful of moments where the effect must land at a
    /// frame the event stream cannot express: the instant a travelling projectile reaches
    /// the target, the frame a capture ball clicks shut, the peak of a lunge.
    ///
    /// Every call site treats this as optional and no-ops when nothing is registered.
    /// </summary>
    public interface ICinematicVfxHook
    {
        /// <summary>Spawn a one-shot effect identified by the engine's VfxKey.</summary>
        void PlayVfx(string vfxKey, Vector3 position, Quaternion rotation);

        /// <summary>Attach a persistent effect to a transform, returning a handle to stop it.</summary>
        GameObject AttachVfx(string vfxKey, Transform parent);
    }

    /// <summary>Frame-accurate audio cues from inside a beat. See <see cref="ICinematicVfxHook"/>.</summary>
    public interface ICinematicAudioHook
    {
        void PlayCue(string cueId, Vector3 position);

        /// <summary>The creature's species cry, used at send-out and on the victory beat.</summary>
        void PlayCry(int speciesId, Vector3 position);
    }

    /// <summary>
    /// HUD beats the choreography drives directly: flying the battle HUD in when the
    /// creatures have taken their marks, hiding it for a set piece, and dimming it during
    /// the capture sequence.
    /// </summary>
    public interface ICinematicHudHook
    {
        /// <summary>Fly the battle HUD in or out over the given duration. Never a hard toggle.</summary>
        void SetHudVisible(bool visible, float duration);

        /// <summary>A named emphasis beat, e.g. "critical", "supereffective", "levelup".</summary>
        void PlayHudBeat(string beatId, float intensity);
    }

    /// <summary>
    /// Resolves the optional presentation hooks and swallows their absence.
    ///
    /// Two resolution paths are tried, because of an assembly-graph constraint: no worker
    /// assembly references <c>PokeLab.Cinematics</c>, so the VFX/UI/Audio workers cannot
    /// name the interfaces above. Until the integrator promotes them into <c>Core</c>,
    /// those workers can register a plain delegate whose type is expressible from any
    /// assembly (BCL + Core types only) and we will find it. The delegate shapes are
    /// deliberately distinct from each other so the three cannot collide in ServiceHub.
    ///
    /// Resolution is cached but re-checked while nothing is registered, so a hook that
    /// comes online mid-session is picked up rather than being missed for the whole run.
    /// </summary>
    public static class CinematicHooks
    {
        /// <summary>VFX fallback shape: (vfxKey, worldPosition, worldRotation).</summary>
        public delegate void VfxDelegate(string vfxKey, Vector3 position, Quaternion rotation);

        /// <summary>Audio fallback shape: (cueId, worldPosition).</summary>
        public delegate void AudioDelegate(string cueId, Vector3 position);

        /// <summary>HUD fallback shape: (beatId, intensity).</summary>
        public delegate void HudDelegate(string beatId, float intensity);

        private static ICinematicVfxHook s_vfx;
        private static ICinematicAudioHook s_audio;
        private static ICinematicHudHook s_hud;

        private static Action<string, Vector3, Quaternion> s_vfxFallback;
        private static Action<string, Vector3> s_audioFallback;
        private static Action<string, float> s_hudFallback;

        // Re-probing ServiceHub every call would be a dictionary lookup per VFX spawn during
        // a multi-hit move. Probe at most a few times a second while unresolved instead.
        private const float RetryInterval = 0.5f;
        private static float s_nextProbe;

        private static void Probe()
        {
            if (Time.unscaledTime < s_nextProbe) return;
            s_nextProbe = Time.unscaledTime + RetryInterval;

            if (s_vfx == null) ServiceHub.TryGet(out s_vfx);
            if (s_audio == null) ServiceHub.TryGet(out s_audio);
            if (s_hud == null) ServiceHub.TryGet(out s_hud);

            if (s_vfx == null && s_vfxFallback == null) ServiceHub.TryGet(out s_vfxFallback);
            if (s_audio == null && s_audioFallback == null) ServiceHub.TryGet(out s_audioFallback);
            if (s_hud == null && s_hudFallback == null) ServiceHub.TryGet(out s_hudFallback);
        }

        /// <summary>Fire a one-shot effect. Silently ignored when no VFX layer is present.</summary>
        public static void Vfx(string vfxKey, Vector3 position, Quaternion rotation)
        {
            if (string.IsNullOrEmpty(vfxKey)) return;
            Probe();
            if (s_vfx != null) { s_vfx.PlayVfx(vfxKey, position, rotation); return; }
            s_vfxFallback?.Invoke(vfxKey, position, rotation);
        }

        /// <summary>Fire a one-shot effect facing away from <paramref name="forward"/>'s origin.</summary>
        public static void Vfx(string vfxKey, Vector3 position, Vector3 forward)
            => Vfx(vfxKey, position, forward.sqrMagnitude > 1e-6f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity);

        /// <summary>Attach a looping effect. Returns null when no VFX layer is present.</summary>
        public static GameObject AttachVfx(string vfxKey, Transform parent)
        {
            if (string.IsNullOrEmpty(vfxKey) || parent == null) return null;
            Probe();
            return s_vfx?.AttachVfx(vfxKey, parent);
        }

        /// <summary>Play a positional audio cue.</summary>
        public static void Audio(string cueId, Vector3 position)
        {
            if (string.IsNullOrEmpty(cueId)) return;
            Probe();
            if (s_audio != null) { s_audio.PlayCue(cueId, position); return; }
            s_audioFallback?.Invoke(cueId, position);
        }

        /// <summary>Play a species cry. Falls back to a keyed cue when only the delegate hook exists.</summary>
        public static void Cry(int speciesId, Vector3 position)
        {
            Probe();
            if (s_audio != null) { s_audio.PlayCry(speciesId, position); return; }
            s_audioFallback?.Invoke("cry_" + speciesId, position);
        }

        /// <summary>Fly the battle HUD in or out. Never a hard toggle — duration is honoured by the UI worker.</summary>
        public static void HudVisible(bool visible, float duration)
        {
            Probe();
            if (s_hud != null) { s_hud.SetHudVisible(visible, duration); return; }
            // The delegate form carries the duration in the intensity slot; the beat id
            // disambiguates direction. Documented in the integration section of the report.
            s_hudFallback?.Invoke(visible ? "hud_in" : "hud_out", duration);
        }

        /// <summary>A named HUD emphasis beat, e.g. "critical" or "supereffective".</summary>
        public static void HudBeat(string beatId, float intensity = 1f)
        {
            if (string.IsNullOrEmpty(beatId)) return;
            Probe();
            if (s_hud != null) { s_hud.PlayHudBeat(beatId, intensity); return; }
            s_hudFallback?.Invoke(beatId, intensity);
        }

        /// <summary>Drops cached resolutions. Call alongside <see cref="ServiceHub.Reset"/>.</summary>
        public static void Reset()
        {
            s_vfx = null; s_audio = null; s_hud = null;
            s_vfxFallback = null; s_audioFallback = null; s_hudFallback = null;
            s_nextProbe = 0f;
        }
    }

    /// <summary>
    /// Canonical VFX key strings the choreography emits. Listed here so the VFX worker has
    /// one place to read them from rather than grepping the presenter.
    /// Engine-supplied keys (<see cref="MoveExecutedEvent.VfxKey"/>) always win when present.
    /// </summary>
    public static class CinematicVfxKeys
    {
        public const string BallThrowTrail = "ball_throw_trail";
        public const string BallBurst = "ball_burst";
        public const string BallRecall = "ball_recall";
        public const string BallAbsorb = "ball_absorb";
        public const string BallClick = "ball_click";
        public const string BallBreakOut = "ball_break_out";
        public const string LandingDust = "landing_dust";
        public const string ImpactGeneric = "impact_generic";
        public const string ImpactCritical = "impact_critical";
        public const string ImpactSuperEffective = "impact_super_effective";
        public const string DodgeStreak = "dodge_streak";
        public const string FaintCollapse = "faint_collapse";
        public const string StatUp = "stat_up";
        public const string StatDown = "stat_down";
        public const string AbilityFlare = "ability_flare";
        public const string CelebrateSparkle = "celebrate_sparkle";
    }

    /// <summary>Canonical audio cue ids the choreography emits. Same rationale as <see cref="CinematicVfxKeys"/>.</summary>
    public static class CinematicAudioCues
    {
        public const string BallThrow = "sfx_ball_throw";
        public const string BallOpen = "sfx_ball_open";
        public const string BallShake = "sfx_ball_shake";
        public const string BallClick = "sfx_ball_click";
        public const string BallBreakOut = "sfx_ball_break_out";
        public const string CreatureLand = "sfx_creature_land";
        public const string Whoosh = "sfx_whoosh";
        public const string HitNeutral = "sfx_hit_neutral";
        public const string HitSuper = "sfx_hit_super";
        public const string HitWeak = "sfx_hit_weak";
        public const string Critical = "sfx_critical";
        public const string Faint = "sfx_faint";
        public const string Victory = "sfx_victory";
        public const string TransitionWhoosh = "sfx_transition_whoosh";
        public const string EncounterSting = "sfx_encounter_sting";
    }
}
