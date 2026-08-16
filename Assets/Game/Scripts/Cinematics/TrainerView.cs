using System.Collections;
using PokeLab.Overworld.People;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// The person who throws the ball, standing on a trainer mark for exactly as long as the
    /// throw takes.
    ///
    /// The throw itself was already built — <see cref="BallActor"/> arcs out of
    /// <c>BattleStage.TrainerMarkOf</c> on every send-out and on every capture — and the marks
    /// were empty transforms, so the ball came out of thin air. This is the missing half and
    /// nothing more: a sprite, an entrance and an exit.
    ///
    /// <b>Only around the throw.</b> The series puts the trainer on screen for the wind-up and
    /// takes them off once the creature is out, and that is not decoration: the shot is framed on
    /// two combatants, and a person standing at the near mark for the whole battle is a
    /// foreground occluder in every frame of it. So this defaults to hidden and
    /// <see cref="BattlePresenter"/> brackets the throw with <see cref="Enter"/> and
    /// <see cref="Exit"/>.
    ///
    /// <b>The drawing is the overworld's.</b> <see cref="PersonBillboard"/> already resolves a
    /// person key against <c>people_manifest.json</c>, picks front/back/side by sector and
    /// mirrors the side sheet correctly. Reimplementing that here to keep the cinematics assembly
    /// free of the overworld would be three hundred lines of duplicate that drifts the first time
    /// the manifest changes shape, so the reference goes the other way instead — which is safe,
    /// because the overworld deliberately cannot see this assembly and so no cycle exists.
    /// </summary>
    [DefaultExecutionOrder(-150)]
    [DisallowMultipleComponent]
    public sealed class TrainerView : MonoBehaviour
    {
        [Tooltip("The billboard that draws the person. Built on this object's Visual child when empty.")]
        [SerializeField] private PersonBillboard billboard;

        [Tooltip("Metres the trainer slides sideways to leave the frame. Sideways rather than " +
                 "backwards: the camera stands behind the player's mark, so a trainer walking " +
                 "back would walk into the lens.")]
        [SerializeField] private float exitOffset = 3.4f;

        private Transform _visual;

        /// <summary>True when a person key resolved to art. False means nothing will be drawn.</summary>
        public bool HasArt { get; private set; }

        private void Awake()
        {
            EnsureBuilt();
            // Hidden until somebody throws. A trainer visible on the first frame of a battle is
            // one standing in shot through the whole opening, which is the thing this is not.
            SetShown(false);
        }

        private void EnsureBuilt()
        {
            if (_visual == null)
            {
                Transform found = transform.Find("Visual");
                if (found == null)
                {
                    var go = new GameObject("Visual");
                    go.transform.SetParent(transform, false);
                    go.AddComponent<MeshFilter>();
                    go.AddComponent<MeshRenderer>();
                    found = go.transform;
                }
                _visual = found;
            }

            if (billboard == null) billboard = _visual.GetComponent<PersonBillboard>();
            if (billboard == null) billboard = _visual.gameObject.AddComponent<PersonBillboard>();

            // The billboard reads its facing off its motion source, and this object is the one
            // whose yaw the stage aligns with the field. Left to default it would take the
            // Visual child's own parent — which is this object anyway — but saying so keeps the
            // sector maths below true if the hierarchy ever gains a level.
            billboard.SetMotionSource(transform);
        }

        /// <summary>
        /// Binds the character to draw.
        ///
        /// The key is lower-cased first: the manifest is keyed in lower case and the ids that
        /// reach here come from trainer data written as "Youngster", which the library's alias
        /// table cannot match and would report as a character with no art.
        /// </summary>
        public void Bind(string personKey)
        {
            EnsureBuilt();

            if (string.IsNullOrEmpty(personKey))
            {
                HasArt = false;
                SetShown(false);
                return;
            }

            billboard.PersonKey = personKey.ToLowerInvariant();
            HasArt = PersonSpriteLibrary.Shared.Find(billboard.PersonKey) != null;
            SetShown(false);
        }

        /// <summary>
        /// Walks the trainer in from off frame to their mark.
        ///
        /// The facing is not tweened and does not need to be: the mark already looks down the
        /// stage axis, and the battle camera is 18° off that axis, which puts the trainer 27°
        /// clear of the billboard's 45° back/side boundary. That margin is the whole reason the
        /// player is drawn from behind here rather than as a mirrored side view — the same
        /// failure the creature billboards had, and the same fix.
        /// </summary>
        public IEnumerator Enter(float seconds)
        {
            if (!HasArt) yield break;

            Vector3 from = OffMark;
            transform.localPosition = from;
            SetShown(true);

            yield return CinematicRunner.Tween(Mathf.Max(0.01f, seconds), CinematicEase.OutCubic,
                p => transform.localPosition = Vector3.Lerp(from, Vector3.zero, p));
            transform.localPosition = Vector3.zero;
        }

        /// <summary>Stands the trainer on their mark with no entrance, for a beat already in progress.</summary>
        public void Present()
        {
            if (!HasArt) return;
            transform.localPosition = Vector3.zero;
            SetShown(true);
        }

        /// <summary>
        /// Walks the trainer out of frame and switches them off.
        ///
        /// Switched off rather than left standing at the offset: the exit distance is tuned for
        /// this camera, and any shot that pushes in or changes angle would find them parked just
        /// outside the frame they were supposed to have left.
        /// </summary>
        public IEnumerator Exit(float seconds)
        {
            if (!HasArt || _visual == null || !_visual.gameObject.activeSelf) yield break;

            Vector3 from = transform.localPosition;
            Vector3 to = OffMark;

            yield return CinematicRunner.Tween(Mathf.Max(0.01f, seconds), CinematicEase.InOutCubic,
                p => transform.localPosition = Vector3.Lerp(from, to, p));

            SetShown(false);
            transform.localPosition = Vector3.zero;
        }

        /// <summary>Hides the trainer at once. For tearing a battle down, never mid-beat.</summary>
        public void Hide()
        {
            transform.localPosition = Vector3.zero;
            SetShown(false);
        }

        /// <summary>
        /// Where the trainer stands when they are off the frame, in the mark's own space.
        ///
        /// Local rather than world, and that is what makes one number serve both sides: the two
        /// trainer marks face each other, so local -X is screen-left for the player and
        /// screen-right for the opponent — each of them leaves by their own edge of the frame.
        /// Working in world space would also mean re-solving every time the stage rescales itself
        /// to the creatures standing in it, which it does on every send-out.
        /// </summary>
        private Vector3 OffMark => new Vector3(-Mathf.Abs(exitOffset), 0f, 0f);

        private void SetShown(bool shown)
        {
            EnsureBuilt();
            if (_visual != null) _visual.gameObject.SetActive(shown);
        }
    }
}
