using System.Collections;
using Unity.Cinemachine;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The starter choice, staged in the world: the case set down on the ground, three balls
    /// standing in it, and a camera that comes down to their height for it.
    ///
    /// This is the presentation half of the beat and it deliberately knows nothing about the
    /// choice. It is told where the case goes, which species are in which ball and which ball
    /// the cursor is on; it never reads input, never touches
    /// <c>StarterSelection</c> and cannot commit anything. <c>PokeLab.Boot</c> owns
    /// that half and drives this one, which is the same split the rest of the game uses and
    /// the only reason a Cinematics class can stage an Overworld moment at all — the two
    /// assemblies cannot see each other.
    ///
    /// <b>The camera is taken with a virtual camera, not by moving the real one.</b> The
    /// exploration rig's framing is authored — 8 degrees of pitch on an 8.5 m boom — and a
    /// beat that writes to the main camera's transform has to put every one of those numbers
    /// back afterwards, exactly, from memory. A <see cref="CinemachineCamera"/> at a higher
    /// priority needs none of that: the brain blends to it, the beat drops the priority, and
    /// the brain blends back to a rig that was never touched. It also means the push-in is a
    /// blend rather than a cut, which is what the shot wants anyway.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StarterCaseStage : MonoBehaviour
    {
        /// <summary>
        /// What the world draws its pixel art at, relative to the size the manifest claims.
        ///
        /// <c>PersonBillboard</c> puts the cast at 0.58 of their traced height, so the people
        /// in this scene are 0.93 m tall and not 1.6 m. A prop drawn at its literal manifest
        /// height standing next to them is the thing that looks wrong, not the people — a
        /// 1.02 m briefcase beside a 0.93 m professor is a wardrobe. Everything this class
        /// places in the world goes through this factor for that reason.
        /// </summary>
        public const float WorldSpriteScale = 0.58f;

        /// <summary>
        /// Priority the shot camera claims. Above the exploration rig and above
        /// <see cref="OverworldCinematics"/>'s 120, because a trainer noticing the player
        /// mid-choice must not be allowed to pull the camera off the case.
        /// </summary>
        private const int ShotPriority = 150;

        // The two shots, both measured from the ball line rather than from the case's feet,
        // so the framing survives the case art being redrawn at a different height.
        //
        // The reveal shot is *wider* than the browse shot, which is the opposite of the
        // instinct. The registry has these creatures at 0.5-0.7 m and the browse shot frames
        // three quarters of a metre: pushing in on the reveal puts the creature's head and
        // most of its body outside the frame at the exact moment it is supposed to be looked
        // at. The push-in belongs to the balls; the creature gets the pull-back.
        private const float ShotDistance = 1.35f;
        private const float ShotHeight = 0.40f;
        private const float ShotAimY = 0.04f;
        private const float ShotFov = 32f;

        private const float RevealDistance = 2.35f;
        private const float RevealHeight = 0.85f;
        private const float RevealAimY = 0.30f;

        /// <summary>Centre-to-centre spacing of the balls. The drawn case is ~0.55 m across.</summary>
        private const float BallSpacing = 0.17f;
        /// <summary>How far the ball under the cursor lifts out of the case.</summary>
        private const float HighlightRise = 0.045f;

        private sealed class Slot
        {
            public Transform Anchor;      // rest position of the ball, in the case
            public Transform Ball;        // the ball itself, lifted and spun off the anchor
            public GameObject Closed;
            public GameObject Open;
            public Light Glow;
            public Transform CreatureRoot;
            public CreatureBillboard Creature;
            public float Lift;            // current rise, metres, damped toward the target
            public float Spin;
            /// <summary>
            /// True while a beat owns this ball's transform. <see cref="LateUpdate"/> writes
            /// the cursor's lift every frame and runs after coroutines do, so without this the
            /// shiver in <see cref="Reveal"/> is overwritten on the same frame it is applied
            /// and the ball simply sits there before opening.
            /// </summary>
            public bool Scripted;
        }

        private Slot[] _slots = System.Array.Empty<Slot>();
        private StarterSpriteCard _case;
        private StarterSpriteCard _player;
        private Light _caseLight;
        private CinemachineCamera _shot;
        private StarterBallKit _kit;

        private Vector3 _ballRowCentre;
        private Vector3 _shotBack;        // unit vector from the balls toward the camera
        private int _highlighted = -1;
        private bool _open;

        /// <summary>Which ball the cursor is on, or -1 before the case opens.</summary>
        public int Highlighted => _highlighted;

        /// <summary>Number of balls staged.</summary>
        public int Count => _slots.Length;

        /// <summary>
        /// Builds the stage at a case position and points a shot at it.
        ///
        /// <paramref name="ballLine"/> is the <c>Look_Case</c> marker, and it is used as
        /// authored: its Y is 0.53 m above the ground the actors stand on, which is where the
        /// top of a 0.59 m case is — the marker is the balls' own height, not a guess at one.
        /// Its XZ is where the case is set down.
        /// </summary>
        public static StarterCaseStage Create(Vector3 ballLine, float groundY,
            Vector3 professorPosition, Vector3 playerPosition)
        {
            var go = new GameObject("~StarterCaseStage");
            var stage = go.AddComponent<StarterCaseStage>();
            stage.Lay(ballLine, groundY, professorPosition, playerPosition);
            return stage;
        }

        private void Lay(Vector3 ballLine, float groundY, Vector3 professorPosition,
            Vector3 playerPosition)
        {
            var ground = new Vector3(ballLine.x, groundY, ballLine.z);
            transform.position = ground;

            // The camera goes on the side of the case that neither actor is standing on.
            //
            // The obvious placement — behind the player, looking the way the exploration
            // camera already looks — puts the player directly between the lens and the case
            // at a metre and a half, so the shot is the back of someone's head. Linden and
            // the player stand within about a metre of the case and roughly a right angle
            // apart, so the bisector of the two of them, reversed, is the one direction with
            // nobody in it, and it leaves them flanking the case in frame instead.
            var toProfessor = Flat(professorPosition - ground);
            var toPlayer = Flat(playerPosition - ground);
            var away = -(toProfessor + toPlayer);
            _shotBack = away.sqrMagnitude > 1e-4f ? away.normalized : Vector3.forward;

            // Face the case out at the camera. Its own quad billboards, but the ball row is
            // laid along this and has to read as a row rather than as a line into the screen.
            transform.rotation = Quaternion.LookRotation(_shotBack, Vector3.up);

            _ballRowCentre = new Vector3(ballLine.x, ballLine.y, ballLine.z);

            _case = BuildCard("Case");
            _player = BuildCard("PlayerTakingBall");
            _player.gameObject.SetActive(false);

            // Warm, short-range, and inside the case rather than above it, so what it lights
            // is the balls and the inside of the lid. Off until the case opens.
            var lightGo = new GameObject("CaseLight");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = new Vector3(0f, (ballLine.y - groundY) * 0.6f, 0f);
            _caseLight = lightGo.AddComponent<Light>();
            _caseLight.type = LightType.Point;
            _caseLight.color = new Color(1f, 0.94f, 0.78f);
            _caseLight.range = 1.6f;
            _caseLight.intensity = 0f;
            _caseLight.shadows = LightShadows.None;

            BuildShotCamera();
        }

        private StarterSpriteCard BuildCard(string name)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            return go.AddComponent<StarterSpriteCard>();
        }

        /// <summary>
        /// Binds the case art. Sizes are the manifest's own, scaled by
        /// <see cref="WorldSpriteScale"/>; the caller reads them out of the people manifest
        /// because this assembly cannot.
        /// </summary>
        public void SetCaseArt(Texture2D texture, int columns, int rows, float frameHeightMetres,
            float groundOrigin)
        {
            if (_case == null) return;
            _case.Bind(texture, columns, rows, frameHeightMetres * WorldSpriteScale, groundOrigin);
            if (texture == null)
            {
                Debug.LogWarning("[Starter] No briefcase art resolved, so the balls will be " +
                                 "standing on nothing. The sprite is 'briefcase' under props in " +
                                 "people_manifest.json.", this);
            }
        }

        /// <summary>Binds the "player reaches into the case" art, shown once on the reveal.</summary>
        public void SetPlayerArt(Texture2D texture, int columns, int rows, float frameHeightMetres,
            float groundOrigin)
        {
            if (_player == null) return;
            _player.Bind(texture, columns, rows, frameHeightMetres * WorldSpriteScale, groundOrigin);

            // Beside the case on the camera's left and a little nearer the lens, facing in.
            // Not between the camera and the case, where a 1 m sprite at 1.2 m fills the frame
            // and the thing the player is choosing from is behind it.
            var left = Vector3.Cross(Vector3.up, _shotBack).normalized;
            _player.transform.position = new Vector3(_ballRowCentre.x, transform.position.y,
                _ballRowCentre.z) + left * 0.42f + _shotBack * 0.30f;
        }

        /// <summary>
        /// Stages one ball per option. The species is bound now rather than on the reveal so
        /// the sprite sheet is already resident when the loudest moment in the game starts.
        /// </summary>
        public void SetBalls(int[] speciesIds, float[] displayHeights)
        {
            if (speciesIds == null) return;
            _kit = StarterBallKit.Load();
            if (_kit == null || _kit.Closed == null)
            {
                Debug.LogWarning("[Starter] No StarterBallKit in Resources, so the case holds " +
                                 "primitive spheres. Rebuild it from Tools > Poké Lab > Art > " +
                                 "Rebuild Starter Ball Kit; the models are in Art/Props and " +
                                 "nothing outside Resources survives into a build.", this);
            }

            _slots = new Slot[speciesIds.Length];
            var span = (speciesIds.Length - 1) * 0.5f;

            for (var i = 0; i < speciesIds.Length; i++)
            {
                var slot = new Slot();
                _slots[i] = slot;

                var anchor = new GameObject($"Ball{i}").transform;
                anchor.SetParent(transform, false);
                // Local X runs along the row because the root looks at the camera.
                anchor.localPosition = new Vector3((i - span) * BallSpacing,
                    _ballRowCentre.y - transform.position.y, 0f);
                slot.Anchor = anchor;

                var ball = new GameObject("Model").transform;
                ball.SetParent(anchor, false);
                slot.Ball = ball;

                slot.Closed = InstantiateBall(_kit != null ? _kit.Closed : null, ball, "Closed");
                slot.Open = InstantiateBall(_kit != null ? _kit.Open : null, ball, "Open");
                if (slot.Open != null) slot.Open.SetActive(false);

                var glowGo = new GameObject("Glow");
                glowGo.transform.SetParent(ball, false);
                glowGo.transform.localPosition = new Vector3(0f, 0.12f, -0.06f);
                slot.Glow = glowGo.AddComponent<Light>();
                slot.Glow.type = LightType.Point;
                slot.Glow.color = new Color(0.72f, 0.95f, 1f);
                slot.Glow.range = 0.6f;
                slot.Glow.intensity = 0f;
                slot.Glow.shadows = LightShadows.None;

                // The creature stands on top of the ball rather than in front of it, so the
                // reveal reads as coming out of that ball and not as a picture appearing.
                var creatureRoot = new GameObject("Creature").transform;
                creatureRoot.SetParent(anchor, false);
                creatureRoot.localPosition = new Vector3(0f, 0.09f, 0f);
                slot.CreatureRoot = creatureRoot;

                var creatureGo = new GameObject("Sprite", typeof(MeshFilter), typeof(MeshRenderer));
                creatureGo.transform.SetParent(creatureRoot, false);
                slot.Creature = creatureGo.AddComponent<CreatureBillboard>();
                // Pinned to the front view. The sector test measures the creature's own facing
                // against the lens, and a creature standing on a ball has no facing worth
                // measuring — left to itself it shows the back sprite from half the angles the
                // camera blends through on the way in.
                slot.Creature.ForcedFacing = SpriteFacing.Front;
                slot.Creature.Bind(speciesIds[i],
                    displayHeights != null && i < displayHeights.Length ? displayHeights[i] : 0.5f,
                    null);
                slot.Creature.SetVisible(false);

                if (!slot.Creature.HasArt)
                {
                    // An untextured quad draws as a white rectangle the size of the creature,
                    // which on the reveal is worse than nothing at all. Drop the component so
                    // the beat still plays and the ball still opens on an empty pedestal.
                    Debug.LogWarning($"[Starter] Species {speciesIds[i]} has no sprite in " +
                                     "sprite_manifest.json, so its ball opens on nothing.", this);
                    Destroy(creatureGo);
                    slot.Creature = null;
                }

                // Sunk until the case opens. Set here rather than at the top of OpenCase, or
                // three balls stand in a shut case for however many frames pass between the
                // stage being built and the signal that opens it.
                anchor.localScale = Vector3.zero;
            }
        }

        private static GameObject InstantiateBall(GameObject prefab, Transform parent, string name)
        {
            GameObject instance;
            if (prefab != null)
            {
                instance = Instantiate(prefab, parent);
            }
            else
            {
                // A sphere at the ball's real diameter. Wrong, and wrong in a way that is
                // obvious in a capture, which is the point of a stand-in.
                instance = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                instance.transform.SetParent(parent, false);
                instance.transform.localScale = Vector3.one * 0.11f;
                var collider = instance.GetComponent<Collider>();
                if (collider != null) Destroy(collider);
            }

            instance.name = name;
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            return instance;
        }

        // --- Camera --------------------------------------------------------------------------

        private void BuildShotCamera()
        {
            var go = new GameObject("StarterCaseShot");
            go.transform.SetParent(transform, false);
            _shot = go.AddComponent<CinemachineCamera>();
            _shot.Lens = LensSettings.Default;
            _shot.Lens.FieldOfView = ShotFov;
            // No Follow and no LookAt on purpose: this camera is a fixed pose, and a tracking
            // component would let the shot drift with whoever it was bound to.
            _shot.Priority = 0;
            PlaceShot(0f);
            go.SetActive(false);
        }

        /// <summary>
        /// Interpolates the shot between the browse framing (0) and the reveal framing (1).
        /// One function rather than two poses so a beat cut off part-way cannot leave the
        /// camera at a pose nobody authored.
        /// </summary>
        private void PlaceShot(float toReveal)
        {
            if (_shot == null) return;

            var distance = Mathf.Lerp(ShotDistance, RevealDistance, toReveal);
            var height = Mathf.Lerp(ShotHeight, RevealHeight, toReveal);
            var aimY = Mathf.Lerp(ShotAimY, RevealAimY, toReveal);

            var aim = _ballRowCentre + Vector3.up * aimY;
            var position = _ballRowCentre + _shotBack * distance + Vector3.up * height;
            _shot.transform.SetPositionAndRotation(position,
                Quaternion.LookRotation((aim - position).normalized, Vector3.up));
        }

        /// <summary>Raises the shot. The brain blends; nothing is cut and nothing is written
        /// to the exploration rig.</summary>
        public void TakeShot()
        {
            if (_shot == null) return;
            _shot.gameObject.SetActive(true);
            _shot.Priority = ShotPriority;
        }

        /// <summary>Hands the camera back. The rig it returns to was never modified.</summary>
        public void ReleaseShot()
        {
            if (_shot == null) return;
            _shot.Priority = 0;
        }

        // --- Beats ---------------------------------------------------------------------------

        /// <summary>
        /// The case opens: the shot comes down to it, the light inside comes up, and the three
        /// balls rise into the row.
        /// </summary>
        public IEnumerator OpenCase()
        {
            if (_open) yield break;
            _open = true;

            TakeShot();

            yield return CinematicRunner.Tween(0.45f, CinematicEase.OutCubic,
                t => _caseLight.intensity = Mathf.Lerp(0f, 2.6f, t));

            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                // Wrapped rather than passed as a method group: OutBack's overshoot is an
                // optional parameter, and a method group conversion may not fill one in.
                CinematicRunner.Fork(CinematicRunner.Tween(0.42f, t => CinematicEase.OutBack(t, 1.6f),
                    t => slot.Anchor.localScale = Vector3.one * t));
                yield return CinematicRunner.Wait(0.12f);
            }

            yield return CinematicRunner.Wait(0.3f);
        }

        /// <summary>Moves the cursor. Idempotent, and safe before the case has opened.</summary>
        public void Highlight(int index)
        {
            _highlighted = index >= 0 && index < _slots.Length ? index : -1;
        }

        /// <summary>Where a ball is in the world, for a screen-space cursor to sit over.</summary>
        public Vector3 BallPoint(int index)
        {
            if (index < 0 || index >= _slots.Length) return _ballRowCentre;
            return _slots[index].Ball.position;
        }

        /// <summary>
        /// The reveal. The ball opens and the creature stands up out of it.
        ///
        /// Written as one routine rather than as three the caller sequences, because the
        /// timing between the shake, the split and the creature landing is the whole effect
        /// and it should not be possible to get it wrong from outside.
        /// </summary>
        public IEnumerator Reveal(int index)
        {
            if (index < 0 || index >= _slots.Length) yield break;
            var slot = _slots[index];

            Highlight(index);
            slot.Scripted = true;
            if (_player != null) _player.gameObject.SetActive(true);

            // Wind up: the ball shivers in place. Short — anything longer reads as a stall.
            var lift = slot.Lift;
            yield return CinematicRunner.Progress(0.35f, t =>
            {
                var shake = Mathf.Sin(t * 46f) * 0.008f * (1f - t);
                slot.Ball.localPosition = new Vector3(shake, lift, 0f);
            });

            if (slot.Closed != null) slot.Closed.SetActive(false);
            if (slot.Open != null) slot.Open.SetActive(true);
            slot.Glow.intensity = 9f;

            if (slot.Creature != null)
            {
                slot.Creature.SetVisible(true);
                slot.Creature.Flash(1f);
            }

            CinematicRunner.Fork(CinematicRunner.Tween(0.8f, CinematicEase.InOutCubic,
                t => PlaceShot(t)));

            // Out of the ball: a fast overshoot to full size. OutBack rather than OutCubic
            // because the creature has to arrive rather than fade up.
            yield return CinematicRunner.Tween(0.55f, t => CinematicEase.OutBack(t, 2.1f), t =>
            {
                var scale = Mathf.Lerp(0.15f, 1f, t);
                if (slot.Creature != null) slot.Creature.SetSquash(new Vector3(scale, scale, scale));
            });

            yield return CinematicRunner.Tween(0.5f, CinematicEase.OutCubic,
                t => slot.Glow.intensity = Mathf.Lerp(9f, 3.2f, t));
        }

        /// <summary>Undoes a reveal, for a player who looks and then changes their mind.</summary>
        public IEnumerator Unreveal(int index)
        {
            if (index < 0 || index >= _slots.Length) yield break;
            var slot = _slots[index];

            if (_player != null) _player.gameObject.SetActive(false);
            CinematicRunner.Fork(CinematicRunner.Tween(0.5f, CinematicEase.InOutCubic,
                t => PlaceShot(1f - t)));

            yield return CinematicRunner.Tween(0.28f, CinematicEase.InCubic, t =>
            {
                var scale = Mathf.Lerp(1f, 0.1f, t);
                if (slot.Creature != null) slot.Creature.SetSquash(new Vector3(scale, scale, scale));
                slot.Glow.intensity = Mathf.Lerp(3.2f, 0f, t);
            });

            if (slot.Creature != null) slot.Creature.SetVisible(false);
            if (slot.Open != null) slot.Open.SetActive(false);
            if (slot.Closed != null) slot.Closed.SetActive(true);
            slot.Scripted = false;
        }

        /// <summary>
        /// Gives the camera back and takes the case away, in that order — the case must not
        /// blink out in a frame the close shot is still on.
        /// </summary>
        public IEnumerator Dismiss(float blendSeconds = 1.6f)
        {
            ReleaseShot();
            yield return CinematicRunner.Wait(blendSeconds);
            if (this != null) Destroy(gameObject);
        }

        private void LateUpdate()
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[i];
                if (slot.Scripted) continue;

                var selected = i == _highlighted;

                var wantedLift = selected ? HighlightRise : 0f;
                slot.Lift = Mathf.Lerp(slot.Lift, wantedLift, 1f - Mathf.Exp(-14f * Time.deltaTime));
                slot.Ball.localPosition = new Vector3(0f, slot.Lift, 0f);

                // Only the ball under the cursor turns. Three balls all spinning is a display
                // case; one spinning is the one being looked at.
                if (selected) slot.Spin += Time.deltaTime * 42f;
                slot.Ball.localRotation = Quaternion.Euler(0f, slot.Spin, 0f);

                var wantedGlow = selected ? 2.4f : 0f;
                if (slot.Creature == null || !slot.Creature.IsVisible)
                    slot.Glow.intensity = Mathf.Lerp(slot.Glow.intensity, wantedGlow,
                        1f - Mathf.Exp(-10f * Time.deltaTime));
            }
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.zero;
        }
    }
}
