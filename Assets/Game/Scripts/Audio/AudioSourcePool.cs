using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Audio;

namespace PokeLab.Audio
{
    /// <summary>
    /// A fixed pool of <see cref="AudioSource"/> components.
    ///
    /// Nothing in this assembly ever instantiates a GameObject to play a sound. The pool
    /// is allocated once at boot; renting scans for an idle source, and when every source
    /// is busy the oldest one is stolen rather than growing the pool, so a runaway caller
    /// costs a dropped tail instead of an unbounded allocation spike mid-battle.
    ///
    /// Looping sources are rented with <see cref="RentReserved"/>, which takes the source
    /// out of the steal pool until it is explicitly released -- an ambience bed must never
    /// be stolen by a footstep.
    /// </summary>
    public sealed class AudioSourcePool
    {
        private struct Slot
        {
            public AudioSource Source;
            public float StartedAt;
            public bool Reserved;
        }

        private readonly Slot[] _slots;
        private readonly Transform _root;

        public int Capacity => _slots.Length;

        public AudioSourcePool(Transform parent, string label, int capacity,
                               AudioMixerGroup group, bool spatial = false)
        {
            _slots = new Slot[Mathf.Max(1, capacity)];
            var host = new GameObject(label);
            host.transform.SetParent(parent, false);
            _root = host.transform;

            for (int i = 0; i < _slots.Length; i++)
            {
                var go = new GameObject($"{label}_{i:00}");
                go.transform.SetParent(_root, false);
                var src = go.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.loop = false;
                src.outputAudioMixerGroup = group;
                src.spatialBlend = spatial ? 1f : 0f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.minDistance = 2f;
                src.maxDistance = 45f;
                src.dopplerLevel = 0f;
                _slots[i] = new Slot { Source = src, StartedAt = -999f, Reserved = false };
            }
        }

        /// <summary>An idle source for a one-shot. Never returns null; steals if saturated.</summary>
        public AudioSource Rent()
        {
            int oldest = -1;
            float oldestTime = float.MaxValue;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Reserved) continue;
                var src = _slots[i].Source;
                if (src == null) continue;
                if (!src.isPlaying)
                {
                    _slots[i].StartedAt = Time.unscaledTime;
                    Reset(src);
                    return src;
                }
                if (_slots[i].StartedAt < oldestTime)
                {
                    oldestTime = _slots[i].StartedAt;
                    oldest = i;
                }
            }

            if (oldest < 0) return null;
            _slots[oldest].StartedAt = Time.unscaledTime;
            var stolen = _slots[oldest].Source;
            stolen.Stop();
            Reset(stolen);
            return stolen;
        }

        /// <summary>A source held until <see cref="Release"/>. For loops and beds.</summary>
        public AudioSource RentReserved()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Reserved) continue;
                var src = _slots[i].Source;
                if (src == null || src.isPlaying) continue;
                _slots[i].Reserved = true;
                _slots[i].StartedAt = Time.unscaledTime;
                Reset(src);
                return src;
            }
            return null;
        }

        public void Release(AudioSource source)
        {
            if (source == null) return;
            for (int i = 0; i < _slots.Length; i++)
            {
                if (!ReferenceEquals(_slots[i].Source, source)) continue;
                _slots[i].Reserved = false;
                source.Stop();
                Reset(source);
                return;
            }
        }

        private static void Reset(AudioSource src)
        {
            src.clip = null;
            src.loop = false;
            src.volume = 1f;
            src.pitch = 1f;
            src.panStereo = 0f;
            src.spatialBlend = 0f;
            src.time = 0f;
            src.transform.localPosition = Vector3.zero;
        }

        public void StopAll()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i].Source != null) _slots[i].Source.Stop();
                _slots[i].Reserved = false;
            }
        }

        /// <summary>How many sources are currently occupied; surfaced by the debug overlay.</summary>
        public int ActiveCount()
        {
            int n = 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                var s = _slots[i].Source;
                if (s != null && (s.isPlaying || _slots[i].Reserved)) n++;
            }
            return n;
        }
    }
}
