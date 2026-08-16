using PokeLab.Cinematics;
using PokeLab.Core;
using PokeLab.Overworld;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Shows the Pokémon carrying the player across water, and the wake it makes.
    ///
    /// Surfing that is only a change of speed reads as the player walking on the lake. The
    /// creature under their feet is what makes it legible as riding — and it is also the
    /// only place in exploration where the party is visible at all, which is worth having.
    ///
    /// Lives in PokeLab.Boot because it is the one assembly that can see both halves:
    /// <see cref="CreatureBillboard"/> and the creature sprite library are PokeLab.Cinematics,
    /// the player and the traversal state are PokeLab.Overworld, and those two assemblies
    /// deliberately cannot see each other. Composition belongs here rather than in a new
    /// reference between them.
    ///
    /// It listens to the traversal event rather than to the water plane, so any number of
    /// lakes and rivers get this for free and none of them needs to know it exists.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SurfMount : MonoBehaviour
    {
        [Header("Mount")]
        [Tooltip("Metres below the player's pivot the ride sprite sits. Slightly under, so " +
                 "the player reads as standing on its back rather than beside it.")]
        [SerializeField] private float _mountDrop = 0.35f;

        [Tooltip("Metres the ride is drawn at. Independent of the species' canonical height, " +
                 "which runs from a 0.3 m Caterpie to a 6.5 m Gyarados — a literal Gyarados " +
                 "would fill the screen and a literal Poliwag would vanish under the player.")]
        [SerializeField] private float _mountHeight = 1.6f;

        [Tooltip("Seconds of bob, so the ride is alive while the player is standing still.")]
        [SerializeField] private float _bobPeriod = 2.6f;
        [SerializeField] private float _bobAmplitude = 0.06f;

        [Header("Wake")]
        [Tooltip("Particles per metre travelled. Rate is per distance rather than per second " +
                 "so a stationary rider does not keep throwing spray.")]
        [SerializeField] private float _spraysPerMetre = 6f;

        [SerializeField] private Color _sprayColour = new Color(0.82f, 0.93f, 1f, 0.9f);

        private PlayerLocomotion _player;
        private CreatureBillboard _billboard;
        private ParticleSystem _spray;
        private ParticleSystem.EmissionModule _sprayEmission;
        private bool _riding;
        private float _bobPhase;

        private void Awake()
        {
            _player = FindFirstObjectByType<PlayerLocomotion>();
            if (_player == null)
            {
                Debug.LogWarning("[Surf] No player in this scene, so nothing can be ridden.", this);
                enabled = false;
                return;
            }

            BuildMount();
            BuildSpray();
            SetRiding(false);
        }

        private void OnEnable() => OverworldEvents.TraversalChanged += OnTraversalChanged;
        private void OnDisable() => OverworldEvents.TraversalChanged -= OnTraversalChanged;

        private void BuildMount()
        {
            var go = new GameObject("SurfMount");
            go.transform.SetParent(_player.transform, false);
            go.transform.localPosition = new Vector3(0f, -_mountDrop, 0f);
            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>();
            _billboard = go.AddComponent<CreatureBillboard>();
        }

        private void BuildSpray()
        {
            var go = new GameObject("SurfSpray");
            go.transform.SetParent(_player.transform, false);
            // At the waterline, not at the pivot: spray that starts at the player's waist
            // reads as rain rather than as a wake.
            go.transform.localPosition = new Vector3(0f, -_mountDrop + 0.05f, 0f);

            _spray = go.AddComponent<ParticleSystem>();
            var main = _spray.main;
            main.startLifetime = 0.5f;
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.05f, 0.16f);
            main.startColor = _sprayColour;
            main.gravityModifier = 1.4f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 160;
            main.playOnAwake = false;

            var shape = _spray.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 42f;
            shape.radius = 0.35f;
            // Thrown up and outward from the hull, which is where a wake comes from.
            shape.rotation = new Vector3(-90f, 0f, 0f);

            _sprayEmission = _spray.emission;
            _sprayEmission.rateOverTime = 0f;
            _sprayEmission.rateOverDistance = 0f;

            var renderer = _spray.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            // Left on whatever the pipeline gives a bare ParticleSystem rather than reaching
            // for the VFX library, which lives in an assembly this one cannot see either.
            renderer.alignment = ParticleSystemRenderSpace.View;
        }

        private void OnTraversalChanged(TraversalState state) => SetRiding(state == TraversalState.Water);

        private void SetRiding(bool riding)
        {
            _riding = riding;

            if (_billboard != null)
            {
                if (riding) BindMountSpecies();
                _billboard.SetVisible(riding);
            }

            if (_spray == null) return;
            _sprayEmission.rateOverDistance = riding ? _spraysPerMetre : 0f;
            if (riding) _spray.Play();
            else _spray.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }

        /// <summary>
        /// Binds whichever party member is doing the carrying.
        ///
        /// Resolved at mount time rather than cached, because the party changes between
        /// swims and the creature under the player has to be one they actually have.
        /// </summary>
        private void BindMountSpecies()
        {
            var mount = SurfCapability.Mount();
            if (mount == null)
            {
                // Reachable: traversal can be forced by a cutscene or a debug console with no
                // swimmer in the party. Better a rider with no visible mount than a null.
                Debug.LogWarning("[Surf] Riding with no water-type in the party; drawing no mount.", this);
                _billboard.SetVisible(false);
                return;
            }
            _billboard.Bind(mount.SpeciesId, _mountHeight, null);
        }

        private void LateUpdate()
        {
            if (!_riding || _billboard == null) return;

            _bobPhase += Time.deltaTime;
            var bob = Mathf.Sin(_bobPhase * Mathf.PI * 2f / Mathf.Max(0.01f, _bobPeriod)) * _bobAmplitude;
            _billboard.transform.localPosition = new Vector3(0f, -_mountDrop + bob, 0f);
        }
    }
}
