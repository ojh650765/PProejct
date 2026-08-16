using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>
    /// Dissolves whatever stands between the camera and the player, instead of moving the
    /// camera.
    ///
    /// The rig's boom is a fixed length on purpose — a spring arm that shortens behind a
    /// wall is a third-person convention, and it made the framing lurch between 1.4 m and
    /// 16 m while the world was read from a different distance every second. The cost of
    /// fixing the boom is that a building can now stand in front of the player, so the
    /// building is what gives way.
    ///
    /// It fades by dithering, not by transparency. `_FadeAmount` clips pixels on a
    /// screen-space noise threshold in PokeLab/PropGroundBlend, which costs one clip, needs
    /// no transparent queue and no sorting against everything behind it, and does not force
    /// a per-renderer material instance out of the batch. It also reads as a dissolve rather
    /// than a ghost, which matters: a smoothly translucent wall looks like something the
    /// player might be able to walk through, and a stippled one does not.
    ///
    /// The fade is driven through a MaterialPropertyBlock, so nothing is written to a shared
    /// material and a fade left behind by a crash cannot persist into the next session.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraOccluderFade : MonoBehaviour
    {
        private static readonly int FadeAmount = Shader.PropertyToID("_FadeAmount");

        [Header("Probe")]
        [Tooltip("What the player is followed at. Defaults to the rig's own follow target.")]
        [SerializeField] private Transform _target;

        [Tooltip("Cast radius. Wide enough that a pillar clipping the frame edge is caught, " +
                 "since a wall you can see past on one side is the annoying case, not the " +
                 "one that fully blocks.")]
        [SerializeField] private float _probeRadius = 0.55f;

        [SerializeField] private LayerMask _mask = ~0;

        [Header("Fade")]
        [Tooltip("How much of a blocker is punched out. 1 removes it entirely, which loses " +
                 "the sense of a building being there at all.")]
        [Range(0f, 1f)]
        [SerializeField] private float _fadeTo = 0.72f;

        [SerializeField] private float _fadeInSeconds = 0.14f;

        [Tooltip("Slower coming back, so brushing past a corner does not flicker the wall.")]
        [SerializeField] private float _fadeOutSeconds = 0.35f;

        private readonly RaycastHit[] _hits = new RaycastHit[24];
        private readonly Dictionary<Renderer, float> _faded = new Dictionary<Renderer, float>();
        private readonly List<Renderer> _blocking = new List<Renderer>();
        private readonly List<Renderer> _finished = new List<Renderer>();
        private MaterialPropertyBlock _block;
        private Camera _camera;

        private void Awake()
        {
            _block = new MaterialPropertyBlock();
            if (_target == null)
            {
                var rig = GetComponent<OverworldCameraRig>();
                if (rig != null) _target = rig.transform;
            }
        }

        private void LateUpdate()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null || _target == null) return;

            CollectBlockers();
            Drive(Time.deltaTime);
        }

        private void CollectBlockers()
        {
            _blocking.Clear();

            var from = _camera.transform.position;
            var to = _target.position;
            var direction = to - from;
            var distance = direction.magnitude;
            if (distance < 0.05f) return;
            direction /= distance;

            var count = Physics.SphereCastNonAlloc(from, _probeRadius, direction, _hits,
                distance, _mask, QueryTriggerInteraction.Ignore);

            for (var i = 0; i < count; i++)
            {
                var collider = _hits[i].collider;
                if (collider == null) continue;

                // Never the player, and never the ground. Fading the terrain the player is
                // standing on would punch a hole in the world under their feet — the camera
                // looks down at it, so it is always between the two.
                if (collider.transform.root == _target.root) continue;
                if (collider.gameObject.layer == LayerMask.NameToLayer("Ground")) continue;

                foreach (var renderer in collider.GetComponentsInChildren<Renderer>())
                    if (renderer != null) _blocking.Add(renderer);
            }
        }

        private void Drive(float dt)
        {
            foreach (var renderer in _blocking)
            {
                _faded.TryGetValue(renderer, out var current);
                _faded[renderer] = _fadeInSeconds <= 0f
                    ? _fadeTo
                    : Mathf.MoveTowards(current, _fadeTo, dt / _fadeInSeconds * _fadeTo);
            }

            _finished.Clear();
            foreach (var pair in _faded)
            {
                var renderer = pair.Key;
                if (renderer == null) { _finished.Add(renderer); continue; }

                var amount = pair.Value;
                if (!_blocking.Contains(renderer))
                {
                    amount = _fadeOutSeconds <= 0f
                        ? 0f
                        : Mathf.MoveTowards(amount, 0f, dt / _fadeOutSeconds * _fadeTo);
                }

                renderer.GetPropertyBlock(_block);
                _block.SetFloat(FadeAmount, amount);
                renderer.SetPropertyBlock(_block);

                // Dropped once it is solid again, so the dictionary tracks only what is
                // actually faded rather than growing to every prop the player has passed.
                if (amount <= 0.0001f) _finished.Add(renderer);
            }

            foreach (var renderer in _finished) _faded.Remove(renderer);
        }

        private void OnDisable()
        {
            // Anything mid-fade would otherwise stay punched through for the rest of the
            // session, with nothing left running to restore it.
            foreach (var pair in _faded)
            {
                if (pair.Key == null) continue;
                pair.Key.GetPropertyBlock(_block);
                _block.SetFloat(FadeAmount, 0f);
                pair.Key.SetPropertyBlock(_block);
            }
            _faded.Clear();
        }
    }
}
