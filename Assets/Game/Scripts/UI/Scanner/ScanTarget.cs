using UnityEngine;
using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>What the scanner is pointed at.</summary>
    public enum ScanTargetKind
    {
        /// <summary>Tall grass or another encounter volume: species is a likely spawn, not a certainty.</summary>
        EncounterZone = 0,
        /// <summary>A specific wild creature standing in the world.</summary>
        WildCreature = 1,
        /// <summary>A trainer, whose lead creature is what gets scanned.</summary>
        Trainer = 2,
    }

    /// <summary>
    /// Implemented by anything in the world the handheld scanner can lock onto.
    ///
    /// It lives in the UI assembly rather than Core because the scanner is the only consumer
    /// and Core is frozen. The overworld worker never has to reference this: the integrator
    /// drops the concrete <see cref="ScanTargetTag"/> onto grass volumes and trainer prefabs,
    /// which keeps both assemblies independent. If world objects should carry their own
    /// scan data natively, that is a Core interface request, not a change either worker can
    /// make alone.
    /// </summary>
    public interface IPokeLabScanTarget
    {
        ScanTargetKind Kind { get; }
        /// <summary>Species the scanner should report. Zero means "unknown, show an unresolved scan".</summary>
        int SpeciesId { get; }
        /// <summary>Optional override name, e.g. a trainer's name instead of their lead's species.</summary>
        string DisplayName { get; }
        /// <summary>World point the reticle should lock to.</summary>
        Transform LockAnchor { get; }
    }

    /// <summary>
    /// The concrete tag the integrator attaches to scannable world objects.
    ///
    /// Deliberately dumb — a species id and a kind. Everything else the scanner needs comes
    /// from the oracle and the species registry, so this never has to be kept in sync with
    /// gameplay data.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ScanTargetTag : MonoBehaviour, IPokeLabScanTarget
    {
        [SerializeField] private ScanTargetKind _kind = ScanTargetKind.EncounterZone;
        [Tooltip("Species the scanner reports. For an encounter zone, the most common spawn.")]
        [SerializeField] private int _speciesId;
        [Tooltip("Optional. Overrides the species name in the scanner header.")]
        [SerializeField] private string _displayName;
        [Tooltip("Optional. Where the reticle locks. Defaults to this transform.")]
        [SerializeField] private Transform _lockAnchor;

        public ScanTargetKind Kind => _kind;
        public int SpeciesId => _speciesId;
        public string DisplayName => _displayName;
        public Transform LockAnchor => _lockAnchor != null ? _lockAnchor : transform;

        /// <summary>Retargets at runtime — an encounter zone may roll a different species per approach.</summary>
        public void SetSpecies(int speciesId, string displayName = null)
        {
            _speciesId = speciesId;
            _displayName = displayName;
        }
    }
}
