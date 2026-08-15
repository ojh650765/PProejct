using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Audio
{
    /// <summary>
    /// Name-to-clip lookup, authored as an asset so nothing in this assembly loads by
    /// path at runtime. Build it from <c>Assets/Game/Audio/audio_manifest.json</c> with
    /// <c>Tools &gt; Poke Lab &gt; Audio &gt; Rebuild Catalogue</c>; the manifest carries
    /// the loop flag, duration and default bus for every clip.
    /// </summary>
    [CreateAssetMenu(menuName = "Poke Lab/Audio/Clip Catalogue", fileName = "AudioClipCatalog")]
    public sealed class AudioClipCatalog : ScriptableObject
    {
        [Serializable]
        public struct Entry
        {
            public string Name;
            public AudioClip Clip;
            public AudioBus Bus;
            public bool Loop;
            [Range(0f, 2f)] public float Gain;
        }

        [SerializeField] private List<Entry> entries = new List<Entry>();

        private Dictionary<string, Entry> _index;

        public IReadOnlyList<Entry> Entries => entries;

        private void OnEnable() => _index = null;

        private Dictionary<string, Entry> Index
        {
            get
            {
                if (_index != null) return _index;
                _index = new Dictionary<string, Entry>(entries.Count, StringComparer.Ordinal);
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    if (string.IsNullOrEmpty(e.Name) || e.Clip == null) continue;
                    _index[e.Name] = e;
                }
                return _index;
            }
        }

        public bool TryGet(string name, out Entry entry)
        {
            if (string.IsNullOrEmpty(name))
            {
                entry = default;
                return false;
            }
            return Index.TryGetValue(name, out entry);
        }

        public AudioClip GetClip(string name) =>
            TryGet(name, out var e) ? e.Clip : null;

        /// <summary>Editor/tooling entry point; the catalogue builder replaces the list wholesale.</summary>
        public void SetEntries(List<Entry> newEntries)
        {
            entries = newEntries ?? new List<Entry>();
            _index = null;
        }

        /// <summary>Names present in the catalogue but with no clip assigned. Used by the validator.</summary>
        public List<string> FindBrokenEntries()
        {
            var broken = new List<string>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Clip == null)
                    broken.Add(string.IsNullOrEmpty(entries[i].Name) ? $"<unnamed #{i}>" : entries[i].Name);
            }
            return broken;
        }
    }
}
