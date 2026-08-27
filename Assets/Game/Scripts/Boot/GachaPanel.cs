using System;
using System.Collections;
using System.Collections.Generic;
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
    /// The gacha: where you get in, and what it looks like when you pull.
    ///
    /// <b>진입.</b> One row on the title screen, disabled with a reason on it until there is a
    /// server and an account — a gacha reached from a menu that then says "sign in first" is a
    /// door that should not have been open. It opens on the team you already have, so the
    /// screen is worth visiting when you are not pulling, and the roll is a button on it rather
    /// than the thing that happens when you arrive.
    ///
    /// <b>연출.</b> The presentation is the product here — the roll itself is one HTTP call and
    /// six rows in a database, and everything a player feels about it happens in the four
    /// seconds afterwards. So the reveal is built as a sequence rather than a list appearing:
    ///
    /// <list type="number">
    /// <item>The screen drops to deep navy, speed lines start turning behind it, and a ball
    /// falls in, overshoots, and <b>squashes on landing</b> — weight before anything else.</item>
    /// <item>It shakes — <b>once for a common, four times for a legendary</b>. This is the whole
    /// trick: the player learns within two pulls that more shaking means something better, and
    /// from then on the tension is real because the information is real. The rarity comes from
    /// the server, so what the shaking promises is what arrives. Each shake is wider and slower
    /// than the last and the light behind the ball comes up with it, and above epic the beat
    /// before the final shake is stretched — the pause is the tension, not the motion.</item>
    /// <item>The burst: a white flash, a starburst, rings thrown outward, sparkles flung in
    /// every direction and a shake of the whole stage. Every one of those five scales with the
    /// rank, so a legendary fills the screen and a common is a pop.</item>
    /// <item>The creature slams in over the flash with an overshoot, on a rarity ribbon, held
    /// until the player advances or a beat passes — longer for the rare ones, because that is
    /// the one they want to look at. Epic and legendary get confetti.</item>
    /// </list>
    ///
    /// <b>Skippable, always.</b> A reveal that cannot be skipped is a reveal that is charming
    /// once and an obstacle every time after. Space or click advances a pull; the skip button
    /// jumps to the finished team.
    ///
    /// Motion respects <see cref="UiTween.MotionEnabled"/>, so the accessibility switch that
    /// turns off UI animation turns this off too rather than being a setting the loudest screen
    /// in the game ignores.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class GachaPanel : MonoBehaviour
    {
        /// <summary>Raised when the panel closes, so the menu behind it can redraw.</summary>
        public Action Closed;

        public bool IsOpen { get; private set; }

        private const int TeamSize = 6;

        /// <summary>
        /// The counts the buttons offer.
        ///
        /// The user's rule: 무조건 6개가 아니라, 1개도 뽑을 수도 있고 ~n개를 뽑기 가능한거지.
        /// One is the honest unit — you should be able to spend exactly what you have — five is
        /// the everyday multi, and ten is what a saved-up purse is for. Anything the server
        /// will not allow is clamped away in <see cref="RefreshPullStepper"/> rather than
        /// discovered as a refusal.
        /// </summary>
        /// <summary>Parts of the count stepper the refresh has to reach back into.</summary>
        private TextMeshProUGUI _pullCount;
        private Button _pullDown;
        private Button _pullUp;
        private UiPane _pullDownPane;
        private UiPane _pullUpPane;
        private TextMeshProUGUI _pullDownLabel;
        private TextMeshProUGUI _pullUpLabel;

        private const float CardWidth = 216f;
        private const float CardHeight = 300f;

        private RectTransform _teamRoot;
        private RectTransform _idleMark;
        private RectTransform _revealRoot;
        private TextMeshProUGUI _status;
        private RectTransform _statusPill;
        private TextMeshProUGUI _oddsText;
        private Button _rollButton;
        private TextMeshProUGUI _rollLabel;
        private UiPane _rollPane;

        private TextMeshProUGUI _purseLabel;

        /// <summary>How many the next press draws. Sticky across rolls, so a ten-pull run is one press each.</summary>
        private int _pulls = 1;

        /// <summary>The last roll, kept only so the grid can show what just arrived.</summary>
        private GachaPull[] _lastPulls = Array.Empty<GachaPull>();

        private Coroutine _reveal;
        private bool _advance;
        private bool _skip;


        private void Awake() => gameObject.SetActive(false);

        public void Open()
        {
            OnlineSession.Ensure();
            gameObject.SetActive(true);
            IsOpen = true;
            UiSound.MenuOpen();

            // Arriving shows the party, not whatever was drawn last time the screen was open.
            // The last roll is what the player wants to look at in the seconds after a reveal;
            // a session later it is just a stale list standing where the team should be.
            _lastPulls = Array.Empty<GachaPull>();

            Build();
            Refresh();
        }

        public void Close()
        {
            if (_reveal != null) { StopCoroutine(_reveal); _reveal = null; }
            IsOpen = false;
            UiSound.MenuClose();
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        // --- The standing screen --------------------------------------------------------------

        private void Build()
        {
            var root = (RectTransform)transform;
            UiBuilder.ClearChildren(root);
            UiBuilder.Stretch(root);

            // The sky itself draws nothing that takes raycasts — every generated Image is built
            // with raycastTarget off — so the panel needs one invisible catcher underneath it,
            // or clicks land on the title screen it is covering.
            var blocker = UiBuilder.Backdrop("Blocker", root, null, new Color(0f, 0f, 0f, 0f), true);
            UiBuilder.Stretch(blocker.rectTransform);

            // The same lit navy field as the title, with the key light pulled violet: the
            // gacha is a different room in the same building, not a different building.
            UiJuice.Backdrop(root, UiPalette.AceViolet, UiPalette.AceCyan);

            var safe = UiBuilder.SafeArea(root, 96f, 56f);

            var mark = UiJuice.Ball(safe, 84f);
            UiBuilder.Anchor(mark, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0.5f, 0.5f),
                new Vector2(46f, -60f), new Vector2(84f, 84f));
            UiIdle.Attach(mark, UiIdleMode.Sway, 8f, 3.1f);

            var title = UiBuilder.Text("Title", safe, Loc.Pick("Gacha", "가챠"), UiTextRole.Metric,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(104f, 0f), new Vector2(-104f, 124f));

            // The rates, on the screen that uses them. On a dark pill because the line is long
            // and small, and small text on a sky is the first thing to become unreadable.
            var oddsPill = UiBuilder.Rect("Odds", safe, false);
            UiBuilder.Anchor(oddsPill, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -134f), new Vector2(1180f, 50f));
            var oddsBack = UiBuilder.Image("Pill", oddsPill, UiSprites.Pill(42),
                UiPalette.AceGlass.WithAlpha(0.72f));
            UiBuilder.Stretch(oddsBack.rectTransform);
            var oddsRim = UiBuilder.Image("Rim", oddsPill, UiSprites.Frame(24, 2), UiPalette.AceRim);
            UiBuilder.Stretch(oddsRim.rectTransform);
            _oddsText = UiBuilder.Text("Text", oddsPill, OddsLine(), UiTextRole.Caption,
                UiPalette.AceTextDim, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_oddsText.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-40f, 0f));

            BuildPurse(safe);

            _teamRoot = UiBuilder.Rect("Team", safe, false);
            UiBuilder.Anchor(_teamRoot, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, 4f), new Vector2(1406f, CardHeight));
            UiBuilder.Grid(_teamRoot, new Vector2(CardWidth, CardHeight), new Vector2(22f, 22f), 6);

            // What the middle of the screen is before the first pull of a visit.
            //
            // It used to be the player's team, which read as a result that had already
            // happened. That was removed and the band became genuinely empty -- and an empty
            // band is not restraint, it is a hole: two thirds of the screen with the roll
            // button marooned at the bottom of it.
            //
            // So: the ball the reveal bursts from, sitting there dim and breathing. It is the
            // same art at the same place, which makes the roll read as this waking up rather
            // than as a cutscene arriving from nowhere. No caption -- the screen is titled
            // 가챠 and the button states the price; a sentence here would only be explaining
            // a space nobody was confused by.
            _idleMark = UiBuilder.Rect("Idle", safe, false);
            UiBuilder.Anchor(_idleMark, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, 4f), new Vector2(300f, 300f));
            _idleMark.SetAsFirstSibling();
            var idleBall = UiJuice.Ball(_idleMark, 300f);
            UiBuilder.Anchor(idleBall, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(300f, 300f));
            UiIdle.Attach(idleBall, UiIdleMode.Bob, 12f, 3.8f);
            // Dim enough to be scenery. At full strength it competes with the roll button for
            // the eye, and the roll button is the thing to press.
            SetAlpha(_idleMark.gameObject, 0.22f);

            // Stacked up the screen from the bottom edge, and the numbers have to stay in
            // that order: the roll button occupies 30..118, the pull chips 128..190, and this
            // sits above both. It used to sit at 150, which put it straight through the chips.
            _statusPill = UiBuilder.Rect("Status", safe, false);
            UiBuilder.Anchor(_statusPill, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 206f), new Vector2(940f, 54f));
            var statusBack = UiBuilder.Image("Pill", _statusPill, UiSprites.Pill(46),
                UiPalette.AceGlass.WithAlpha(0.72f));
            UiBuilder.Stretch(statusBack.rectTransform);
            var statusRim = UiBuilder.Image("Rim", _statusPill, UiSprites.Frame(26, 2), UiPalette.AceRim);
            UiBuilder.Stretch(statusRim.rectTransform);
            _status = UiBuilder.Text("Text", _statusPill, "", UiTextRole.Secondary,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_status.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-36f, -8f));
            // Built hidden, because it is built empty. Say() is what shows it, and until
            // something has been said there is no message -- only a capsule. That capsule was
            // visible on arrival for every account with no free pulls left: a lit, rimmed,
            // 940-wide box in the middle of the screen containing nothing.
            _statusPill.gameObject.SetActive(false);

            var roll = UiBuilder.Rect("Roll", safe, false);
            UiBuilder.Anchor(roll, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(-150f, 30f), new Vector2(420f, 88f));
            var rollGlow = UiBuilder.Image("Glow", roll, UiSprites.Shadow(22, 34),
                UiPalette.AceRed.WithAlpha(0.34f));
            UiBuilder.Stretch(rollGlow.rectTransform, -20f);
            _rollPane = UiJuice.Pane("Pane", roll, UiPalette.AceRed.WithAlpha(0.92f), 20, true, true, true,
                UiPalette.AceRed.WithAlpha(0.6f), 88);
            _rollLabel = UiBuilder.Text("Label", roll, "", UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_rollLabel.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-28f, -16f));
            UiButtonMotion.Attach(roll, 22);
            _rollButton = UiBuilder.Button("Take", roll, _rollPane.Fill, Roll);
            // The one thing on the screen the player is here to press, and the only thing on it
            // that breathes.
            UiIdle.Attach(roll, UiIdleMode.Pulse, 0.022f, 1.9f);

            var close = UiBuilder.Rect("Close", safe, false);
            UiBuilder.Anchor(close, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(180f, 30f), new Vector2(240f, 88f));
            var closePane = UiJuice.Pane("Pane", close, UiPalette.AceGlass.WithAlpha(0.72f), 20,
                true, true, true, UiPalette.AceRim, 88);
            var closeLabel = UiBuilder.Text("Label", close, Loc.Pick("Close", "닫기"),
                UiTextRole.Body, UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(closeLabel.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-28f, -16f));
            UiButtonMotion.Attach(close, 16);
            UiBuilder.Button("Take", close, closePane.Fill, Close);

            BuildPullStepper(safe);

            UiJuice.PopIn(oddsPill, 0.06f, new Vector2(0f, 60f), 0.4f);
            UiJuice.PopIn(roll, 0.22f, new Vector2(0f, -90f), 0.42f);
            UiJuice.PopIn(close, 0.27f, new Vector2(0f, -90f), 0.42f);

            // Built last so it sits above everything, and left empty until a roll fills it.
            _revealRoot = UiBuilder.Rect("Reveal", root, false);
            UiBuilder.Stretch(_revealRoot);
            _revealRoot.gameObject.SetActive(false);
        }

        private void Refresh()
        {
            var session = OnlineSession.Instance;

            // The grid shows the LAST ROLL while there is one, and the party otherwise.
            //
            // With a collection there is no longer one team this screen is about: what the
            // player wants to see the instant a reveal ends is what they just got, and what they
            // want to see on arriving is who is currently fighting for them. Everything else
            // they own lives on 내 포켓몬, which is the screen built for a list that grows.
            var showing = _lastPulls != null && _lastPulls.Length > 0
                ? AsCards(_lastPulls)
                : Array.Empty<RosterEntry>();

            UiBuilder.ClearChildren(_teamRoot);

            // Exactly what was drawn, and nothing at all before that.
            //
            // This band used to fall back to the player's team, which made the screen look like
            // it was reporting a pull whenever it was opened -- six creatures laid out as cards,
            // indistinguishable from a result, hours after the last one was drawn. The team has
            // its own screen now. Here, cards mean "you just got these" or there are no cards.
            var afterRoll = _lastPulls != null && _lastPulls.Length > 0;
            var cells = afterRoll
                ? Mathf.Min(showing.Length, session != null ? session.MaxPulls : 10)
                : 0;

            // Ten pulls do not fit one row of six, so the grid grows a second row and the block
            // is re-centred on it. The band between the odds pill and the status pill is about
            // 690 reference units; two 300pt rows and a 22pt gutter is 622.
            var rows = Mathf.Max(1, Mathf.CeilToInt(cells / 6f));
            _teamRoot.sizeDelta = new Vector2(1406f, rows * CardHeight + (rows - 1) * 22f);

            // Before the first pull of a visit the band is simply empty.
            //
            // It carried a caption for one revision -- "뽑은 포켓몬이 여기에 나와요" -- and that was
            // a manual sentence explaining a space nobody was confused by: the screen is titled
            // 가챠, the odds are stated above it and the button below says 1회 뽑기 · 500. Writing
            // out what the middle is for is the kind of line that reads as translated, so the
            // band keeps its height and says nothing.
            if (cells == 0) _teamRoot.sizeDelta = new Vector2(1406f, CardHeight);
            if (_idleMark != null) _idleMark.gameObject.SetActive(cells == 0);

            for (var slot = 0; slot < cells; slot++)
            {
                BuildCard(_teamRoot, slot < showing.Length ? showing[slot] : null, slot);
            }

            RefreshPurse();
            RefreshPullStepper();

            var affordable = session != null && session.AffordablePulls >= _pulls;
            var cost = CostOf(session, _pulls);

            _rollLabel.text = !affordable
                ? Loc.Pick("Not enough coins", "코인이 부족해요")
                : cost <= 0
                    ? Loc.Pick($"Draw {_pulls} · free", $"{_pulls}회 뽑기 · 무료")
                    : Loc.Pick($"Draw {_pulls} · {cost:N0}", $"{_pulls}회 뽑기 · {cost:N0}");

            if (_rollButton != null) _rollButton.interactable = affordable;
            if (_rollPane.IsValid)
            {
                UiJuice.Recolour(_rollPane,
                    affordable ? UiPalette.AceRed.WithAlpha(0.92f) : UiPalette.AceGlass.WithAlpha(0.4f));
            }

            // The idle line says one thing and only when it is worth saying.
            //
            // It used to explain the economy every time the screen was opened -- where coins come
            // from, what a duplicate turns into -- which is a manual read out at somebody who has
            // opened this screen fifty times. Free pulls are the exception: that is a fact about
            // the player's account right now, and it expires.
            if (_status != null && string.IsNullOrEmpty(_status.text))
            {
                var free = session != null ? session.FreePulls : 0;
                // Said either way: Say("") is what takes the pill back down, and skipping the
                // call is what left it standing there empty.
                Say(free > 0
                    ? Loc.Pick($"{free} free pulls left.", $"무료 뽑기 {free}회가 남았어요.")
                    : "");
            }
        }

        /// <summary>Coins the given number of pulls would cost, free pulls taken off first.</summary>
        private static int CostOf(OnlineSession session, int pulls)
        {
            if (session == null) return 0;
            var paid = Mathf.Max(0, pulls - session.FreePulls);
            return paid * session.PullCost;
        }

        /// <summary>
        /// The last roll as cards.
        ///
        /// A duplicate is shown as the creature it was, marked, rather than hidden: the shard it
        /// became is the only route to 돌파, so the screen says so instead of looking like a
        /// pull that did nothing.
        /// </summary>
        private static RosterEntry[] AsCards(GachaPull[] pulls)
        {
            if (pulls == null) return Array.Empty<RosterEntry>();
            var cards = new RosterEntry[pulls.Length];
            for (var i = 0; i < pulls.Length; i++)
            {
                var pull = pulls[i];
                cards[i] = new RosterEntry
                {
                    speciesId = pull.speciesId,
                    level = pull.level,
                    experience = pull.level <= 1 ? 0 : pull.level * pull.level * pull.level,
                    rarity = pull.rarity,
                    slot = pull.slot,
                    // Borrowed as the "this was a duplicate" mark. The card only ever draws the
                    // last roll, where a shard count of its own would mean nothing.
                    shards = pull.duplicate ? 1 : 0,
                };
            }
            return cards;
        }

        /// <summary>
        /// One team slot.
        ///
        /// Every card leans a degree or so, alternating across the row. Six identical
        /// rectangles in a line read as a spreadsheet; six that each sit slightly differently
        /// read as six things somebody laid out on a table, and the whole grid stops being a
        /// grid without any of them becoming hard to compare.
        /// </summary>
        private void BuildCard(Transform parent, RosterEntry entry, int slot)
        {
            var cell = UiBuilder.Rect("Slot_" + slot, parent, false);

            var card = UiBuilder.Rect("Tilt", cell);
            card.localRotation = Quaternion.Euler(0f, 0f, (slot - 2.5f) * 0.9f);

            var filled = entry != null;
            var rank = filled ? RankOf(entry.rarity) : 0;
            var accent = filled ? UiPalette.Rarity(rank) : UiPalette.AceRim;

            // A filled slot is rimmed in its own tier colour, which is what makes the grid
            // readable at a glance; an empty one is glass at a third of the opacity.
            UiJuice.Pane("Pane", card,
                UiPalette.AceGlass.WithAlpha(filled ? 0.78f : 0.34f), 18, true, true, true,
                filled ? accent.WithAlpha(0.7f) : UiPalette.AceRim.WithAlpha(0.08f), 200);

            if (!filled)
            {
                var ghost = UiBuilder.Image("Ghost", card, UiSprites.BallGlyph(192, 11),
                    UiPalette.AceTextFaint.WithAlpha(0.16f), Image.Type.Simple);
                UiBuilder.Anchor(ghost.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(0f, 26f), new Vector2(128f, 128f));

                var empty = UiBuilder.Text("Empty", card, "—", UiTextRole.Title,
                    UiPalette.AceTextFaint, TextAlignmentOptions.Center);
                UiBuilder.Anchor(empty.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(-16f, 68f));

                UiJuice.PopIn(card, 0.05f + slot * 0.04f, new Vector2(0f, -50f), 0.4f);
                return;
            }

            // A rarity-coloured halo behind the portrait: the tier has to be readable from the
            // grid at a glance, and a coloured rim alone is two pixels of information.
            var halo = UiBuilder.Image("Halo", card, UiSprites.Glow(192, 1.7f),
                accent.WithAlpha(0.55f), Image.Type.Simple);
            UiBuilder.Anchor(halo.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0.5f, 0.5f), new Vector2(0f, -96f), new Vector2(214f, 214f));

            var portrait = Portrait(entry.speciesId);
            if (portrait != null)
            {
                var image = UiBuilder.Image("Portrait", card, portrait, Color.white, Image.Type.Simple);
                UiBuilder.Anchor(image.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0.5f, 0.5f), new Vector2(0f, -96f), new Vector2(146f, 146f));
                image.preserveAspect = true;
            }
            else
            {
                var stand = UiBuilder.Image("Stand", card, UiSprites.BallGlyph(192, 12),
                    accent.WithAlpha(0.75f), Image.Type.Simple);
                UiBuilder.Anchor(stand.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0.5f, 0.5f), new Vector2(0f, -96f), new Vector2(126f, 126f));
            }

            var name = UiBuilder.Text("Name", card, SpeciesName(entry.speciesId), UiTextRole.Secondary,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 118f), new Vector2(-16f, 36f));

            var banner = UiBuilder.Rect("Rarity", card, false);
            UiBuilder.Anchor(banner, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 84f), new Vector2(168f, 32f));
            var bannerFace = UiBuilder.Image("Face", banner, UiSprites.Pill(26), accent);
            UiBuilder.Stretch(bannerFace.rectTransform);
            var rarity = UiBuilder.Text("Text", banner, RarityLabel(entry.rarity), UiTextRole.Caption,
                UiPalette.AceInk, TextAlignmentOptions.Center);
            UiBuilder.Anchor(rarity.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-26f, 0f));

            // Health on top, experience under it — the pairing every creature card in the
            // reference carries. Health has nothing to report outside a battle, so it is drawn
            // full rather than faked; experience is real, and comes off the server's own curve.
            Bar(card, 62f, 10f, UiPalette.AceLime, 1f);
            Bar(card, 46f, 7f, UiPalette.AceCyan, ExperienceFraction(entry.level, entry.experience));

            var level = UiBuilder.Text("Level", card, "Lv " + entry.level, UiTextRole.Caption,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(level.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(-36f, 30f));

            // 돌파 조각, top-right, when this creature has any.
            //
            // On a card drawn from the last roll it means "you already had this one, and the
            // pull became a piece" — which is a result, not a blank. On a party card it is the
            // standing count, and the same badge answering the same question either way is why
            // there is only one of it.
            if (entry.shards > 0)
            {
                var piece = UiBuilder.Rect("Shards", card, false);
                UiBuilder.Anchor(piece, new Vector2(1f, 1f), new Vector2(1f, 1f),
                    new Vector2(1f, 1f), new Vector2(-10f, -10f), new Vector2(74f, 34f));
                var pieceFace = UiBuilder.Image("Face", piece, UiSprites.Pill(28),
                    UiPalette.AceViolet.WithAlpha(0.94f));
                UiBuilder.Stretch(pieceFace.rectTransform);
                var pieceText = UiBuilder.Text("Text", piece, "◆ " + entry.shards, UiTextRole.Caption,
                    UiPalette.AceText, TextAlignmentOptions.Center);
                UiBuilder.Anchor(pieceText.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-10f, 0f));
            }

            UiJuice.PopIn(card, 0.05f + slot * 0.05f, new Vector2(0f, -60f), 0.44f);
        }

        /// <summary>A track with a fill on it, anchored a given height off the card's bottom edge.</summary>
        private static void Bar(Transform card, float bottom, float height, Color colour, float fraction)
        {
            var track = UiBuilder.Rect("Bar", card, false);
            UiBuilder.Anchor(track, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0f, bottom), new Vector2(-36f, height));

            // The track has to read as an empty bar, not as absence: a fresh team has no
            // experience at all, and a track dark enough to vanish makes the cyan bar look
            // like a bug on every card the player has not battled with yet.
            var back = UiBuilder.Image("Track", track, UiSprites.Pill(Mathf.Max(4, (int)height - 4)),
                new Color(1f, 1f, 1f, 0.18f));
            UiBuilder.Stretch(back.rectTransform);

            var fill = UiBuilder.Image("Fill", track, UiSprites.Pill(Mathf.Max(4, (int)height - 4)), colour);
            UiBuilder.Anchor(fill.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 1f),
                new Vector2(0f, 0.5f), Vector2.zero, new Vector2(0f, 0f));
            var f = fill.rectTransform;
            f.anchorMax = new Vector2(0f, 1f);
            f.sizeDelta = new Vector2(0f, 0f);
            // Grown into place rather than drawn at length: a bar that is already full when the
            // card appears is a picture of a bar, and this one is worth watching fill.
            UiTween.Run(0.5f, t =>
            {
                if (f == null) return;
                f.anchorMax = new Vector2(Mathf.Clamp01(fraction) * t, 1f);
                f.sizeDelta = Vector2.zero;
            }, Ease.OutCubic, 0.25f);
        }

        /// <summary>
        /// How far through its level a creature is, 0-1.
        ///
        /// The curve is <c>level^3</c>, hard-coded to match <c>experienceForLevel</c> in the
        /// Worker's gacha.ts. Duplicated rather than fetched because the roster the server
        /// sends carries the raw total and nothing else, and a bar that guesses its own scale
        /// is a bar that lies; the duplication is flagged in both files rather than hidden.
        /// </summary>
        private static float ExperienceFraction(int level, int experience)
        {
            if (level >= 100) return 1f;
            var floor = level <= 1 ? 0 : level * level * level;
            var ceiling = (level + 1) * (level + 1) * (level + 1);
            if (ceiling <= floor) return 0f;
            return Mathf.Clamp01((experience - floor) / (float)(ceiling - floor));
        }

        /// <summary>A drawn group, in the shape the team grid draws.</summary>
        // --- Rolling ---------------------------------------------------------------------------

        private void Roll()
        {
            var session = OnlineSession.Instance;
            if (session == null || session.Busy || _reveal != null) return;

            if (session.AffordablePulls < _pulls)
            {
                UiSound.Error();
                Say(Loc.Pick("Not enough coins. Battles pay whether you win or lose.",
                             "코인이 부족해요. 대전은 이기든 지든 코인을 줘요."));
                return;
            }

            Say(Loc.Pick("Drawing…", "뽑는 중…"));
            _rollButton.interactable = false;
            UiSound.Confirm();
            UiJuice.Squash(_rollPane.Root);

            StartCoroutine(session.RollGacha(_pulls, response =>
            {
                _rollButton.interactable = true;

                if (response == null)
                {
                    UiSound.Error();
                    Say(OnlineClient.Explain(session.LastError));
                    Refresh();
                    return;
                }

                Say("");
                _lastPulls = response.pulls ?? Array.Empty<GachaPull>();
                _reveal = StartCoroutine(Reveal(response.pulls));
            }));
        }

        // --- The purse and the pull counts ------------------------------------------------

        /// <summary>
        /// What the account can spend, in the corner the group picker used to sit in.
        ///
        /// It replaced that button rather than joining it. A collection is never overwritten, so
        /// there are no earlier groups to compare against and the picker answered a question the
        /// game stopped asking; what a player needs in its place is the one number that decides
        /// whether the button below is pressable.
        /// </summary>
        private void BuildPurse(Transform safe)
        {
            var purse = UiBuilder.Rect("Purse", safe, false);
            UiBuilder.Anchor(purse, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 1f), Vector2.zero, new Vector2(380f, 76f));

            UiJuice.Pane("Pane", purse, UiPalette.AceGlass.WithAlpha(0.78f), 18,
                true, true, true, UiPalette.AceRim, 76);

            var coin = UiBuilder.Image("Coin", purse, UiSprites.BallGlyph(48, 5),
                UiPalette.AceGold, Image.Type.Simple);
            UiBuilder.Anchor(coin.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0.5f, 0.5f), new Vector2(38f, 0f), new Vector2(32f, 32f));

            _purseLabel = UiBuilder.Text("Text", purse, "0", UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Right);
            UiBuilder.Anchor(_purseLabel.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
            _purseLabel.rectTransform.offsetMin = new Vector2(66f, 8f);
            _purseLabel.rectTransform.offsetMax = new Vector2(-26f, -8f);
            _purseLabel.textWrappingMode = TextWrappingModes.NoWrap;

            UiJuice.PopIn(purse, 0.1f, new Vector2(0f, 70f), 0.42f);
        }

        private void RefreshPurse()
        {
            if (_purseLabel == null) return;
            var session = OnlineSession.Instance;
            var coins = session != null ? session.Coins : 0;
            var free = session != null ? session.FreePulls : 0;

            _purseLabel.text = free > 0
                ? Loc.Pick($"{coins:N0}  ·  {free} free", $"{coins:N0}  ·  무료 {free}회")
                : $"{coins:N0}";
        }

        /// <summary>
        /// How many to draw: two arrows and the number between them.
        ///
        /// <b>Why not the three chips this replaces.</b> x1 / x5 / x10 offered three counts and
        /// implied there were only three, which is not what the route does -- the Worker takes
        /// any number up to MAX_PULLS_PER_ROLL. A stepper says the real rule without a legend:
        /// the number goes up, the number goes down, and it stops where the server stops.
        ///
        /// Arrows rather than a typed field. A text input on this screen would want a keyboard,
        /// and this is the screen that opens on a phone.
        /// </summary>
        private void BuildPullStepper(Transform safe)
        {
            const float arrow = 64f;
            const float box = 132f;
            const float height = 62f;
            const float gap = 10f;
            var total = arrow * 2f + box + gap * 2f;

            var row = UiBuilder.Rect("Pulls", safe, false);
            UiBuilder.Anchor(row, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(-150f, 128f), new Vector2(total, height));

            _pullDown = StepperArrow(row, "Down", "‹", 0f, arrow, height, -1);

            var field = UiBuilder.Rect("Count", row, false);
            UiBuilder.Anchor(field, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(arrow + gap, 0f), new Vector2(box, height));
            UiJuice.Pane("Pane", field, UiPalette.AceGlass.WithAlpha(0.72f), 16,
                true, true, true, UiPalette.AceRim, (int)height);
            _pullCount = UiBuilder.Text("Label", field, "1", UiTextRole.Numeric,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_pullCount.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-12f, -12f));
            _pullCount.textWrappingMode = TextWrappingModes.NoWrap;

            _pullUp = StepperArrow(row, "Up", "›", arrow + gap + box + gap, arrow, height, 1);

            UiJuice.PopIn(row, 0.18f, new Vector2(0f, -60f), 0.4f);
        }

        /// <summary>One arrow. Returns the button so the refresh can grey it at its limit.</summary>
        private Button StepperArrow(Transform row, string name, string glyph, float x,
                                    float width, float height, int delta)
        {
            var cell = UiBuilder.Rect(name, row, false);
            UiBuilder.Anchor(cell, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(width, height));

            var pane = UiJuice.Pane("Pane", cell, UiPalette.AceGlass.WithAlpha(0.72f), 16,
                true, true, true, UiPalette.AceRim, (int)height);
            var label = UiBuilder.Text("Label", cell, glyph, UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(label.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-8f, -10f));

            if (delta < 0) _pullDownPane = pane; else _pullUpPane = pane;
            if (delta < 0) _pullDownLabel = label; else _pullUpLabel = label;

            UiButtonMotion.Attach(cell, 16);
            return UiBuilder.Button("Take", cell, pane.Fill, () => StepPulls(delta));
        }

        /// <summary>Nudges the count, clamped to what the route will accept.</summary>
        private void StepPulls(int delta)
        {
            var session = OnlineSession.Instance;
            var ceiling = session != null ? Mathf.Max(1, session.MaxPulls) : 10;
            var wanted = Mathf.Clamp(_pulls + delta, 1, ceiling);
            if (wanted == _pulls) { UiSound.Error(); return; }
            ChoosePulls(wanted);
        }

        private void ChoosePulls(int count)
        {
            if (_pulls == count) return;
            _pulls = count;
            UiSound.Navigate();
            Say("");
            Refresh();
        }

        private void RefreshPullStepper()
        {
            var session = OnlineSession.Instance;
            var ceiling = session != null ? Mathf.Max(1, session.MaxPulls) : 10;

            // Never leave the count above what the route accepts. Silently: this runs on every
            // refresh and a sentence about it would fire on arrival.
            _pulls = Mathf.Clamp(_pulls, 1, ceiling);

            if (_pullCount != null)
            {
                _pullCount.text = _pulls.ToString();
                // Amber once the purse cannot cover the count. The number still moves -- seeing
                // that ten is out of reach is the argument for going and battling -- but it says
                // so before the draw button has to.
                var affordable = session != null ? session.AffordablePulls : 0;
                _pullCount.color = _pulls <= affordable ? UiPalette.AceText : UiPalette.AceGold;
            }

            SetArrow(_pullDown, _pullDownPane, _pullDownLabel, _pulls > 1);
            SetArrow(_pullUp, _pullUpPane, _pullUpLabel, _pulls < ceiling);
        }

        private static void SetArrow(Button button, UiPane pane, TextMeshProUGUI label, bool live)
        {
            if (button != null) button.interactable = live;
            if (pane.IsValid)
                UiJuice.Recolour(pane, UiPalette.AceGlass.WithAlpha(live ? 0.72f : 0.28f));
            if (label != null) label.color = live ? UiPalette.AceText : UiPalette.AceTextFaint;
        }

        /// <summary>
        /// The sequence. One pass per pull, then the team.
        ///
        /// Written as a coroutine rather than a chain of tween callbacks because it is a
        /// sequence with waits in it, and a sequence expressed as nested callbacks is a
        /// sequence nobody can insert a step into later.
        /// </summary>
        private IEnumerator Reveal(GachaPull[] pulls)
        {
            if (pulls == null || pulls.Length == 0) { _reveal = null; Refresh(); yield break; }

            _skip = false;
            _revealRoot.gameObject.SetActive(true);
            // Last sibling every time, not just on the frame it was built: anything added to
            // the panel after Build would otherwise draw over the reveal.
            _revealRoot.SetAsLastSibling();
            UiBuilder.ClearChildren(_revealRoot);

            // Opaque, not a scrim. The first capture of this used 0.97 alpha and the team grid
            // read straight through it, which turns the one moment the screen is supposed to
            // own into a transition playing over a menu.
            var curtain = UiBuilder.Backdrop("Curtain", _revealRoot, null, UiPalette.AceNight, true);
            UiBuilder.Stretch(curtain.rectTransform);

            // A pool of light on the floor of the empty room, so the curtain is a place rather
            // than a black rectangle even before the ball lands in it.
            var pool = UiBuilder.Image("Pool", _revealRoot, UiSprites.Glow(256, 1.6f),
                UiPalette.AceHalo.WithAlpha(0.22f), Image.Type.Simple);
            UiBuilder.Anchor(pool.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(2200f, 1600f));

            // Everything that can be shaken lives in here. The curtain does not, because a
            // full-screen backdrop that moves shows the screen it is covering along one edge.
            var world = UiBuilder.Rect("World", _revealRoot);

            var rays = UiBuilder.Image("Rays", world, UiSprites.SpeedLines(512, 30, 0.14f),
                new Color(1f, 1f, 1f, 0f), Image.Type.Simple);
            UiBuilder.Anchor(rays.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(2400f, 2400f));
            UiIdle.Attach(rays.rectTransform, UiIdleMode.Spin, -360f, 26f);

            var glow = UiBuilder.Image("Glow", world, UiSprites.Glow(384, 2.1f),
                new Color(1f, 1f, 1f, 0f), Image.Type.Simple);
            UiBuilder.Anchor(glow.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1000f, 1000f));

            var stage = UiBuilder.Rect("Stage", world, false);
            UiBuilder.Anchor(stage, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 820f));

            // Built once and reused by every pull rather than per pull, and oversized by
            // 200px on each edge: the stage shakes during the burst, and a flash that only
            // covers the screen exactly would show a strip of curtain along whichever edge the
            // shake pushed it away from.
            var flash = UiBuilder.Backdrop("Flash", world, null, new Color(1f, 1f, 1f, 0f), false);
            UiBuilder.Stretch(flash.rectTransform, -200f);

            var counter = BuildCounter(_revealRoot);
            BuildSkipButton(_revealRoot);

            for (var index = 0; index < pulls.Length && !_skip; index++)
            {
                counter.text = $"{index + 1} / {pulls.Length}";
                yield return RevealOne(world, stage, rays, glow, flash, pulls[index]);
            }

            _revealRoot.gameObject.SetActive(false);
            _reveal = null;

            Refresh();
            Say(Loc.Pick("Your team is ready.", "팀이 완성되었어요."));
        }

        private IEnumerator RevealOne(RectTransform world, RectTransform stage, Image rays, Image glow,
                                      Image flash, GachaPull pull)
        {
            UiBuilder.ClearChildren(stage);

            var rank = Mathf.Clamp(pull.rarityRank, 0, 4);
            var colour = UiPalette.Rarity(rank);

            rays.color = colour.WithAlpha(0f);
            glow.color = colour.WithAlpha(0f);
            flash.color = new Color(1f, 1f, 1f, 0f);
            world.anchoredPosition = Vector2.zero;
            stage.localScale = Vector3.one;

            var ball = UiJuice.Ball(stage, 300f);

            // 1. It falls in, overshoots, and squashes on landing. The squash is the whole
            //    difference between a ball arriving and an image appearing.
            ball.anchoredPosition = new Vector2(0f, 460f);
            ball.localScale = Vector3.one * 0.45f;
            UiTween.AnchoredMove(ball, Vector2.zero, 0.44f, Ease.OutBack);
            UiTween.Scale(ball, Vector3.one, 0.40f, Ease.OutCubic);
            yield return Wait(0.40f);

            UiJuice.Shockwave(stage, UiPalette.AceSelect.WithAlpha(0.5f), 300f, 1.9f, 0.5f);
            UiTween.Scale(ball, new Vector3(1.22f, 0.78f, 1f), 0.07f, Ease.OutCubic, 0f,
                () => UiTween.Scale(ball, Vector3.one, 0.34f, Ease.OutElastic));
            yield return Wait(0.26f);

            var restingGlow = 0.10f + rank * 0.07f;
            UiTween.Run(0.3f, t => { if (glow != null) glow.color = colour.WithAlpha(t * restingGlow); });

            // 2. It shakes, once per rarity rank plus one. The count IS the tell, so it is
            //    driven straight off the server's rank and nothing else.
            var shakes = rank + 1;
            for (var i = 0; i < shakes && !_skip; i++)
            {
                var last = i == shakes - 1;

                // Above epic, the beat before the last shake is stretched and the stage creeps
                // in. Anticipation is the only part of this that cannot be bought with more
                // particles: what makes a legendary land is the second in which nothing happens.
                if (last && rank >= 3)
                {
                    UiTween.Scale(stage, Vector3.one * 1.09f, 0.55f, Ease.InOutQuad);
                    UiTween.Run(0.55f, t => { if (glow != null) glow.color = colour.WithAlpha(restingGlow * (1f + t * 1.4f)); });
                    yield return Wait(0.42f);
                }

                var amplitude = 12f + i * 7f;
                var beat = 0.15f + i * 0.035f;
                var peak = restingGlow + 0.14f + i * 0.06f;
                UiTween.Run(beat * 1.6f, t => { if (glow != null) glow.color = colour.WithAlpha(Mathf.Lerp(peak, restingGlow, t)); });
                UiTween.Punch(ball, 0.05f + i * 0.02f, beat * 1.4f);
                UiSound.Navigate();

                yield return ShakeBall(ball, amplitude, beat);
                yield return Wait(0.13f + i * 0.06f);
            }

            if (_skip) yield break;

            // 3. The burst. Five things at once, every one of them scaled by rank: the flash,
            //    the starburst, the rings, the sparkles and the shake. A common gets a pop; a
            //    legendary gets all five at full size, which is why they do not feel alike.
            var washPeak = 0.42f + rank * 0.14f;
            UiTween.Run(0.55f, t => { if (flash != null) flash.color = new Color(1f, 1f, 1f, Mathf.Pow(1f - t, 2.2f) * washPeak); });

            UiTween.Run(0.9f, t => { if (rays != null) rays.color = colour.WithAlpha(Mathf.Sin(t * Mathf.PI) * (0.10f + rank * 0.07f)); });
            UiTween.Run(0.9f, t => { if (glow != null) glow.color = colour.WithAlpha(Mathf.Lerp(0.55f + rank * 0.08f, restingGlow * 1.4f, t)); });

            var star = UiBuilder.Image("Star", stage, UiSprites.Starburst(384, 8 + rank * 2, 0.40f),
                colour, Image.Type.Simple);
            UiBuilder.Anchor(star.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(420f, 420f));
            star.rectTransform.SetAsFirstSibling();
            UiTween.Run(0.75f, t =>
            {
                if (star == null) return;
                star.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.25f, 1.7f + rank * 0.45f, t);
                star.rectTransform.localRotation = Quaternion.Euler(0f, 0f, t * (40f + rank * 20f));
                star.color = colour.WithAlpha((1f - t) * 0.85f);
            }, Ease.OutCubic);

            for (var ring = 0; ring <= rank; ring++)
            {
                UiJuice.Shockwave(stage, colour.WithAlpha(0.9f), 280f, 3.4f + rank * 0.5f, 0.75f, 0.09f * ring, 10);
            }

            Sparkles(stage, colour, 6 + rank * 5);

            UiTween.Scale(ball, Vector3.one * 1.45f, 0.2f, Ease.OutCubic);
            UiTween.Run(0.24f, t => SetAlpha(ball.gameObject, 1f - t), Ease.OutCubic, 0.1f);
            StartCoroutine(ShakeStage(world, 7f + rank * 8f, 0.3f + rank * 0.06f));
            UiSound.Confirm();
            yield return Wait(0.26f);

            // 4. The creature.
            var card = BuildRevealCard(stage, pull, colour, rank);
            if (rank >= 3) Confetti(stage, colour, 22 + rank * 8);
            yield return Wait(0.12f);

            // Held until the player moves it on, or a beat passes. Rarer pulls hold longer,
            // because that is the one the player wants to look at.
            yield return WaitForAdvance(1.15f + rank * 0.38f);

            UiTween.Run(0.2f, t => SetAlpha(card, 1f - t));
            UiTween.Run(0.25f, t => { if (glow != null) glow.color = colour.WithAlpha(restingGlow * (1f - t)); });
            yield return Wait(0.22f);
        }

        /// <summary>Wobbles the ball about its own centre. Rotation and not position: a ball that translates looks dragged.</summary>
        private IEnumerator ShakeBall(RectTransform ball, float amplitude, float beat)
        {
            var done = false;
            UiTween.Run(beat, t =>
            {
                if (ball == null) return;
                // Two full cycles per shake, decaying, so it reads as a wobble rather than a
                // slide.
                var angle = Mathf.Sin(t * Mathf.PI * 4f) * amplitude * (1f - t);
                ball.localRotation = Quaternion.Euler(0f, 0f, angle);
            }, Ease.Linear, 0f, true, () => { if (ball != null) ball.localRotation = Quaternion.identity; done = true; });

            var guard = 0f;
            while (!done && guard < beat + 0.5f && !_skip)
            {
                guard += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        /// <summary>
        /// Kicks the whole reveal sideways for a fraction of a second.
        ///
        /// Decays over its life and lands exactly back on zero, because a shake that ends on a
        /// random offset leaves everything on the screen a few pixels out of place for the rest
        /// of the pull and nobody can see why.
        /// </summary>
        private IEnumerator ShakeStage(RectTransform target, float magnitude, float seconds)
        {
            if (!UiTween.MotionEnabled || target == null) yield break;

            var elapsed = 0f;
            while (elapsed < seconds && target != null)
            {
                elapsed += Time.unscaledDeltaTime;
                var decay = 1f - Mathf.Clamp01(elapsed / seconds);
                target.anchoredPosition = new Vector2(
                    UnityEngine.Random.Range(-1f, 1f) * magnitude * decay,
                    UnityEngine.Random.Range(-1f, 1f) * magnitude * decay);
                yield return null;
            }

            if (target != null) target.anchoredPosition = Vector2.zero;
        }

        /// <summary>Flings twinkles outward from the centre, each with its own direction, distance and spin.</summary>
        private static void Sparkles(Transform stage, Color colour, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var angle = (i / (float)count) * Mathf.PI * 2f + UnityEngine.Random.Range(-0.22f, 0.22f);
                var distance = UnityEngine.Random.Range(240f, 430f);
                var size = UnityEngine.Random.Range(34f, 74f);
                var spin = UnityEngine.Random.Range(-170f, 170f);
                var life = UnityEngine.Random.Range(0.5f, 0.85f);

                var spark = UiBuilder.Image("Sparkle", stage, UiSprites.Sparkle(64), colour, Image.Type.Simple);
                UiBuilder.Anchor(spark.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));

                var target = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                UiTween.Run(life, t =>
                {
                    if (spark == null) return;
                    // Eased outward with a little gravity on the tail, so they arc rather than
                    // radiate — a perfect starburst of straight lines reads as a diagram.
                    spark.rectTransform.anchoredPosition = new Vector2(target.x * t, target.y * t - t * t * 90f);
                    spark.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.4f, 1.15f, Mathf.Min(1f, t * 3f));
                    spark.rectTransform.localRotation = Quaternion.Euler(0f, 0f, spin * t);
                    spark.color = colour.WithAlpha(1f - t * t);
                }, Ease.OutCubic);
            }
        }

        /// <summary>Paper rain, for the two tiers that have earned it.</summary>
        private static void Confetti(Transform parent, Color colour, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var width = UnityEngine.Random.Range(12f, 22f);
                var height = UnityEngine.Random.Range(18f, 34f);
                var x = UnityEngine.Random.Range(-940f, 940f);
                var fall = UnityEngine.Random.Range(1.5f, 2.4f);
                var delay = UnityEngine.Random.Range(0f, 0.7f);
                var spin = UnityEngine.Random.Range(-540f, 540f);
                var tint = UnityEngine.Random.value < 0.5f ? colour : UiPalette.AceSelect;

                var piece = UiBuilder.Image("Confetti", parent, UiSprites.Panel(4), tint);
                UiBuilder.Anchor(piece.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(x, 700f), new Vector2(width, height));

                UiTween.Run(fall, t =>
                {
                    if (piece == null) return;
                    piece.rectTransform.anchoredPosition = new Vector2(
                        x + Mathf.Sin(t * 8f + i) * 46f, Mathf.Lerp(700f, -760f, t));
                    piece.rectTransform.localRotation = Quaternion.Euler(0f, 0f, spin * t);
                    piece.color = tint.WithAlpha(t > 0.8f ? (1f - t) * 5f : 1f);
                }, Ease.Linear, delay);
            }
        }

        /// <summary>
        /// The payoff card: the creature, its name, and the tier it came out at.
        ///
        /// It arrives from above its final scale rather than below it — scaling down into place
        /// reads as something being pushed at the camera, which is what a reveal is, where
        /// scaling up reads as a dialog opening.
        /// </summary>
        private GameObject BuildRevealCard(Transform stage, GachaPull pull, Color colour, int rank)
        {
            var card = UiBuilder.Rect("Card", stage, false);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(700f, 700f));

            var portrait = Portrait(pull.speciesId);
            if (portrait != null)
            {
                var image = UiBuilder.Image("Portrait", card, portrait, Color.white, Image.Type.Simple);
                UiBuilder.Anchor(image.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), new Vector2(380f, 380f));
                image.preserveAspect = true;
            }
            else
            {
                // No art registry in this build. A blank middle would read as a bug, so the
                // silhouette stands in — the tier and the name are what the reveal is about.
                var stand = UiBuilder.Image("Stand", card, UiSprites.BallGlyph(384, 20),
                    UiPalette.AceSelect.WithAlpha(0.92f), Image.Type.Simple);
                UiBuilder.Anchor(stand.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), new Vector2(300f, 300f));
            }

            var banner = UiBuilder.Rect("Rarity", card, false);
            UiBuilder.Anchor(banner, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 160f), new Vector2(340f, 62f));
            var bannerGlow = UiBuilder.Image("Glow", banner, UiSprites.Shadow(26, 40), colour.WithAlpha(0.5f));
            UiBuilder.Stretch(bannerGlow.rectTransform, -24f);
            var bannerFace = UiBuilder.Image("Face", banner, UiSprites.Pill(54), colour);
            UiBuilder.Stretch(bannerFace.rectTransform);
            var rarity = UiBuilder.Text("Text", banner, RarityLabel(pull.rarity), UiTextRole.Heading,
                UiPalette.AceInk, TextAlignmentOptions.Center);
            UiBuilder.Anchor(rarity.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-44f, 0f));

            // Title, not Metric. Every label in this UI carries overflowMode Ellipsis, and TMP
            // draws nothing at all when one line is taller than its rect — a 96pt Metric in a
            // 72px box is an invisible creature name, which is the whole payoff of the reveal.
            var name = UiBuilder.Text("Name", card, SpeciesName(pull.speciesId), UiTextRole.Title,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 76f), new Vector2(0f, 76f));
            // The one label in the whole front end that gets a drawn rim: it lands on top of a
            // white flash and a moving starburst, and nothing else would hold its edge there.
            UiJuice.Ink(name, UiPalette.AceNight, 5f, 6f);

            // A duplicate says so, under the name, in the moment it lands.
            //
            // It has to be said HERE and not only afterwards. The whole reveal is built to
            // promise something by how loudly it shakes, and a legendary you already own shakes
            // just as hard as one you do not — so a player who is not told reads the fanfare as
            // a lie the second time it happens. Saying what it became instead keeps the promise
            // true: a piece is not nothing, it is the only route to 돌파.
            if (pull.duplicate)
            {
                var already = UiBuilder.Text("Duplicate", card,
                    Loc.Pick("Already yours · +1 breakthrough piece", "이미 가진 포켓몬 · 돌파 조각 +1"),
                    UiTextRole.Body, UiPalette.AceViolet, TextAlignmentOptions.Center);
                UiBuilder.Anchor(already.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(0f, 44f));
                UiJuice.Ink(already, UiPalette.AceNight, 4f, 5f);
            }

            // The ribbon arrives after the creature and from the side, so the two do not read
            // as one object landing.
            banner.anchoredPosition += new Vector2(-560f, 0f);
            UiTween.AnchoredMove(banner, new Vector2(0f, 160f), 0.42f, Ease.OutBack, 0.16f);

            card.localScale = Vector3.one * (1.7f + rank * 0.12f);
            UiTween.Scale(card, Vector3.one, 0.38f, Ease.OutBack);
            SetAlpha(card.gameObject, 0f);
            UiTween.Run(0.2f, t => SetAlpha(card.gameObject, t));

            return card.gameObject;
        }

        private static TextMeshProUGUI BuildCounter(Transform parent)
        {
            var pill = UiBuilder.Rect("Counter", parent, false);
            UiBuilder.Anchor(pill, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(0f, -56f), new Vector2(180f, 52f));

            var back = UiBuilder.Image("Pill", pill, UiSprites.Pill(44), UiPalette.AceSelect.WithAlpha(0.14f));
            UiBuilder.Stretch(back.rectTransform);

            var text = UiBuilder.Text("Text", pill, "", UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(text.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-16f, -8f));
            return text;
        }

        private void BuildSkipButton(Transform parent)
        {
            var skip = UiBuilder.Rect("Skip", parent, false);
            UiBuilder.Anchor(skip, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(-56f, 48f), new Vector2(220f, 72f));

            var pane = UiJuice.Pane("Pane", skip, UiPalette.AceGlass.WithAlpha(0.8f), 18,
                true, true, true, UiPalette.AceRim, 72);

            var label = UiBuilder.Text("Label", skip, Loc.Pick("Skip", "건너뛰기"), UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(label.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-20f, -14f));

            UiButtonMotion.Attach(skip, 16);
            UiBuilder.Button("Take", skip, pane.Fill, () => _skip = true);
        }

        // --- Waiting -----------------------------------------------------------------------------

        private IEnumerator Wait(float seconds)
        {
            // Unscaled, because this screen can be open while the game behind it is paused, and
            // a reveal that stops when Time.timeScale does is a reveal that hangs.
            if (!UiTween.MotionEnabled) yield break;
            var elapsed = 0f;
            while (elapsed < seconds && !_skip)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private IEnumerator WaitForAdvance(float seconds)
        {
            _advance = false;
            var elapsed = 0f;
            while (elapsed < seconds && !_advance && !_skip)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }
        }

        private void Update()
        {
            if (!IsOpen) return;

            var keyboard = Keyboard.current;
            var mouse = Mouse.current;

            var pressed =
                (keyboard != null && (keyboard.spaceKey.wasPressedThisFrame
                                      || keyboard.enterKey.wasPressedThisFrame))
                || (mouse != null && mouse.leftButton.wasPressedThisFrame);

            if (pressed) _advance = true;

            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
            {
                // Innermost surface first: the reveal, then the panel itself.
                if (_reveal != null) _skip = true;
                else Close();
            }
        }

        // --- Look-ups ----------------------------------------------------------------------------

        /// <summary>
        /// The creature's picture.
        ///
        /// This asked <c>ICreatureArtRegistry.GetPortrait</c> and got null every single time,
        /// which is why a pull showed a name over empty space. Nothing registers a real art
        /// registry outside a battle — and the one that exists there answers heights only —
        /// while the `*_portrait.png` files sit outside any Resources folder and cannot be
        /// loaded at runtime at all. <see cref="CreatureThumbnail"/> goes through the manifest
        /// path the battle billboards use, which does have textures behind it.
        /// </summary>
        private static Sprite Portrait(int speciesId) => CreatureThumbnail.Front(speciesId);

        private static string SpeciesName(int speciesId)
        {
            if (ServiceHub.TryGet<ISpeciesRegistry>(out var species)
                && species.TryGet(speciesId, out var data))
            {
                return data.DisplayName;
            }
            return "#" + speciesId;
        }

        /// <summary>
        /// The rank of a rarity name, and the only place the mapping lives.
        ///
        /// <see cref="GachaPull"/> carries its own <c>rarityRank</c> straight from the server;
        /// <see cref="RosterEntry"/> carries only the name, so the team grid has to recover the
        /// rank to colour a card. Doing it here means the burst and the card it produced can
        /// never disagree about which tier they are showing.
        /// </summary>
        private static int RankOf(string rarity)
        {
            switch (rarity)
            {
                case "legendary": return 4;
                case "epic": return 3;
                case "rare": return 2;
                case "uncommon": return 1;
                default: return 0;
            }
        }

        private static string RarityLabel(string rarity)
        {
            switch (rarity)
            {
                case "legendary": return Loc.Pick("LEGENDARY", "전설");
                case "epic": return Loc.Pick("EPIC", "영웅");
                case "rare": return Loc.Pick("RARE", "희귀");
                case "uncommon": return Loc.Pick("UNCOMMON", "고급");
                default: return Loc.Pick("COMMON", "일반");
            }
        }

        /// <summary>
        /// The odds, stated on the screen that uses them.
        ///
        /// Hard-coded to match `TIER_WEIGHT` in the Worker's pool.ts. A gacha that does not
        /// publish its rates is one players are right not to trust, and the duplication is
        /// flagged in both files rather than hidden.
        /// </summary>
        private static string OddsLine() => Loc.Pick(
            "COMMON 55%   UNCOMMON 27%   RARE 12%   EPIC 4.5%   LEGENDARY 1.5%   ·   53 species · a duplicate becomes a piece",
            "일반 55%   고급 27%   희귀 12%   영웅 4.5%   전설 1.5%   ·   53종 · 중복은 돌파 조각으로");

        private static void SetAlpha(GameObject target, float alpha)
        {
            if (target == null) return;
            var group = target.GetComponent<CanvasGroup>();
            if (group == null) group = target.AddComponent<CanvasGroup>();
            group.alpha = Mathf.Clamp01(alpha);
        }

        private void Say(string message)
        {
            if (_status != null) _status.text = message ?? "";
            // The pill is the message, not a shelf the message sits on: an empty capsule in the
            // middle of the screen is a piece of UI that means nothing.
            if (_statusPill != null) _statusPill.gameObject.SetActive(!string.IsNullOrEmpty(message));
        }
    }
}
