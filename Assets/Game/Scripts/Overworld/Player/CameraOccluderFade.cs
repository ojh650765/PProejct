using System.Collections.Generic;
using Unity.Cinemachine;
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

        [Tooltip("How far short of the player the probe stops. Anything level with them rather " +
                 "than in front of them is not standing between them and the camera, however " +
                 "close it is. Roughly the player's own width plus the probe radius.")]
        [SerializeField] private float _clearanceAtTarget = 1.2f;

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

        /// <summary>
        /// Every ghost we have made, back to the material it was cloned from.
        ///
        /// This is what makes the fault recoverable rather than merely rarer. If a renderer is
        /// ever met while it is already wearing our clones and we have no record of its solid
        /// set — a level rebuild between frames, a band streamed out mid-fade, anything that
        /// empties the bookkeeping without restoring first — reading <c>sharedMaterials</c>
        /// would file those clones as "solid" and the building could never be put back. Mapping
        /// each clone to its source means the original is recoverable from the renderer itself.
        /// </summary>
        private readonly Dictionary<Material, Material> _ghostSource =
            new Dictionary<Material, Material>();
        private readonly List<Renderer> _blocking = new List<Renderer>();
        private readonly List<Renderer> _finished = new List<Renderer>();
        private readonly List<Renderer> _fading = new List<Renderer>();
        private readonly List<Renderer> _stale = new List<Renderer>();
        private MaterialPropertyBlock _block;
        private Camera _camera;

        /// <summary>
        /// The one instance allowed to touch materials this frame.
        ///
        /// Three copies of this component are alive at once — the persistent rig plus one in
        /// each streamed band — and they all read the same <see cref="Camera.main"/>. That is
        /// what made a faded building stay faded forever, and it is why widening the restore
        /// paths twice did not help: both fixes made fading *work more reliably*, which made
        /// the collision more likely, not less.
        ///
        /// The collision is in the bookkeeping, not the fading. Instance A swaps a renderer
        /// onto ghost materials and remembers the solid ones. Instance B then meets the same
        /// renderer, finds nothing in its own table, and reads <c>sharedMaterials</c> — which
        /// now returns A's ghosts. B files those as that renderer's solid set. From then on
        /// every restore either instance performs puts the building back onto a transparent
        /// material, and no amount of correct restore logic can recover it: the record of what
        /// solid looked like is gone.
        /// </summary>
        private static CameraOccluderFade _owner;

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
            if (!ClaimOwnership())
            {
                // Handing back rather than merely standing down. A band that loses ownership
                // mid-fade is holding the only record of what those renderers looked like
                // solid, and nothing else will ever put them back.
                ReleaseAll();
                return;
            }

            if (_camera == null) _camera = Camera.main;

            // Re-resolved rather than trusted. Awake caught the follow target once, and a
            // Single scene load destroys the player it pointed at — after which this is a
            // reference to a dead object for the rest of the session.
            if (_target == null)
            {
                var rig = GetComponent<OverworldCameraRig>();
                if (rig != null) _target = rig.FollowTarget;
            }

            // Restored, not merely skipped.
            //
            // Losing the camera or the target is exactly when something is already ghosted:
            // the player is being torn down for a scene load, or the brain has not picked a
            // camera yet. Returning here used to jump over the whole of Drive — including the
            // sweep that puts stale renderers back — so every wall that happened to be faded
            // at that moment stayed glass for the rest of the session, and nothing left
            // running had any record of what solid looked like.
            if (_camera == null || _target == null)
            {
                ReleaseAll();
                return;
            }

            CollectBlockers();
            Drive(Time.deltaTime);
        }

        /// <summary>
        /// Whether this instance is the one steering the live camera.
        ///
        /// Asked of Cinemachine rather than settled first-wins, because first-wins resolves by
        /// script execution order — and the winner could just as easily be a band's leftover
        /// rig following a player nobody is controlling, which would dissolve buildings around
        /// a spawn point on the other side of the map. When the brain is driving something else
        /// entirely, such as a battle camera, nobody owns it and nobody fades, which is right.
        /// </summary>
        private bool ClaimOwnership()
        {
            var mine = GetComponent<CinemachineVirtualCameraBase>();
            if (mine != null)
            {
                // Reached through the camera rather than a static accessor: Cinemachine 3
                // dropped the brain registry, and the brain is a component on whichever camera
                // is rendering anyway.
                if (_camera == null) _camera = Camera.main;
                var brain = _camera != null ? _camera.GetComponent<CinemachineBrain>() : null;
                var live = brain != null ? brain.ActiveVirtualCamera : null;
                if (live != null)
                {
                    var isMine = ReferenceEquals(live as CinemachineVirtualCameraBase, mine);
                    if (isMine) _owner = this;
                    return isMine;
                }
            }

            // No brain, or this rig carries no virtual camera: fall back to first-wins so a
            // plain camera setup still gets occlusion fading rather than none.
            if (_owner == null) _owner = this;
            return ReferenceEquals(_owner, this);
        }

        /// <summary>Puts everything this instance ghosted back, and forgets it.</summary>
        private void ReleaseAll()
        {
            if (_solidMaterials.Count == 0 && _faded.Count == 0) return;

            foreach (var pair in _faded)
            {
                if (pair.Key == null) continue;
                pair.Key.GetPropertyBlock(_block);
                _block.SetFloat(FadeAmount, 0f);
                pair.Key.SetPropertyBlock(_block);
            }
            _faded.Clear();

            foreach (var pair in _solidMaterials)
                if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            _solidMaterials.Clear();
        }

        private void CollectBlockers()
        {
            _blocking.Clear();

            var from = _camera.transform.position;
            var to = _target.position;

            // Rays at the player, not a fat sphere swept past them.
            //
            // The sweep asked "does this lie near the line to the player", and near is not the
            // question. A 0.55 m probe run the whole way collects whatever it brushes — a wall
            // the player is standing against, a fence the boom has backed into, a lamp beside
            // the path — none of which cover the player, all of which then sit at glass for as
            // long as the geometry keeps being brushed. Measured in town it was holding six
            // things transparent at once, one of them 8.5 m off the line and behind the player.
            //
            // What the feature is actually for is "what is drawn over the character", and a ray
            // from the eye to the character answers exactly that and nothing else. Several of
            // them, up the player's height, so a pillar covering half of them still counts;
            // each stopped short of the body so the ground they stand on and the wall they lean
            // against are never in the answer.
            for (var s = 0; s < SampleHeights.Length; s++)
            {
                var point = to + Vector3.up * SampleHeights[s];
                var direction = point - from;
                var distance = direction.magnitude;
                if (distance < 0.05f) continue;
                direction /= distance;

                var reach = distance - _clearanceAtTarget;
                if (reach <= 0.05f) continue;

                var count = Physics.RaycastNonAlloc(from, direction, _hits, reach, _mask,
                    QueryTriggerInteraction.Ignore);

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
                        if (renderer != null && CanFade(renderer) && !_blocking.Contains(renderer))
                            _blocking.Add(renderer);
                }
            }
        }

        /// <summary>
        /// Heights up the player the rays are aimed at, in metres from their feet.
        ///
        /// Three is enough to catch something covering only the head or only the legs, and few
        /// enough that this stays three raycasts a frame rather than a shape query.
        /// </summary>
        private static readonly float[] SampleHeights = { 0.25f, 0.9f, 1.5f };

        /// <summary>
        /// Whether this renderer is the kind of thing that should give way.
        ///
        /// Architecture is. Vegetation and people are not, and letting them through is what the
        /// stuck-translucency reports were actually looking at. A grass or foliage batch is one
        /// renderer covering a large part of the frame; putting it on the transparent queue with
        /// depth writing off makes it sort against everything else by distance to a single
        /// origin, so it stops being leaves and becomes a flat sheet of green laid over the
        /// shot. Billboards are worse: they are already alpha-clipped by their own shader, so a
        /// transparent clone gains nothing and loses the depth they rely on to sit in the world,
        /// and a half-dissolved NPC standing in a cutscene reads as a rendering fault.
        ///
        /// The camera stands still through the long scenes — the starter choice, the ambush —
        /// so anything caught here stays caught for as long as the shot lasts. That is why this
        /// has to be a rule about what may fade rather than a timeout.
        /// </summary>
        private static bool CanFade(Renderer renderer)
        {
            if (renderer is SpriteRenderer || renderer is ParticleSystemRenderer) return false;

            var materials = renderer.sharedMaterials;
            for (var i = 0; i < materials.Length; i++)
            {
                var material = materials[i];
                if (material == null) continue;

                // The project builds its billboard materials at runtime and names them with a
                // leading '~'; the shader check catches the authored ones alongside them.
                if (material.name.Length > 0 && material.name[0] == '~') return false;
                if (material.name.IndexOf("Foliage", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;

                var shader = material.shader;
                if (shader != null && shader.name.IndexOf("Billboard", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return false;
            }

            return true;
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

            // Walked over a copy of the keys so the fade can be written back.
            //
            // This read the amount out of the dictionary, decayed it into a local, drew with
            // it — and dropped it, because a dictionary cannot be assigned to while it is
            // being enumerated. Every frame started again from the stored value and threw one
            // frame of fading out away, so the amount never came down, the renderer was never
            // finished, MakeSolid was never called, and a wall that went to glass once stayed
            // glass for the rest of the session. Walking about town simply added to the pile.
            _fading.Clear();
            foreach (var pair in _faded) _fading.Add(pair.Key);

            _finished.Clear();
            for (var index = 0; index < _fading.Count; index++)
            {
                var renderer = _fading[index];
                if (renderer == null) { _finished.Add(renderer); continue; }

                var amount = _faded[renderer];
                if (!_blocking.Contains(renderer))
                {
                    amount = _fadeOutSeconds <= 0f
                        ? 0f
                        : Mathf.MoveTowards(amount, 0f, dt / _fadeOutSeconds * _fadeTo);
                }
                _faded[renderer] = amount;

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

            var solid = SolidSetOf(renderer);
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
                    _ghostSource[ghost] = source;
                }
                ghosted[i] = ghost;
            }
            renderer.sharedMaterials = ghosted;
        }

        /// <summary>
        /// What this renderer looks like solid, with any of our clones mapped back to source.
        ///
        /// Reading <c>sharedMaterials</c> straight is only correct while the invariant holds.
        /// This makes it correct unconditionally, so the record of solid can never be a
        /// transparent clone of itself.
        /// </summary>
        private Material[] SolidSetOf(Renderer renderer)
        {
            var current = renderer.sharedMaterials;
            for (var i = 0; i < current.Length; i++)
            {
                var material = current[i];
                if (material == null) continue;
                if (_ghostSource.TryGetValue(material, out var source)) current[i] = source;
            }
            return current;
        }

        private void MakeSolid(Renderer renderer)
        {
            if (!_solidMaterials.TryGetValue(renderer, out var solid)) return;
            renderer.sharedMaterials = solid;
            _solidMaterials.Remove(renderer);
        }

        private void OnDisable()
        {
            if (ReferenceEquals(_owner, this)) _owner = null;

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
            _ghostSource.Clear();

            _faded.Clear();
        }
    }
}
