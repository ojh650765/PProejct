using PokeLab.Overworld;
using PokeLab.Overworld.People;
using PokeLab.UI;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Puts conversations on screen.
    ///
    /// <see cref="DialogueRunner"/> sequences lines, waits for input and raises an event per
    /// line; <see cref="DialogueView"/> draws a box, types the text out and offers choices.
    /// Both were finished. Neither had ever been told the other existed, so every
    /// conversation in the game ran to completion invisibly — the runner advancing through
    /// its lines, the player standing frozen watching nothing happen, and no error anywhere
    /// because from each side's point of view it was working.
    ///
    /// They could not simply reference each other: PokeLab.UI and PokeLab.Overworld are
    /// separate assemblies and neither is allowed to see the other, which is what kept them
    /// apart. This lives in PokeLab.Boot, which references both, and is the only place that
    /// knows the two exist together. Neither side gains a dependency.
    ///
    /// The view is created here rather than placed in the scene by hand. It builds its own
    /// hierarchy on Awake, so a scene that carries a serialized copy would have to be
    /// re-saved every time the layout changed, in every scene, and the copies would drift.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-50)]
    public sealed class DialoguePresenter : MonoBehaviour
    {
        [Tooltip("Left empty, one is built on Awake. Assign only to use a hand-authored box.")]
        [SerializeField] private DialogueView _view;

        [SerializeField] private DialogueRunner _runner;

        private void Awake()
        {
            if (_view == null) _view = BuildView();
        }

        private void OnEnable()
        {
            if (_runner == null) _runner = DialogueRunner.Instance;
            if (_runner == null) _runner = FindFirstObjectByType<DialogueRunner>();

            if (_runner == null)
            {
                Debug.LogWarning("[Dialogue] No DialogueRunner in this scene, so nothing will " +
                                 "ever ask for a line to be drawn.", this);
                return;
            }

            _runner.LinePresented += OnLine;
            _runner.SequenceEnded += OnEnded;
        }

        private void OnDisable()
        {
            if (_runner == null) return;
            _runner.LinePresented -= OnLine;
            _runner.SequenceEnded -= OnEnded;
        }

        /// <summary>
        /// Builds the canvas the view needs and the view inside it.
        ///
        /// DialogueView.BuildRuntime starts with <c>transform as RectTransform</c> and
        /// returns the moment that is null — so a plain GameObject with the component on it
        /// builds nothing at all, silently, and the box never appears. A capture caught it:
        /// the opening faded to black and ran its whole prologue with control taken and no
        /// text on screen.
        ///
        /// The sorting order is above the screen transition's, because the transition covers
        /// the screen with a quad on the camera's near plane and the prologue is *meant* to
        /// be read over that black. Underneath it, the dialogue would be hidden by exactly
        /// the effect it is supposed to accompany.
        /// </summary>
        private static DialogueView BuildView()
        {
            var canvasGo = new GameObject("DialogueCanvas", typeof(RectTransform));
            var canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(canvas, DialogueSortingOrder);

            var viewGo = new GameObject("DialogueView", typeof(RectTransform));
            viewGo.transform.SetParent(canvasGo.transform, false);
            return viewGo.AddComponent<DialogueView>();
        }

        /// <summary>Above the screen wipe, so text drawn over a fade is visible.</summary>
        private const int DialogueSortingOrder = 400;

        private void OnLine(DialogueLine line)
        {
            if (_view == null) return;

            // DialogueLine is a struct, so there is no null to test — an unset line arrives
            // as a default one with no text, and drawing that is an empty box the player
            // has to dismiss.
            if (string.IsNullOrEmpty(line.Text))
            {
                // Except when it carries choices, which is the one shape of this that cannot be
                // walked away from. By the time the line reaches here the runner has already put
                // itself into awaiting-a-choice and refuses an interact-advance, so dropping it
                // leaves the player frozen with Dialogue mode pushed, input taken, and nothing
                // on screen to answer — no error, no way out.
                //
                // No authored line does this today. It is drawn anyway rather than guarded
                // against, because an unlabelled question is ugly and a locked game is not, and
                // it is logged as an error because the line itself is a data fault that wants
                // fixing in the book rather than absorbing here.
                if (line.Choices == null || line.Choices.Length == 0) return;

                Debug.LogError($"[Dialogue] Choice line in sequence '{_runner?.CurrentSequenceId}' " +
                               $"from '{line.SpeakerId}' has no text. Showing its options against an " +
                               "empty prompt so the conversation can still be answered.", this);
            }

            // The runner owns advancing — it is what the interact button is wired to, and it
            // is what knows whether the sequence has more lines. The view is told what to
            // draw and nothing else, so a click on the box and a press of the button take
            // the same path rather than two that can disagree.
            ApplyBackdrop(line);

            if (line.Choices != null && line.Choices.Length > 0)
            {
                var labels = new string[line.Choices.Length];
                for (var i = 0; i < labels.Length; i++) labels[i] = line.Choices[i].Text;

                _view.ShowChoices(line.SpeakerName, line.Text, labels,
                    index => _runner.Choose(index), PortraitFor(line), line.SpeakerSubtitle);
                return;
            }

            _view.Show(line.SpeakerName, line.Text, PortraitFor(line),
                () => _runner.Advance(), line.SpeakerSubtitle);
        }

        private readonly System.Collections.Generic.Dictionary<string, Sprite> _portraits =
            new System.Collections.Generic.Dictionary<string, Sprite>();

        // Scenes that are staged rather than met, keyed by the conversation rather than by who
        // is having it.
        //
        // Keyed on the speaker to begin with, which put the laboratory behind Linden every time
        // he opened his mouth — including standing at the lake counting fish. A room belongs to
        // a moment. The opening happens indoors before the world exists; everything after it
        // happens somewhere the player walked to, and that place is the backdrop.
        private static readonly System.Collections.Generic.Dictionary<string, string> Rooms =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "op_prologue", "study" },
                { "op_prologue_confirm", "study" },
            };

        private Texture2D _worldBlur;

        /// <summary>Whether this conversation already has its backdrop. Cleared when it ends.</summary>
        private bool _backdropGrabbed;

        /// <summary>
        /// Puts something behind the speaker.
        ///
        /// The professor is talking in his own laboratory, so he gets a room: the scene is
        /// staged, the player is not walking around during it, and the world behind him is
        /// whatever corner of the plaza the camera was left pointing at. Everyone else stops
        /// the player where they stand, and the honest background for that is the game itself
        /// thrown out of focus -- it keeps the conversation in the place it is happening
        /// while taking the detail out from behind the text.
        /// </summary>
        private void ApplyBackdrop(DialogueLine line)
        {
            if (_view == null) return;

            var sequence = _runner != null ? _runner.CurrentSequenceId : null;
            if (!string.IsNullOrEmpty(sequence) && Rooms.TryGetValue(sequence, out var room))
            {
                var sprite = Resources.Load<Sprite>("Portraits/Backdrops/" + room);
                if (sprite != null)
                {
                    // A staged room and the composed full-figure framing go together: the
                    // drawn backdrop exists to be stood in, and cropping the figure that is
                    // standing in it leaves a room with a pair of legs in it.
                    _view.SetPortraitFraming(staged: true);
                    _view.SetBackdrop(sprite, null, 1f);
                    return;
                }
            }

            // Everyone else is met where they stand, so they are framed close.
            _view.SetPortraitFraming(staged: false);

            // Grabbed once per conversation, not once per line.
            //
            // Re-capturing on every line re-renders the camera into the target, and the camera
            // is still easing toward its dialogue framing — so each line replaced the backdrop
            // with a slightly different one and the blur visibly slid and widened behind the
            // text. The world does not change during a conversation, so one grab is not merely
            // cheaper, it is the only one that holds still.
            if (!_backdropGrabbed)
            {
                _backdropGrabbed = true;
                _view.SetBackdrop(null, CaptureBlurredWorld(), 0.72f);
            }
        }

        /// <summary>
        /// The game as it looks right now, at a fraction of its resolution.
        ///
        /// The blur is the downsample. Rendering the camera into a small target and letting
        /// the UI stretch it back up is a box filter done by the sampler, which costs one
        /// pass and no shader — and at this size the difference between that and a real
        /// gaussian is not visible behind a text panel.
        /// </summary>
        private Texture2D CaptureBlurredWorld()
        {
            var camera = Camera.main;
            if (camera == null) return null;

            const int width = 160;
            const int height = 90;

            var target = RenderTexture.GetTemporary(width, height, 16);
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;

            camera.targetTexture = target;
            camera.Render();

            RenderTexture.active = target;
            if (_worldBlur == null)
                _worldBlur = new Texture2D(width, height, TextureFormat.RGB24, false)
                    { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            _worldBlur.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            _worldBlur.Apply();

            camera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            RenderTexture.ReleaseTemporary(target);

            return _worldBlur;
        }

        /// <summary>
        /// The speaker's face, cut out of the sheet they walk around in.
        ///
        /// No portrait art exists — the frame the box reserves is 116x172 and nothing has
        /// ever been drawn for it, so the opening was a name and a voice with an empty plate
        /// beside it. Their own front-idle cell is real drawn art of the right person, and a
        /// 32-pixel face scaled up in a pixel game reads as a portrait rather than as a
        /// placeholder. It is also guaranteed to match whoever is actually standing in the
        /// world, which a separately drawn portrait would not be.
        ///
        /// Cached per key: this is called once per line, and building a Sprite allocates.
        /// </summary>
        private Sprite PortraitFor(DialogueLine line)
        {
            var key = ResolvePersonKey(line);
            if (string.IsNullOrEmpty(key)) return null;
            if (_portraits.TryGetValue(key, out var cached)) return cached;

            // The drawn illustration first. DialoguePortraits loads a half-body from
            // Resources/Portraits and is what the box is really designed around; the
            // sprite-sheet face below is what shows until that art exists, so neither
            // path has to wait for the other.
            // Asked for by the normalised key, not the raw one. The book writes PortraitKey as
            // the speaker id — npc_professor_01 — and the drawn portraits are filed under the
            // role, so looking up the raw value missed every one of them and quietly fell
            // through to the walking sprite. The same mistake as the world art, one lookup
            // along: the key has to be normalised wherever it is used, not where it is read.
            // Through the same alias table the world sprite uses, so the box and the world
            // cannot disagree about who a speaker is.
            Sprite portrait = null;
            foreach (var candidate in PersonSpriteLibrary.ArtKeysFor(key))
            {
                portrait = DialoguePortraits.For(candidate);
                if (portrait != null) break;
            }
            if (portrait == null) portrait = DialoguePortraits.For(line.PortraitKey);
            if (portrait == null) portrait = DialoguePortraits.For(line.SpeakerName);

            if (portrait != null)
            {
                _portraits[key] = portrait;
                return portrait;
            }

            var entry = PersonSpriteLibrary.Shared.Find(key);
            var clip = entry?.frontIdle;
            if (clip != null && clip.IsValid)
            {
                var texture = PersonSpriteLibrary.Shared.Texture(clip.texture);
                if (texture != null)
                {
                    var frame = clip.sequence[0];
                    var cellW = texture.width / Mathf.Max(1, clip.columns);
                    var cellH = texture.height / Mathf.Max(1, clip.rows);
                    var column = frame % clip.columns;
                    var row = frame / clip.columns;

                    // Row 0 is the top of the sheet; Sprite rects count up from the bottom.
                    var rect = new Rect(column * cellW,
                                        texture.height - (row + 1) * cellH,
                                        cellW, cellH);
                    portrait = Sprite.Create(texture, rect, new Vector2(0.5f, 0.5f), cellH);
                    portrait.name = "~Portrait_" + key;
                }
            }

            _portraits[key] = portrait;
            return portrait;
        }

        /// <summary>
        /// Turns a speaker id into the key the sprite library uses.
        ///
        /// The dialogue names speakers as npc_professor_01 — a role and an instance — while
        /// the art is keyed on the role alone. PortraitKey wins when the book sets one,
        /// which is the escape hatch for a speaker whose face is not their own sprite.
        /// </summary>
        private static string ResolvePersonKey(DialogueLine line)
        {
            // Normalised whichever field it came from. PortraitKey used to be returned raw,
            // and the book fills it with the SpeakerId — npc_professor_01 — so the lookup
            // asked the sprite library for a key it does not have and the plate stayed empty
            // while the name and the line beside it were correct.
            var id = !string.IsNullOrEmpty(line.PortraitKey) ? line.PortraitKey : line.SpeakerId;
            if (string.IsNullOrEmpty(id)) return null;

            if (id.StartsWith("npc_", System.StringComparison.Ordinal)) id = id.Substring(4);
            if (id.StartsWith("prop_", System.StringComparison.Ordinal)) id = id.Substring(5);

            // Drop a trailing instance number: the cast names speakers per instance and the
            // art is drawn per role.
            var tail = id.LastIndexOf('_');
            if (tail > 0 && int.TryParse(id.Substring(tail + 1), out _)) id = id.Substring(0, tail);
            return id;
        }

        private void ResetBackdrop() => _backdropGrabbed = false;

        private void OnEnded(string sequenceId)
        {
            if (_view != null) _view.Close();
            ResetBackdrop();
        }
    }
}
