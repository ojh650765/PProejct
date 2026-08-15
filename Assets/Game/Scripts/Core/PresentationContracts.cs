using System;
using UnityEngine;

namespace PokeLab.Core
{
    /// <summary>Mixer routing. Values are stable — the mixer asset binds exposed parameters by name.</summary>
    public enum AudioBus
    {
        Master = 0,
        Music = 1,
        Sfx = 2,
        Ambience = 3,
        Ui = 4,
    }

    /// <summary>
    /// Audio playback, available to every system.
    ///
    /// This lives in Core rather than in the audio assembly because the ownership rules stop
    /// any worker referencing another's assembly — so UI, overworld and cinematics could not
    /// play a sound at all while this interface sat in <c>PokeLab.Audio</c>. Everything resolves
    /// it through <see cref="ServiceHub.TryGet{T}"/> and stays silent when nothing is registered.
    /// </summary>
    public interface IGameAudio
    {
        void PlaySfx(string clipName, float volume = 1f, float pitch = 1f);
        void PlaySfxAt(string clipName, Vector3 worldPosition, float volume = 1f, float pitch = 1f);
        void PlayUi(string clipName, float volume = 1f, float pitch = 1f);

        /// <summary>Starts a looping clip on the given bus. Returns null if the clip is missing.</summary>
        AudioSource PlayLoop(string clipName, AudioBus bus, float volume = 1f, bool spatial = false);
        void StopLoop(AudioSource source, float fadeSeconds = 0.25f);

        /// <summary>Linear 0-1; converted to a logarithmic dB curve before it reaches the mixer.</summary>
        void SetBusVolume(AudioBus bus, float linear01);
        float GetBusVolume(AudioBus bus);

        /// <summary>Dips music for a fixed time, e.g. under the victory fanfare.</summary>
        void DuckMusic(float amount01, float attack, float hold, float release);

        /// <summary>Holds music down until the returned handle is disposed, e.g. for a dialogue run.</summary>
        IDisposable DuckMusicScope(float amount01 = 0.55f, float attack = 0.25f, float release = 0.6f);

        bool HasClip(string clipName);
    }

    /// <summary>
    /// A VFX cue fired from inside a choreographed beat.
    ///
    /// The VFX layer subscribes to the <see cref="BattleEvent"/> stream and plays its own
    /// response independently — that is the point of the seam. This interface exists only for
    /// the handful of moments where an effect must land on a frame the event stream cannot
    /// express: the instant a travelling projectile reaches its target, the frame a capture ball
    /// clicks shut, the peak of a lunge.
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
    /// HUD beats the choreography drives directly: flying the battle HUD in once the creatures
    /// have taken their marks, hiding it for a set piece, dimming it during a capture.
    /// </summary>
    public interface ICinematicHudHook
    {
        /// <summary>Fly the battle HUD in or out over the given duration. Never a hard toggle.</summary>
        void SetHudVisible(bool visible, float duration);

        /// <summary>A named emphasis beat, e.g. "critical", "supereffective", "levelup".</summary>
        void PlayHudBeat(string beatId, float intensity);
    }
}
