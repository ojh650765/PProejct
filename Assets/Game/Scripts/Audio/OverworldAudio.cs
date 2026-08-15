using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// Overworld one-shots, and specifically the footstep logic.
    ///
    /// Footsteps are the sound the player hears more than any other, so two things get
    /// deliberate attention here: the variant chosen is never the one just played (a
    /// naive random pick repeats about a quarter of the time and the ear catches it
    /// immediately), and every step is nudged in pitch and level by a few percent. Four
    /// recorded variants plus that jitter is the difference between walking and a
    /// machine gun.
    ///
    /// The overworld worker calls <see cref="PlayFootstep"/> from its own animation or
    /// distance-travelled logic; this class deliberately does not own a step timer,
    /// because the character controller knows the gait and this does not.
    /// </summary>
    [AddComponentMenu("Poke Lab/Audio/Overworld Audio")]
    public sealed class OverworldAudio : MonoBehaviour
    {
        [SerializeField, Range(0f, 1f)] private float footstepVolume = 0.75f;
        [SerializeField, Range(0f, 0.3f)] private float pitchJitter = 0.07f;
        [SerializeField, Range(0f, 0.5f)] private float volumeJitter = 0.12f;

        [Tooltip("Play footsteps as 3D sources at the given transform rather than flat. " +
                 "Off is usually right for the player, on for NPCs.")]
        [SerializeField] private bool spatialFootsteps;

        private AudioDirector _audio;
        private readonly int[] _lastVariant = new int[5];

        private void Awake()
        {
            for (int i = 0; i < _lastVariant.Length; i++) _lastVariant[i] = -1;
            ServiceHub.Register(this);
        }

        private bool Ready()
        {
            if (_audio == null) AudioDirector.TryResolve(out _audio);
            return _audio != null;
        }

        /// <summary>One footstep. Pass a world position only when <c>spatialFootsteps</c> is on.</summary>
        public void PlayFootstep(FootstepSurface surface, Vector3? worldPosition = null,
                                 float volumeScale = 1f)
        {
            if (!Ready()) return;

            int index = (int)surface;
            int variant = NextVariant(index);
            string clip = AudioIds.Footstep(surface, variant);

            float pitch = 1f + Random.Range(-pitchJitter, pitchJitter);
            float volume = footstepVolume * volumeScale *
                           (1f + Random.Range(-volumeJitter, volumeJitter));

            if (spatialFootsteps && worldPosition.HasValue)
                _audio.PlaySfxAt(clip, worldPosition.Value, volume, pitch);
            else
                _audio.PlaySfx(clip, volume, pitch);
        }

        /// <summary>A variant that is not the one played last for this surface.</summary>
        private int NextVariant(int surfaceIndex)
        {
            int variants = AudioIds.FootstepVariants;
            if (variants <= 1) return 0;
            int previous = _lastVariant[surfaceIndex];
            int pick = Random.Range(0, variants);
            if (pick == previous) pick = (pick + 1 + Random.Range(0, variants - 1)) % variants;
            _lastVariant[surfaceIndex] = pick;
            return pick;
        }

        /// <summary>Brushing through tall grass. Also the tell that an encounter may fire.</summary>
        public void PlayGrassRustle(Vector3? worldPosition = null, float volumeScale = 1f)
        {
            if (!Ready()) return;
            var clip = AudioIds.GrassRustle[Random.Range(0, AudioIds.GrassRustle.Length)];
            float pitch = 1f + Random.Range(-0.09f, 0.09f);
            if (worldPosition.HasValue) _audio.PlaySfxAt(clip, worldPosition.Value, 0.8f * volumeScale, pitch);
            else _audio.PlaySfx(clip, 0.8f * volumeScale, pitch);
        }

        public void PlayLedgeHop() { if (Ready()) _audio.PlaySfx(AudioIds.OverworldLedgeHop); }

        public void PlayDoorOpen(Vector3? at = null) => PlayPositional(AudioIds.OverworldDoorOpen, at, 0.9f);
        public void PlayDoorClose(Vector3? at = null) => PlayPositional(AudioIds.OverworldDoorClose, at, 0.9f);
        public void PlayItemPickup() { if (Ready()) _audio.PlaySfx(AudioIds.OverworldItemPickup); }
        public void PlayHeal() { if (Ready()) _audio.PlaySfx(AudioIds.OverworldHeal); }

        private void PlayPositional(string clip, Vector3? at, float volume)
        {
            if (!Ready()) return;
            if (at.HasValue) _audio.PlaySfxAt(clip, at.Value, volume);
            else _audio.PlaySfx(clip, volume);
        }

        /// <summary>
        /// Best-effort surface classification from a physics hit, so callers that have not
        /// tagged their ground can still get something sensible. Prefer an explicit
        /// surface from the overworld worker when it has one.
        /// </summary>
        public static FootstepSurface ClassifySurface(string tagOrMaterialName)
        {
            if (string.IsNullOrEmpty(tagOrMaterialName)) return FootstepSurface.Dirt;
            var s = tagOrMaterialName.ToLowerInvariant();
            if (s.Contains("grass") || s.Contains("foliage")) return FootstepSurface.Grass;
            if (s.Contains("stone") || s.Contains("rock") || s.Contains("cave") ||
                s.Contains("gravel") || s.Contains("pav")) return FootstepSurface.Stone;
            if (s.Contains("wood") || s.Contains("plank") || s.Contains("deck") ||
                s.Contains("bridge")) return FootstepSurface.Wood;
            if (s.Contains("water") || s.Contains("shallow") || s.Contains("puddle"))
                return FootstepSurface.Water;
            return FootstepSurface.Dirt;
        }
    }
}
