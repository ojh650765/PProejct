using System;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// Finding an opponent, and looking at them before the fight.
    ///
    /// <b>Why this is a screen and not a spinner.</b> Everything between pressing 온라인 대전
    /// and the first turn is dead time the player did not ask for, and the only honest way to
    /// spend it is to make the wait legible and then make the payoff worth having arrived at.
    /// So it is three beats: the queue says how long it has been and can always be left; the
    /// pairing lands as an event; and the VS board shows both teams, because a PvP battle is
    /// the first moment in this game where the other six creatures were drawn by a person.
    ///
    /// <b>Every state the network can be in has a screen.</b> <see cref="PvpSession.Phase"/> has
    /// seven values and all seven are rendered — including the two that are somebody else's
    /// fault, <c>OpponentLeft</c> and <c>Failed</c>. A matchmaking UI that only draws the happy
    /// path leaves the player looking at a spinner that will never stop, which is the specific
    /// failure this screen is shaped to avoid.
    ///
    /// <b>The lobby socket closing is not a disconnection.</b> That is worth knowing here as
    /// well as in <see cref="PvpSession"/>: the Worker pairs you and then closes the queue
    /// socket on purpose, so <c>Matched</c> flickers past on the way to the match room and must
    /// not be drawn as an error.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MatchmakingPanel : MonoBehaviour
    {
        /// <summary>Raised when the panel closes without starting a battle.</summary>
        public Action Closed;

        /// <summary>
        /// Raised when the player commits to the fight. The context — match id, seed, opponent
        /// roster, which side you are — is on <see cref="PvpSession"/> rather than passed here,
        /// so the battle can read it without this screen and the battle knowing about each
        /// other.
        /// </summary>
        public Action Confirmed;

        public bool IsOpen { get; private set; }

        private PvpSession _pvp;
        private RectTransform _body;
        private TextMeshProUGUI _heading;
        private TextMeshProUGUI _detail;
        private PvpSession.Phase _drawn = (PvpSession.Phase)(-1);
        private float _redrawAt;

        private void Awake() => gameObject.SetActive(false);

        public void Open()
        {
            gameObject.SetActive(true);
            IsOpen = true;

            _pvp = PvpSession.Ensure();
            _pvp.Changed += OnPhaseChanged;

            BuildChrome();
            _drawn = (PvpSession.Phase)(-1);
            _pvp.FindMatch();
            Redraw();
        }

        public void Close()
        {
            if (_pvp != null)
            {
                _pvp.Changed -= OnPhaseChanged;
                // Leaving the screen leaves the queue. A player who backed out and is still
                // silently matchable would be paired with somebody who then waits for a client
                // that is showing a title screen.
                if (_pvp.State != PvpSession.Phase.Ready) _pvp.Cancel();
            }

            IsOpen = false;
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        private void OnPhaseChanged() => _drawn = (PvpSession.Phase)(-1);

        private void Update()
        {
            if (!IsOpen || _pvp == null) return;

            // The queue timer is the only thing that changes without a phase change, so it gets
            // its own low-frequency redraw rather than rebuilding the screen every frame.
            if (_drawn != _pvp.State) Redraw();
            else if (_pvp.State == PvpSession.Phase.Queued && Time.unscaledTime >= _redrawAt) UpdateTimer();

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame) { Close(); return; }

            if ((keyboard.enterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame)
                && _pvp.State == PvpSession.Phase.Ready)
            {
                Commit();
            }
        }

        // --- Chrome ---------------------------------------------------------------------

        private void BuildChrome()
        {
            var root = (RectTransform)transform;
            UiBuilder.ClearChildren(root);
            UiBuilder.Stretch(root);

            var scrim = UiBuilder.Backdrop("Scrim", root, null,
                new Color(UiPalette.Scrim.r, UiPalette.Scrim.g, UiPalette.Scrim.b, 0.94f), true);
            UiBuilder.Stretch(scrim.rectTransform);

            _heading = UiBuilder.Text("Heading", root, "", UiTextRole.Metric,
                UiPalette.TextPrimary, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_heading.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -110f), new Vector2(-160f, 120f));

            _detail = UiBuilder.Text("Detail", root, "", UiTextRole.Body,
                UiPalette.TextSecondary, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_detail.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -238f), new Vector2(-160f, 44f));

            _body = UiBuilder.Rect("Body", root, false);
            UiBuilder.Anchor(_body, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -20f), new Vector2(1500f, 520f));
        }

        private void Redraw()
        {
            _drawn = _pvp.State;
            UiBuilder.ClearChildren(_body);

            switch (_pvp.State)
            {
                case PvpSession.Phase.Connecting:
                    Say(Loc.Pick("Connecting", "연결 중"),
                        Loc.Pick("Reaching the match server…", "대전 서버에 접속하고 있어요…"));
                    BuildCancel();
                    break;

                case PvpSession.Phase.Queued:
                    Say(Loc.Pick("Searching", "상대를 찾는 중"), "");
                    UpdateTimer();
                    BuildSearching();
                    BuildCancel();
                    break;

                case PvpSession.Phase.Matched:
                    Say(Loc.Pick("Found", "상대를 찾았다!"),
                        Loc.Pick("Joining the match…", "대전에 참가하는 중…"));
                    break;

                case PvpSession.Phase.Ready:
                    Say(Loc.Pick("VS", "VS"), _pvp.OpponentName);
                    BuildVersus();
                    break;

                case PvpSession.Phase.OpponentLeft:
                    Say(Loc.Pick("Opponent left", "상대가 나갔어요"),
                        Loc.Pick("Nothing was lost. Try again?", "잃은 건 없어요. 다시 찾을까요?"));
                    BuildRetry();
                    break;

                case PvpSession.Phase.Failed:
                    Say(Loc.Pick("Could not match", "매칭 실패"), PvpSession.Explain(_pvp.Error));
                    BuildRetry();
                    break;

                default:
                    Say(Loc.Pick("Online battle", "온라인 대전"), "");
                    BuildRetry();
                    break;
            }
        }

        private void UpdateTimer()
        {
            _redrawAt = Time.unscaledTime + 0.25f;
            var seconds = Mathf.FloorToInt(_pvp.QueuedFor);
            var clock = $"{seconds / 60:00}:{seconds % 60:00}";
            if (_detail != null)
            {
                _detail.text = Loc.Pick($"Waiting for an opponent   {clock}",
                                        $"상대를 기다리는 중   {clock}");
            }
        }

        private void Say(string heading, string detail)
        {
            if (_heading != null) _heading.text = heading ?? "";
            if (_detail != null) _detail.text = detail ?? "";
        }

        // --- The three bodies -------------------------------------------------------------

        /// <summary>A ball that breathes while the queue runs. The only motion on this screen.</summary>
        private void BuildSearching()
        {
            var ball = UiBuilder.Image("Ball", _body, UiSprites.Disc(), UiPalette.TextMuted,
                Image.Type.Simple);
            UiBuilder.Anchor(ball.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(120f, 120f));

            var ring = UiBuilder.Image("Ring", _body, UiSprites.DiscRing(256, 6), UiPalette.ScannerCyan,
                Image.Type.Simple);
            UiBuilder.Anchor(ring.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(120f, 120f));

            // Loops for as long as the screen is up. Killed with the object when the body is
            // cleared, which is what ClearChildren does on the next phase.
            UiTween.Run(1.4f, t =>
            {
                if (ring == null) return;
                var scale = Mathf.Lerp(1f, 2.4f, t);
                ring.rectTransform.localScale = Vector3.one * scale;
                ring.color = new Color(UiPalette.ScannerCyan.r, UiPalette.ScannerCyan.g,
                    UiPalette.ScannerCyan.b, 1f - t);
            }, Ease.OutCubic, 0f, true, () => { if (IsOpen && _pvp.State == PvpSession.Phase.Queued) BuildSearchingLoop(ring); });
        }

        private void BuildSearchingLoop(Image ring)
        {
            if (ring == null || !IsOpen || _pvp.State != PvpSession.Phase.Queued) return;
            UiTween.Run(1.4f, t =>
            {
                if (ring == null) return;
                ring.rectTransform.localScale = Vector3.one * Mathf.Lerp(1f, 2.4f, t);
                ring.color = new Color(UiPalette.ScannerCyan.r, UiPalette.ScannerCyan.g,
                    UiPalette.ScannerCyan.b, 1f - t);
            }, Ease.OutCubic, 0f, true, () => BuildSearchingLoop(ring));
        }

        /// <summary>Both teams, side by side. The reason the wait was worth it.</summary>
        private void BuildVersus()
        {
            var session = OnlineSession.Instance;
            var mine = session != null ? session.Roster : Array.Empty<RosterEntry>();

            BuildTeam(_body, mine,
                session != null && !string.IsNullOrEmpty(session.TrainerName)
                    ? session.TrainerName
                    : Loc.Pick("You", "나"),
                UiPalette.ScannerCyan, left: true);

            BuildTeam(_body, _pvp.OpponentRoster,
                string.IsNullOrEmpty(_pvp.OpponentName) ? Loc.Pick("Opponent", "상대") : _pvp.OpponentName,
                UiPalette.Negative, left: false);

            var start = UiBuilder.Rect("Start", _body, false);
            UiBuilder.Anchor(start, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, -96f), new Vector2(360f, 76f));
            var slab = UiBuilder.Panel("Slab", start, UiPalette.Positive, 18);
            UiBuilder.Stretch(slab.rectTransform);
            var label = UiBuilder.Text("Label", start, Loc.Pick("Battle!", "대전 시작!"),
                UiTextRole.Heading, UiPalette.TextOnAccent, TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);
            UiBuilder.Button("Take", start, slab, Commit);
        }

        private void BuildTeam(Transform parent, RosterEntry[] roster, string trainer, Color accent,
                               bool left)
        {
            var column = UiBuilder.Rect(left ? "Mine" : "Theirs", parent, false);
            var anchor = new Vector2(left ? 0f : 1f, 0.5f);
            UiBuilder.Anchor(column, anchor, anchor, anchor,
                new Vector2(left ? 40f : -40f, 30f), new Vector2(640f, 340f));

            var name = UiBuilder.Text("Trainer", column, trainer, UiTextRole.Title, accent,
                left ? TextAlignmentOptions.Left : TextAlignmentOptions.Right);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), Vector2.zero, new Vector2(0f, 64f));

            var grid = UiBuilder.Rect("Team", column, false);
            UiBuilder.Anchor(grid, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -76f), new Vector2(0f, 240f));
            UiBuilder.Grid(grid, new Vector2(96f, 112f), new Vector2(10f, 10f), 6);

            for (var slot = 0; slot < 6; slot++)
            {
                var entry = Find(roster, slot);
                var card = UiBuilder.Rect("Slot" + slot, grid, false);

                var back = UiBuilder.Panel("Back", card,
                    entry != null ? UiPalette.SurfaceRaised : UiPalette.SurfaceSunken, 12);
                UiBuilder.Stretch(back.rectTransform);

                if (entry == null) continue;

                {
                    // CreatureThumbnail, not ICreatureArtRegistry: nothing registers a real art
                    // registry outside a battle, so GetPortrait is null on every menu screen.
                    var portrait = CreatureThumbnail.Front(entry.speciesId);
                    if (portrait != null)
                    {
                        var image = UiBuilder.Image("Portrait", card, portrait, Color.white,
                            Image.Type.Simple);
                        UiBuilder.Anchor(image.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                            new Vector2(0.5f, 1f), new Vector2(0f, -6f), new Vector2(76f, 76f));
                        image.preserveAspect = true;
                    }
                }

                var level = UiBuilder.Text("Lv", card, "Lv " + entry.level, UiTextRole.Caption,
                    UiPalette.TextSecondary, TextAlignmentOptions.Center);
                UiBuilder.Anchor(level.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(-8f, 26f));
            }
        }

        private static RosterEntry Find(RosterEntry[] roster, int slot)
        {
            if (roster == null) return null;
            foreach (var entry in roster) if (entry != null && entry.slot == slot) return entry;
            return null;
        }

        // --- Buttons ----------------------------------------------------------------------

        private void BuildCancel() => BuildButton(Loc.Pick("Cancel", "취소"), UiPalette.SurfaceRaised,
            UiPalette.TextPrimary, Close);

        private void BuildRetry()
        {
            BuildButton(Loc.Pick("Search again", "다시 찾기"), UiPalette.Info, UiPalette.TextOnAccent,
                () => { _pvp.FindMatch(); _drawn = (PvpSession.Phase)(-1); });

            var back = UiBuilder.Rect("Back", _body, false);
            UiBuilder.Anchor(back, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, -170f), new Vector2(280f, 60f));
            var slab = UiBuilder.Panel("Slab", back, UiPalette.SurfaceRaised, 14);
            UiBuilder.Stretch(slab.rectTransform);
            var label = UiBuilder.Text("Label", back, Loc.Pick("Back", "뒤로"), UiTextRole.Body,
                UiPalette.TextPrimary, TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);
            UiBuilder.Button("Take", back, slab, Close);
        }

        private void BuildButton(string text, Color accent, Color ink, Action onClick)
        {
            var button = UiBuilder.Rect("Button_" + text, _body, false);
            UiBuilder.Anchor(button, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, -96f), new Vector2(280f, 64f));

            var slab = UiBuilder.Panel("Slab", button, accent, 14);
            UiBuilder.Stretch(slab.rectTransform);

            var label = UiBuilder.Text("Label", button, text, UiTextRole.Body, ink,
                TextAlignmentOptions.Center);
            UiBuilder.Stretch(label.rectTransform);

            UiBuilder.Button("Take", button, slab, onClick);
        }

        /// <summary>
        /// Hands off to the battle.
        ///
        /// The screen closes WITHOUT cancelling the session — that is the one call path where
        /// the queue must survive, because the match is the thing we were queuing for and
        /// <see cref="PvpSession"/> is what the battle reads its opponent from.
        /// </summary>
        private void Commit()
        {
            if (_pvp == null || _pvp.State != PvpSession.Phase.Ready) return;

            _pvp.Changed -= OnPhaseChanged;
            IsOpen = false;
            gameObject.SetActive(false);
            Confirmed?.Invoke();
        }
    }
}
