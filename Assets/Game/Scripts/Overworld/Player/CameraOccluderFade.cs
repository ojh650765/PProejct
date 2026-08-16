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
    /// It is real alpha, not a dither. A stipple was tried first and reads as a dissolve —
    /// the object breaking up — rather than as glass, which is not what a wall between you
    /// and the thing you are steering should look like.
    ///
    /// Real alpha costs a material, because blending and depth-writing are material state
    /// and cannot be set per renderer. So each blocker's material gets one transparent clone,
    /// created on first use and shared by every object using that material; the renderer is
    /// swapped onto the clone while it fades and back when it is solid. That bounds the cost
    /// to the handful of things actually in the way rather than to the whole prop set, and
    /// the strength itself still rides a MaterialPropertyBlock so two walls can be at
    /// different opacities on the same clone.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraOccluderFade : MonoBehaviour
    {
        private static readonly int FadeAmount = Shader.PropertyToID("_FadeAmount");
        private static readonly int SrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int DstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int ZWriteMode = Shader.PropertyToID("_ZWriteMode");

        [Header("Probe")]
        [Tooltip("What the player is followed at. Defaults to the rig's own follow target.")]
        [SerializeField] private Transform _target;

        [Tooltip("Cast radius. Wide enough that a pillar clipping the frame edge is caught, " +
                 "since a wall you can see past on one side is the annoying case, not the " +
                 "one that fully blocks.")]
        [SerializeField] private float _probeRadius = 0.55f;

        [SerializeField] private LayerMask _mask = ~0;

        [Header("Fade")]
        [Tooltip("How transparent a blocker becomes. 1 is invisible, which loses the sense " +
                 "of a building being there at all — it should read as glass, not as a gap.")]
        [Range(0f, 1f)]
        [SerializeField] private float _fadeTo = 0.72f;

        [SerializeField] private float _fadeInSeconds = 0.14f;

        [Tooltip("Slower coming back, so brushing past a corner does not flicker the wall.")]
        [SerializeField] private float _fadeOutSeconds = 0.35f;

        private readonly RaycastHit[] _hits = new RaycastHit[24];
        private readonly Dictionary<Renderer, float> _faded = new Dictionary<Renderer, float>();
        private readonly Dictionary<Renderer, Material[]> _solidMaterials =
            new Dictionary<Renderer, Material[]>();
        private readonly Dictionary<(Renderer, Material), Material> _ghosts =
            new Dictionary<(Renderer, Material), Material>();
        private readonly List<Renderer> _blocking = new List<Renderer>();
        private readonly List<Renderer> _finished = new List<Renderer>();
        private readonly List<Renderer> _stale = new List<Renderer>();
        private MaterialPropertyBlock _block;
        private Camera _camera;

        private void Awake()
        {
            _block = new MaterialPropertyBlock();
            if (_target == null)
            {
                // The rig's follow target, not the rig's transform. This component sits on
                // the CinemachineCamera alongside OverworldCameraRig, and Cinemachine drives
                // that transform to exactly where the brain puts Camera.main — so falling
                // back to it made the probe a cast from the camera to itself. CollectBlockers
                // discards anything under 5 cm as a degenerate direction, so it returned on
                // the first line every frame and nothing was ever collected or faded. Every
                // scene serialises _target as null, so this fallback is the only path.
                var rig = GetComponent<OverworldCameraRig>();
                if (rig != null) _target = rig.FollowTarget;
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

                if (amount > 0.0001f) MakeGhost(renderer);
                else MakeSolid(renderer);

                renderer.GetPropertyBlock(_block);
                _block.SetFloat(FadeAmount, amount);
                renderer.SetPropertyBlock(_block);

                // Dropped once it is solid again, so the dictionary tracks only what is
                // actually faded rather than growing to every prop the player has passed.
                if (amount <= 0.0001f) _finished.Add(renderer);
            }

            foreach (var renderer in _finished) _faded.Remove(renderer);

            // Anything still wearing ghost materials that is no longer fading is put back.
            //
            // The invariant this enforces is "ghosted implies fading". Without it the two
            // bookkeeping sets can drift apart — a renderer destroyed and rebuilt by a level
            // rebuild, a scene streamed out mid-fade, an early return that skips a frame — and
            // a renderer that leaves _faded while still on the transparent clone has nothing
            // left that would ever restore it. That is the reported fault exactly: a building
            // goes translucent once and stays translucent, and after a walk around town every
            // building the camera has passed behind is translucent at the same time.
            _stale.Clear();
            foreach (var pair in _solidMaterials)
            {
                if (pair.Key == null) { _stale.Add(pair.Key); continue; }
                if (!_faded.ContainsKey(pair.Key)) _stale.Add(pair.Key);
            }
            foreach (var renderer in _stale)
            {
                if (renderer != null)
                {
                    renderer.GetPropertyBlock(_block);
                    _block.SetFloat(FadeAmount, 0f);
                    renderer.SetPropertyBlock(_block);
                }
                MakeSolid(renderer);
                _solidMaterials.Remove(renderer);
            }
        }

        /// <summary>
        /// Puts a renderer onto transparent clones of its materials, remembering the
        /// originals so it can be put back exactly.
        /// </summary>
        private void MakeGhost(Renderer renderer)
        {
            if (_solidMaterials.ContainsKey(renderer)) return;

            var solid = renderer.sharedMaterials;
            _solidMaterials[renderer] = solid;

            var ghosted = new Material[solid.Length];
            for (var i = 0; i < solid.Length; i++)
            {
                var source = solid[i];
                if (source == null) continue;

                // Keyed by renderer *and* source, not by source alone.
                //
                // Every building in town shares one atlas material, so a single ghost per
                // source is a single object shared by all of them — and any state that lives
                // on the material rather than in a property block is then shared too. Keeping
                // them separate costs one material per faded renderer, which is at most the
                // handful the camera is actually behind.
                var ghostKey = (renderer, source);
                if (!_ghosts.TryGetValue(ghostKey, out var ghost) || ghost == null)
                {
                    ghost = new Material(source) { name = source.name + " (fading)" };
                    // Alpha over what is behind, and no depth write — a faded wall that
                    // still wrote depth would hide the player standing behind it, which is
                    // the one thing this exists to prevent.
                    ghost.SetFloat(SrcBlend, (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    ghost.SetFloat(DstBlend, (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    ghost.SetFloat(ZWriteMode, 0f);
                    ghost.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    _ghosts[ghostKey] = ghost;
                }
                ghosted[i] = ghost;
            }
            renderer.sharedMaterials = ghosted;
        }

        private void MakeSolid(Renderer renderer)
        {
            if (!_solidMaterials.TryGetValue(renderer, out var solid)) return;
            renderer.sharedMaterials = solid;
            _solidMaterials.Remove(renderer);
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
            foreach (var pair in _solidMaterials)
                if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            _solidMaterials.Clear();

            foreach (var ghost in _ghosts.Values)
                if (ghost != null) Destroy(ghost);
            _ghosts.Clear();

            _faded.Clear();
        }
    }
}
