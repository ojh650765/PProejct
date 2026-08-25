using System;
using System.Collections.Generic;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// 내 포켓몬: the collection, and everything you can do to one of them.
    ///
    /// <b>What this screen is for, in the user's words.</b> 내 포켓몬 이라는 메뉴가 존재했으면
    /// 좋겠음. 강화하고 돌파하고, 대전해서 … 기술디스크 … 이런걸 가끔식 획득가능하게 한다음에.
    /// 그걸 내 포켓몬 이라는 메뉴에서 가르치는거지. 그리고 이상한 사탕 아이템도 멕이고.
    /// 블루아카이브 같은 성장형 게임. 수집하고. Four verbs and a list that grows, which is a
    /// different screen from the gacha's fixed six — so it is a different screen.
    ///
    /// <b>List on the left, one creature on the right.</b> Not a grid of cards: a collection
    /// gets long, and every action here is about exactly one creature, so the screen is built
    /// around "which one" and then "what to it". The list stays visible while acting, because
    /// after a 강화 the next thing a player does is look at somebody else.
    ///
    /// <b>Nothing here decides anything.</b> Every button posts to the Worker and redraws from
    /// what comes back — the price, the level, the star, the moveset. That is not politeness
    /// about a single-player screen; it is that a creature grown here walks into a PvP match,
    /// so a level or a move this screen could grant itself would be one that means nothing
    /// across the network. What the screen DOES decide is what to grey out, so that a refusal
    /// is normally something you can see coming rather than a round trip that says no.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class MyCreaturesPanel : MonoBehaviour
    {
        /// <summary>Raised when the panel closes, so the menu behind it can redraw.</summary>
        public Action Closed;

        public bool IsOpen { get; private set; }

        /// <summary>Move slots a creature carries. Matches CreatureFactory and the Worker.</summary>
        private const int MoveSlots = 4;

        /// <summary>Stars a 돌파 can reach. Matches economy.ts MAX_STARS.</summary>
        private const int MaxStars = 5;

        /// <summary>
        /// The costs, restated.
        ///
        /// <b>These are a preview, never the price.</b> The Worker charges what economy.ts says
        /// and refuses what it cannot afford; these exist only so a button can say what it will
        /// cost before it is pressed, and so a row the account cannot pay for goes dim instead
        /// of going through. Duplicated with this note rather than fetched, because a screen
        /// that had to ask the server what a button costs could not draw itself offline — and
        /// because a wrong number here is a cosmetic bug, while a wrong number there is a
        /// broken economy.
        /// </summary>
        private static readonly int[] BreakthroughShards = { 2, 4, 8, 14, 24 };
        private static readonly int[] BreakthroughCoins = { 400, 900, 1800, 3200, 5400 };

        private const string CandyItem = "candy";
        private const string DiscPrefix = "disc:";

        /// <summary>Height of the battle-team strip, chrome included.</summary>
        private const float TeamFrameHeight = 202f;

        /// <summary>Creatures per row in the collection box.</summary>
        private const int BoxColumns = 5;

        private RectTransform _teamRoot;
        private RectTransform _boxContent;
        private RectTransform _detail;
        private RectTransform _discsRoot;
        private TextMeshProUGUI _status;
        private TextMeshProUGUI _purse;
        private TextMeshProUGUI _reorderLabel;

        /// <summary>The collection slot being looked at. -1 before anything is selected.</summary>
        private int _selected = -1;

        /// <summary>
        /// Whether the team strip is being reordered, and who is currently lifted.
        ///
        /// <b>Why a mode rather than a drag.</b> The order of the six IS the battle order --
        /// slot 1 leads, and the rest come in as the leader faints -- so it has to be editable.
        /// A drag would be the obvious gesture and is the wrong one here: the strip lives inside
        /// a scrolling screen on a touch device, where a press that moves is already spoken for,
        /// and a mis-scroll that silently reshuffles the team is worse than one more button. So
        /// the strip has a stated mode, the lifted tile says it is lifted, and the status line
        /// says what the next tap will do.
        /// </summary>
        private bool _reordering;

        /// <summary>The collection slot lifted for a move, or -1 when nobody is held.</summary>
        private int _carry = -1;

        private void Awake() => gameObject.SetActive(false);

        public void Open()
        {
            OnlineSession.Ensure();
            gameObject.SetActive(true);
            IsOpen = true;
            _reordering = false;
            _carry = -1;
            UiSound.MenuOpen();

            Build();

            // The roster is re-fetched rather than trusted, because this screen is where a
            // player comes to spend things and the cached purse is as old as the last battle.
            var session = OnlineSession.Instance;
            if (session != null && session.IsSignedIn) StartCoroutine(session.FetchRoster(_ => Refresh()));

            Refresh();
        }

        public void Close()
        {
            IsOpen = false;
            UiSound.MenuClose();
            gameObject.SetActive(false);
            Closed?.Invoke();
        }

        // --- Chrome ---------------------------------------------------------------------------

        private void Build()
        {
            var root = (RectTransform)transform;
            UiBuilder.ClearChildren(root);
            UiBuilder.Stretch(root);

            // Every generated Image here is built with raycastTarget off, so the panel needs one
            // invisible catcher underneath or clicks land on the menu it is covering.
            var blocker = UiBuilder.Backdrop("Blocker", root, null, new Color(0f, 0f, 0f, 0f), true);
            UiBuilder.Stretch(blocker.rectTransform);

            UiJuice.Backdrop(root, UiPalette.AceMint, UiPalette.AceCyan);

            var safe = UiBuilder.SafeArea(root, 96f, 56f);

            var title = UiBuilder.Text("Title", safe, Loc.Pick("My Pokémon", "내 포켓몬"),
                UiTextRole.Metric, UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(title.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(-420f, 124f));

            BuildPurse(safe);

            // Left column: the six that fight, above everything that is owned.
            //
            // The split is the point of the screen. A collection screen that is one long list
            // makes the party an attribute of a row -- a little badge you have to read each
            // entry to find -- when the party is the thing the player is actually maintaining.
            // Lifting it out gives the six a fixed place with their order written on them, and
            // leaves the box below free to grow to any length without ever pushing them away.
            var left = UiBuilder.Rect("Left", safe, false);
            UiBuilder.Anchor(left, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(0f, -70f), new Vector2(560f, -260f));

            BuildTeamFrame(left);
            BuildBoxFrame(left);

            // Right column: the one creature.
            _detail = UiBuilder.Rect("Detail", safe, false);
            UiBuilder.Anchor(_detail, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0f, 0.5f),
                Vector2.zero, Vector2.zero);
            _detail.offsetMin = new Vector2(596f, 96f);
            _detail.offsetMax = new Vector2(0f, -140f);

            // Status line and close, along the bottom.
            var statusPill = UiBuilder.Rect("Status", safe, false);
            UiBuilder.Anchor(statusPill, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(-150f, 18f), new Vector2(940f, 54f));
            var statusBack = UiBuilder.Image("Pill", statusPill, UiSprites.Pill(46),
                UiPalette.AceGlass.WithAlpha(0.72f));
            UiBuilder.Stretch(statusBack.rectTransform);
            _status = UiBuilder.Text("Text", statusPill, "", UiTextRole.Secondary,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_status.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-36f, -8f));

            var close = UiBuilder.Rect("Close", safe, false);
            UiBuilder.Anchor(close, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 0f), new Vector2(0f, 18f), new Vector2(220f, 54f));
            var closePane = UiJuice.Pane("Pane", close, UiPalette.AceGlass.WithAlpha(0.72f), 16,
                true, true, true, UiPalette.AceRim, 54);
            var closeLabel = UiBuilder.Text("Label", close, Loc.Pick("Close", "닫기"),
                UiTextRole.Body, UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(closeLabel.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-24f, -12f));
            UiBuilder.Button("Take", close, closePane.Fill, Close);

            // The disc list is a page over the screen, built empty until a move slot is pressed.
            _discsRoot = UiBuilder.Rect("Discs", root, false);
            UiBuilder.Stretch(_discsRoot);
            _discsRoot.gameObject.SetActive(false);
        }

        /// <summary>
        /// The battle team: six fixed places, numbered, with the reorder switch on the header.
        /// </summary>
        private void BuildTeamFrame(RectTransform left)
        {
            var frame = UiBuilder.Rect("TeamFrame", left, false);
            UiBuilder.Anchor(frame, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0.5f, 1f),
                Vector2.zero, new Vector2(0f, TeamFrameHeight));
            UiJuice.Pane("Pane", frame, UiPalette.AceGlass.WithAlpha(0.62f), 18,
                true, true, true, UiPalette.AceRim, 120);

            var label = UiBuilder.Text("Label", frame, Loc.Pick("Battle team", "배틀 팀"),
                UiTextRole.Overline, UiPalette.AceTextDim, TextAlignmentOptions.Left);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(18f, -14f), new Vector2(-230f, 30f));
            label.textWrappingMode = TextWrappingModes.NoWrap;

            var toggle = UiBuilder.Rect("Reorder", frame, false);
            UiBuilder.Anchor(toggle, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(-14f, -10f), new Vector2(184f, 44f));
            var togglePane = UiJuice.Pane("Pane", toggle, UiPalette.AceGlass.WithAlpha(0.8f), 14,
                true, true, true, UiPalette.AceRim, 44);
            _reorderLabel = UiBuilder.Text("Label", toggle, "", UiTextRole.Caption,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_reorderLabel.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-16f, -10f));
            _reorderLabel.textWrappingMode = TextWrappingModes.NoWrap;
            UiButtonMotion.Attach(toggle, 14);
            UiBuilder.Button("Take", toggle, togglePane.Fill, ToggleReorder);

            _teamRoot = UiBuilder.Rect("Team", frame, false);
            UiBuilder.Anchor(_teamRoot, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            _teamRoot.offsetMin = new Vector2(14f, 14f);
            _teamRoot.offsetMax = new Vector2(-14f, -58f);
            UiBuilder.Horizontal(_teamRoot, 8f, null, TextAnchor.MiddleCenter, true, true);
        }

        /// <summary>Everything owned, six to a row, scrolling under the team.</summary>
        private void BuildBoxFrame(RectTransform left)
        {
            var frame = UiBuilder.Rect("BoxFrame", left, false);
            UiBuilder.Anchor(frame, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            frame.offsetMin = Vector2.zero;
            frame.offsetMax = new Vector2(0f, -(TeamFrameHeight + 12f));
            UiJuice.Pane("Pane", frame, UiPalette.AceGlass.WithAlpha(0.6f), 18,
                true, true, true, UiPalette.AceRim, 200);

            var label = UiBuilder.Text("Label", frame, Loc.Pick("Box", "보관함"),
                UiTextRole.Overline, UiPalette.AceTextDim, TextAlignmentOptions.Left);
            UiBuilder.Anchor(label.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(18f, -14f), new Vector2(-36f, 30f));

            var list = UiBuilder.Rect("ListHost", frame, false);
            UiBuilder.Anchor(list, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            list.offsetMin = new Vector2(8f, 8f);
            list.offsetMax = new Vector2(-8f, -52f);

            UiBuilder.ScrollList("List", list, out _boxContent, 8f,
                new RectOffset(8, 8, 8, 8), 16);
            var scroll = list.GetComponentInChildren<ScrollRect>();
            if (scroll != null) UiBuilder.Stretch((RectTransform)scroll.transform);
        }

        private void BuildPurse(Transform safe)
        {
            var purse = UiBuilder.Rect("Purse", safe, false);
            UiBuilder.Anchor(purse, new Vector2(1f, 1f), new Vector2(1f, 1f),
                new Vector2(1f, 1f), Vector2.zero, new Vector2(400f, 76f));
            UiJuice.Pane("Pane", purse, UiPalette.AceGlass.WithAlpha(0.78f), 18,
                true, true, true, UiPalette.AceRim, 76);

            _purse = UiBuilder.Text("Text", purse, "", UiTextRole.Body,
                UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(_purse.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-24f, -12f));
            _purse.textWrappingMode = TextWrappingModes.NoWrap;
        }

        // --- Redraw ---------------------------------------------------------------------------

        private void Refresh()
        {
            if (!IsOpen) return;

            var session = OnlineSession.Instance;
            var roster = session != null ? session.Roster : Array.Empty<RosterEntry>();

            if (_purse != null)
            {
                var candies = session != null ? session.ItemCount(CandyItem) : 0;
                var discs = CountDiscs(session);
                _purse.text = Loc.Pick(
                    $"{(session != null ? session.Coins : 0):N0} coins  ·  {candies} candy  ·  {discs} discs",
                    $"코인 {(session != null ? session.Coins : 0):N0}  ·  사탕 {candies}  ·  디스크 {discs}");
            }

            // Selection survives a redraw where it can. Every action here rewrites the roster,
            // and dropping back to the top of the list after each 강화 would make levelling one
            // creature a scrolling exercise.
            if (_selected >= 0 && (session == null || session.Owned(_selected) == null)) _selected = -1;
            if (_selected < 0 && roster.Length > 0) _selected = roster[0].slot;

            if (_reorderLabel != null)
                _reorderLabel.text = _reordering
                    ? Loc.Pick("Done", "완료")
                    : Loc.Pick("Reorder", "순서 바꾸기");

            BuildTeam(session);
            BuildBox(session, roster);
            BuildDetail(session, _selected >= 0 && session != null ? session.Owned(_selected) : null);
        }

        private static int CountDiscs(OnlineSession session)
        {
            if (session?.Items == null) return 0;
            var total = 0;
            foreach (var item in session.Items)
                if (item != null && item.itemId != null && item.itemId.StartsWith(DiscPrefix)) total += item.count;
            return total;
        }

        // --- The battle team --------------------------------------------------------------------

        /// <summary>
        /// Six places, always six, whether or not somebody is standing in them.
        ///
        /// The empty ones are drawn rather than omitted because "you have four" is information,
        /// and a strip that simply got shorter would read as the screen having fewer slots
        /// rather than the team having fewer members.
        /// </summary>
        private void BuildTeam(OnlineSession session)
        {
            if (_teamRoot == null) return;
            UiBuilder.ClearChildren(_teamRoot);

            var party = session != null ? session.Party : Array.Empty<RosterEntry>();
            for (var i = 0; i < OnlineSession.PartySize; i++)
                BuildTeamTile(i < party.Length ? party[i] : null, i);
        }

        private void BuildTeamTile(RosterEntry entry, int index)
        {
            var cell = UiBuilder.Rect("Team_" + index, _teamRoot);
            UiBuilder.Size(cell, flexibleWidth: 1f, flexibleHeight: 1f, minWidth: 60f);

            var lifted = entry != null && _carry == entry.slot;
            var selected = entry != null && entry.slot == _selected && !_reordering;
            var target = _reordering && _carry >= 0 && !lifted;

            var face = lifted ? UiPalette.AceGold.WithAlpha(0.9f)
                : selected ? UiPalette.AceCyan.WithAlpha(0.88f)
                : target ? UiPalette.AceCyan.WithAlpha(0.26f)
                : UiPalette.AceGlass.WithAlpha(entry != null ? 0.66f : 0.28f);
            var rim = lifted ? UiPalette.AceGold
                : selected ? UiPalette.AceCyan
                : target ? UiPalette.AceCyan.WithAlpha(0.7f)
                : UiPalette.AceRim.WithAlpha(entry != null ? 1f : 0.3f);

            var pane = UiJuice.Pane("Pane", cell, face, 14, true, true, true, rim, 120);
            var ink = lifted || selected ? UiPalette.AceInk : UiPalette.AceText;

            // The ordinal IS the battle order, so it is the one thing on the tile that is never
            // allowed to be subtle.
            var badge = UiBuilder.Rect("Order", cell, false);
            UiBuilder.Anchor(badge, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(8f, -8f), new Vector2(30f, 26f));
            var badgeFace = UiBuilder.Image("Face", badge, UiSprites.Pill(22),
                entry != null ? UiPalette.AceGold : UiPalette.AceRim.WithAlpha(0.35f));
            UiBuilder.Stretch(badgeFace.rectTransform);
            var badgeText = UiBuilder.Text("Text", badge, (index + 1).ToString(),
                UiTextRole.Caption, UiPalette.AceInk, TextAlignmentOptions.Center);
            UiBuilder.Stretch(badgeText.rectTransform);

            if (entry == null)
            {
                var empty = UiBuilder.Text("Empty", cell, Loc.Pick("empty", "비어 있음"),
                    UiTextRole.Caption, UiPalette.AceTextFaint, TextAlignmentOptions.Center);
                UiBuilder.Anchor(empty.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-8f, -8f));
                empty.textWrappingMode = TextWrappingModes.NoWrap;

                // An empty place is still a destination: moving somebody onto it is how a team
                // of four is reordered at all.
                if (_reordering && _carry >= 0)
                {
                    var to = index;
                    UiButtonMotion.Attach(cell, 14);
                    UiBuilder.Button("Take", cell, pane.Fill, () => MoveCarried(to));
                }
                return;
            }

            var portrait = Portraits.Of(entry.speciesId);
            if (portrait != null)
            {
                var image = UiBuilder.Image("Portrait", cell, portrait, Color.white, Image.Type.Simple);
                UiBuilder.Anchor(image.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0.5f, 1f), new Vector2(0f, -28f), new Vector2(70f, 70f));
                image.preserveAspect = true;
            }

            var level = UiBuilder.Text("Level", cell, "Lv " + entry.level, UiTextRole.Caption,
                ink, TextAlignmentOptions.Center);
            UiBuilder.Anchor(level.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 30f), new Vector2(-8f, 26f));
            level.textWrappingMode = TextWrappingModes.NoWrap;

            var stars = UiBuilder.Text("Stars", cell, Stars(entry.stars), UiTextRole.Caption,
                lifted || selected ? UiPalette.AceInk.WithAlpha(0.72f) : UiPalette.AceGold,
                TextAlignmentOptions.Center);
            UiBuilder.Anchor(stars.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 8f), new Vector2(-8f, 22f));
            stars.textWrappingMode = TextWrappingModes.NoWrap;

            var slot = entry.slot;
            UiButtonMotion.Attach(cell, 14);
            UiBuilder.Button("Take", cell, pane.Fill, () => TapTeam(slot, index));
        }

        // --- Reordering -------------------------------------------------------------------------

        private void ToggleReorder()
        {
            _reordering = !_reordering;
            _carry = -1;
            UiSound.Navigate();
            Say(_reordering
                ? Loc.Pick("Pick the one to move.", "옆길 포켓몬을 고르세요. 1번이 선두예요.")
                : "");
            Refresh();
        }

        /// <summary>A tap on an occupied team tile: select it, lift it, or land on it.</summary>
        private void TapTeam(int slot, int index)
        {
            if (!_reordering) { Select(slot); return; }

            if (_carry < 0)
            {
                _carry = slot;
                UiSound.Navigate();
                Say(Loc.Pick("Now pick where it goes.", "이제 놓을 자리를 고르세요."));
                Refresh();
                return;
            }

            if (_carry == slot)
            {
                _carry = -1;
                UiSound.Navigate();
                Say(Loc.Pick("Pick the one to move.", "옆길 포켓몬을 고르세요."));
                Refresh();
                return;
            }

            MoveCarried(index);
        }

        /// <summary>
        /// Moves the lifted creature to <paramref name="index"/> and commits the whole order.
        ///
        /// A move, not a swap. Dropping 5 onto 1 with a swap would send 1 to the back, which is
        /// not what dragging a name to the top of a list has ever meant; the rest shuffle down
        /// by one instead. The full order is posted because that is the shape /party/set takes,
        /// and because a party rewritten wholesale cannot half-apply into two creatures both
        /// claiming to lead.
        /// </summary>
        private void MoveCarried(int index)
        {
            var session = OnlineSession.Instance;
            if (session == null || _carry < 0) return;

            var order = new List<int>(OnlineSession.PartySize);
            foreach (var member in session.Party) if (member != null) order.Add(member.slot);

            var from = order.IndexOf(_carry);
            if (from < 0) { _carry = -1; Refresh(); return; }

            var to = Mathf.Clamp(index, 0, order.Count - 1);
            if (to == from) { _carry = -1; Refresh(); return; }

            order.RemoveAt(from);
            order.Insert(to, _carry);

            _selected = _carry;
            _carry = -1;
            Act((s, done) => s.SetParty(order.ToArray(), done),
                Loc.Pick("Order changed.", "전투 순서를 바꿠어요."));
        }

        // --- The box --------------------------------------------------------------------------

        /// <summary>
        /// Everything owned, party included, laid out as tiles.
        ///
        /// The party is not hidden from the box. A collection screen that showed only the bench
        /// would make the six disappear from the count a player is looking at, and the tiles are
        /// where the shard and star marks live -- which is exactly what you consult before
        /// deciding who to bring.
        /// </summary>
        private void BuildBox(OnlineSession session, RosterEntry[] roster)
        {
            if (_boxContent == null) return;
            UiBuilder.ClearChildren(_boxContent);

            if (roster == null || roster.Length == 0)
            {
                var empty = UiBuilder.Text("Empty", _boxContent,
                    Loc.Pick("Nothing collected yet. Open the gacha.",
                             "아직 아무도 없어요. 가챠에서 뽑아 보세요."),
                    UiTextRole.Secondary, UiPalette.AceTextFaint, TextAlignmentOptions.Center);
                UiBuilder.Size(empty.rectTransform, preferredHeight: 120f, minHeight: 120f);
                return;
            }

            // Party first and in battle order, then the bench by draw order.
            var ordered = new List<RosterEntry>(roster);
            ordered.Sort((a, b) =>
            {
                var partyA = a.InParty ? a.partySlot : int.MaxValue;
                var partyB = b.InParty ? b.partySlot : int.MaxValue;
                if (partyA != partyB) return partyA.CompareTo(partyB);
                return a.slot.CompareTo(b.slot);
            });

            RectTransform row = null;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (i % BoxColumns == 0)
                {
                    row = UiBuilder.Rect("Row_" + (i / BoxColumns), _boxContent);
                    UiBuilder.Horizontal(row, 8f, null, TextAnchor.MiddleLeft, true, true);
                    UiBuilder.Size(row, preferredHeight: 132f, minHeight: 132f, flexibleWidth: 1f);
                }
                BuildBoxTile(row, ordered[i]);
            }

            // The last row is padded to a full width of tiles so four creatures do not stretch
            // into four billboards.
            var remainder = ordered.Count % BoxColumns;
            if (remainder != 0 && row != null)
                for (var i = remainder; i < BoxColumns; i++)
                {
                    var filler = UiBuilder.Rect("Filler_" + i, row);
                    UiBuilder.Size(filler, flexibleWidth: 1f, flexibleHeight: 1f);
                }
        }

        private void BuildBoxTile(RectTransform row, RosterEntry entry)
        {
            var cell = UiBuilder.Rect("Box_" + entry.slot, row);
            UiBuilder.Size(cell, flexibleWidth: 1f, flexibleHeight: 1f, minWidth: 60f);

            var selected = entry.slot == _selected;
            var pane = UiJuice.Pane("Pane", cell,
                selected ? UiPalette.AceCyan.WithAlpha(0.88f) : UiPalette.AceGlass.WithAlpha(0.66f),
                14, true, true, true, selected ? UiPalette.AceCyan : UiPalette.AceRim, 120);
            var ink = selected ? UiPalette.AceInk : UiPalette.AceText;

            var portrait = Portraits.Of(entry.speciesId);
            if (portrait != null)
            {
                var image = UiBuilder.Image("Portrait", cell, portrait, Color.white, Image.Type.Simple);
                UiBuilder.Anchor(image.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(64f, 64f));
                image.preserveAspect = true;
            }

            var name = UiBuilder.Text("Name", cell, Names.Of(entry.speciesId), UiTextRole.Caption,
                ink, TextAlignmentOptions.Center);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 34f), new Vector2(-6f, 24f));
            name.textWrappingMode = TextWrappingModes.NoWrap;

            var level = UiBuilder.Text("Level", cell, "Lv " + entry.level, UiTextRole.Caption,
                selected ? UiPalette.AceInk.WithAlpha(0.72f) : UiPalette.AceTextDim,
                TextAlignmentOptions.Center);
            UiBuilder.Anchor(level.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 10f), new Vector2(-6f, 22f));
            level.textWrappingMode = TextWrappingModes.NoWrap;

            if (entry.InParty)
            {
                var badge = UiBuilder.Rect("Party", cell, false);
                UiBuilder.Anchor(badge, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, 1f), new Vector2(6f, -6f), new Vector2(28f, 24f));
                var badgeFace = UiBuilder.Image("Face", badge, UiSprites.Pill(20), UiPalette.AceGold);
                UiBuilder.Stretch(badgeFace.rectTransform);
                var badgeText = UiBuilder.Text("Text", badge, (entry.partySlot + 1).ToString(),
                    UiTextRole.Caption, UiPalette.AceInk, TextAlignmentOptions.Center);
                UiBuilder.Stretch(badgeText.rectTransform);
            }

            if (entry.shards > 0)
            {
                var shards = UiBuilder.Text("Shards", cell, "◆ " + entry.shards, UiTextRole.Caption,
                    selected ? UiPalette.AceInk.WithAlpha(0.8f) : UiPalette.AceViolet,
                    TextAlignmentOptions.Right);
                UiBuilder.Anchor(shards.rectTransform, new Vector2(1f, 1f), new Vector2(1f, 1f),
                    new Vector2(1f, 1f), new Vector2(-8f, -6f), new Vector2(60f, 24f));
                shards.textWrappingMode = TextWrappingModes.NoWrap;
            }

            var slot = entry.slot;
            UiButtonMotion.Attach(cell, 14);
            UiBuilder.Button("Take", cell, pane.Fill, () => Select(slot));
        }

        private void Select(int slot)
        {
            if (_selected == slot) return;
            _selected = slot;
            UiSound.Navigate();
            Say("");
            Refresh();
        }

        // --- The one creature ------------------------------------------------------------------

        private void BuildDetail(OnlineSession session, RosterEntry entry)
        {
            UiBuilder.ClearChildren(_detail);
            if (entry == null) return;

            var cap = LevelCap(entry.stars);
            var atCap = entry.level >= cap;

            var head = UiBuilder.Text("Name", _detail, Names.Of(entry.speciesId), UiTextRole.Title,
                UiPalette.AceText, TextAlignmentOptions.Left);
            UiBuilder.Anchor(head.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), Vector2.zero, new Vector2(0f, 64f));

            var line = UiBuilder.Text("Line", _detail,
                Loc.Pick($"Lv {entry.level} / {cap}   {Stars(entry.stars)}   ◆ {entry.shards}",
                         $"Lv {entry.level} / {cap}   {Stars(entry.stars)}   ◆ {entry.shards}"),
                UiTextRole.Body,
                atCap ? UiPalette.AceGold : UiPalette.AceTextDim, TextAlignmentOptions.Left);
            UiBuilder.Anchor(line.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, -66f), new Vector2(0f, 40f));

            var coins = session != null ? session.Coins : 0;
            var candies = session != null ? session.ItemCount(CandyItem) : 0;
            var slot = entry.slot;

            // --- The four buttons ---------------------------------------------------------
            var enhanceCost = EnhanceCost(entry.level);
            ActionButton(_detail, 0, Loc.Pick("Enhance", "강화"),
                atCap
                    ? Loc.Pick("At its ceiling. Break through first.", "한계예요. 먼저 돌파해 주세요.")
                    : Loc.Pick($"One level · {enhanceCost:N0} coins", $"한 레벨 · 코인 {enhanceCost:N0}"),
                UiPalette.AceLime, !atCap && coins >= enhanceCost,
                () => Act((s, done) => s.Enhance(slot, done), Loc.Pick("Enhanced.", "강화했어요.")));

            var starsNow = Mathf.Clamp(entry.stars, 0, MaxStars);
            var maxed = starsNow >= MaxStars;
            var needShards = maxed ? 0 : BreakthroughShards[starsNow];
            var needCoins = maxed ? 0 : BreakthroughCoins[starsNow];
            ActionButton(_detail, 1, Loc.Pick("Breakthrough", "돌파"),
                maxed
                    ? Loc.Pick("Fully broken through.", "돌파를 마쳤어요.")
                    : Loc.Pick($"◆ {needShards} · {needCoins:N0} coins → Lv {LevelCap(starsNow + 1)}",
                               $"◆ {needShards} · 코인 {needCoins:N0} → Lv {LevelCap(starsNow + 1)}"),
                UiPalette.AceViolet,
                !maxed && entry.shards >= needShards && coins >= needCoins,
                () => Act((s, done) => s.Breakthrough(slot, done), Loc.Pick("Broke through.", "돌파했어요.")));

            ActionButton(_detail, 2, Loc.Pick("Rare Candy", "이상한 사탕"),
                candies <= 0
                    ? Loc.Pick("None in the bag. Battles drop them.", "가방에 없어요. 대전에서 가끔 나와요.")
                    : atCap
                        ? Loc.Pick("At its ceiling.", "한계예요.")
                        : Loc.Pick($"One level · {candies} left", $"한 레벨 · {candies}개 남음"),
                UiPalette.AceGold, candies > 0 && !atCap,
                () => Act((s, done) => s.FeedCandy(slot, done), Loc.Pick("One level up.", "레벨이 올랐어요.")));

            var inParty = entry.InParty;
            ActionButton(_detail, 3,
                inParty ? Loc.Pick("Remove from party", "파티에서 빼기") : Loc.Pick("Add to party", "파티에 넣기"),
                inParty
                    ? Loc.Pick($"Fighting in slot {entry.partySlot + 1}.", $"{entry.partySlot + 1}번으로 싸우고 있어요.")
                    : Loc.Pick("Six fight at a time.", "한 번에 여섯 마리가 싸워요."),
                UiPalette.AceCyan, true, () => ToggleParty(entry));

            // --- The moveset --------------------------------------------------------------
            var movesLabel = UiBuilder.Text("MovesLabel", _detail, Loc.Pick("Moves", "기술"),
                UiTextRole.Overline, UiPalette.AceTextDim, TextAlignmentOptions.Left);
            // Below the last action row, which ends at 454 from the top: 120 + 3 * (76 + 10).
            // Written as a number rather than measured because the four actions are fixed and a
            // layout group here would fight the absolute anchoring the rows above use.
            UiBuilder.Anchor(movesLabel.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(0f, -466f), new Vector2(0f, 30f));

            var known = KnownMoves(entry);
            for (var i = 0; i < MoveSlots; i++) BuildMoveSlot(entry, known, i);
        }

        /// <summary>
        /// One action, as a full-width row. The reason it cannot be pressed is written on it.
        ///
        /// A disabled button with no sentence beside it is a button the player presses three
        /// times before giving up on the screen, so the subtitle always says what would make it
        /// work — how many coins, how many pieces, or that the ceiling is the thing in the way.
        /// </summary>
        private void ActionButton(Transform parent, int index, string label, string note,
                                  Color accent, bool enabled, Action onClick)
        {
            const float height = 76f;
            const float gap = 10f;

            var row = UiBuilder.Rect("Action_" + index, parent, false);
            UiBuilder.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -(120f + index * (height + gap))), new Vector2(0f, height));

            var pane = UiJuice.Pane("Pane", row,
                enabled ? accent.WithAlpha(0.86f) : UiPalette.AceGlass.WithAlpha(0.34f), 16,
                true, true, true, enabled ? accent : UiPalette.AceRim.WithAlpha(0.3f), (int)height);

            var ink = enabled ? UiPalette.AceInk : UiPalette.AceTextFaint;

            var title = UiBuilder.Text("Label", row, label, UiTextRole.Body, ink,
                TextAlignmentOptions.Left);
            UiBuilder.Anchor(title.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(24f, 13f), new Vector2(-48f, 32f));
            title.textWrappingMode = TextWrappingModes.NoWrap;

            var sub = UiBuilder.Text("Note", row, note, UiTextRole.Caption,
                enabled ? UiPalette.AceInk.WithAlpha(0.74f) : UiPalette.AceTextFaint,
                TextAlignmentOptions.Left);
            UiBuilder.Anchor(sub.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(24f, -16f), new Vector2(-48f, 26f));
            sub.textWrappingMode = TextWrappingModes.NoWrap;

            if (!enabled) return;
            UiButtonMotion.Attach(row, 16);
            UiBuilder.Button("Take", row, pane.Fill, onClick);
        }

        /// <summary>
        /// The four move slots.
        ///
        /// Pressing one opens the disc list for THAT slot, so teaching is "put this here" rather
        /// than "teach it and find out what it replaced". Empty is a real state: a creature below
        /// the level that fills all four genuinely knows fewer than four.
        /// </summary>
        private void BuildMoveSlot(RosterEntry entry, IReadOnlyList<string> known, int index)
        {
            const float height = 52f;
            const float gap = 6f;

            var row = UiBuilder.Rect("Move_" + index, _detail, false);
            UiBuilder.Anchor(row, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, -(502f + index * (height + gap))), new Vector2(0f, height));

            var pane = UiJuice.Pane("Pane", row, UiPalette.AceGlass.WithAlpha(0.6f), 14,
                true, true, true, UiPalette.AceRim, (int)height);

            var moveId = index < known.Count ? known[index] : "";
            var filled = !string.IsNullOrEmpty(moveId);

            var label = UiBuilder.Text("Label", row,
                filled ? UiServices.MoveName(moveId) : Loc.Pick("— empty —", "— 비어 있음 —"),
                UiTextRole.Body, filled ? UiPalette.AceText : UiPalette.AceTextFaint,
                TextAlignmentOptions.Left);
            UiBuilder.Anchor(label.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f),
                Vector2.zero, Vector2.zero);
            label.rectTransform.offsetMin = new Vector2(24f, 0f);
            label.rectTransform.offsetMax = new Vector2(-140f, 0f);
            label.textWrappingMode = TextWrappingModes.NoWrap;

            var hint = UiBuilder.Text("Hint", row, Loc.Pick("Teach", "가르치기"), UiTextRole.Caption,
                UiPalette.AceCyan, TextAlignmentOptions.Right);
            UiBuilder.Anchor(hint.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(130f, 30f));

            var slot = index;
            UiButtonMotion.Attach(row, 14);
            UiBuilder.Button("Take", row, pane.Fill, () => OpenDiscs(entry, slot));
        }

        /// <summary>
        /// What this creature knows: the taught list when there is one, the learnset otherwise.
        ///
        /// Exactly the rule the Worker's defaultMoves applies, and it has to be — a creature
        /// whose four moves this screen and the server disagree about is a creature that fights
        /// differently depending on who is looking.
        /// </summary>
        private static IReadOnlyList<string> KnownMoves(RosterEntry entry)
        {
            if (!string.IsNullOrEmpty(entry.moves))
            {
                var taught = entry.moves.Split(',');
                var list = new List<string>(MoveSlots);
                for (var i = 0; i < MoveSlots; i++)
                    list.Add(i < taught.Length ? taught[i].Trim() : "");
                return list;
            }

            var derived = new List<string>(MoveSlots);
            var registry = UiServices.Moves;
            if (registry != null)
            {
                var learned = registry.MovesFor(entry.speciesId, entry.level);
                if (learned != null)
                    foreach (var move in learned)
                        if (move != null) derived.Add(move.Id);
            }
            while (derived.Count < MoveSlots) derived.Add("");
            return derived;
        }

        // --- Discs ------------------------------------------------------------------------------

        /// <summary>
        /// The discs that could go into this slot.
        ///
        /// Filtered twice, and the second filter is the user's rule: 막 모든 포켓몬이 막 모든
        /// 디스크를 배울 수 있다 X. Only discs actually in the bag are listed at all, and one this
        /// species cannot learn is listed greyed with the reason on it rather than hidden — a
        /// player holding a disc needs to be able to find out who it is FOR, and a list that
        /// silently omits it just looks like the disc went missing.
        /// </summary>
        private void OpenDiscs(RosterEntry entry, int moveSlot)
        {
            var session = OnlineSession.Instance;
            if (session == null) { UiSound.Error(); return; }

            UiSound.MenuOpen();
            UiBuilder.ClearChildren(_discsRoot);
            _discsRoot.gameObject.SetActive(true);
            _discsRoot.SetAsLastSibling();

            // Tapping the scrim dismisses. The Button goes on the SCRIM's own object rather
            // than on the root: with it on the root, a click anywhere the card does not itself
            // handle would bubble up and close the list the player is reading.
            var scrim = UiBuilder.Backdrop("Scrim", _discsRoot, null,
                UiPalette.AceNight.WithAlpha(0.86f), true);
            UiBuilder.Stretch(scrim.rectTransform);
            UiBuilder.Button("Dismiss", scrim.rectTransform, scrim, CloseDiscs);

            var card = UiBuilder.Rect("Card", _discsRoot, false);

            // And the card stops clicks reaching the scrim underneath it. Every Image UiJuice
            // builds has raycastTarget off, so without this the card is a hole.
            UiBuilder.Backdrop("Block", card, null, new Color(0f, 0f, 0f, 0f), true);
            UiBuilder.Anchor(card, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(880f, 720f));
            UiJuice.Pane("Pane", card, UiPalette.AceGlass.WithAlpha(0.94f), 22,
                true, true, true, UiPalette.AceRim, 720);

            var head = UiBuilder.Text("Head", card,
                Loc.Pick($"Teach into slot {moveSlot + 1}", $"{moveSlot + 1}번 자리에 가르치기"),
                UiTextRole.Heading, UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(head.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0.5f, 1f), new Vector2(0f, -22f), new Vector2(-40f, 48f));

            var frame = UiBuilder.Rect("Frame", card, false);
            UiBuilder.Anchor(frame, Vector2.zero, Vector2.one, new Vector2(0.5f, 0.5f),
                Vector2.zero, Vector2.zero);
            frame.offsetMin = new Vector2(18f, 84f);
            frame.offsetMax = new Vector2(-18f, -84f);

            UiBuilder.ScrollList("Discs", frame, out var content, 8f,
                new RectOffset(10, 10, 10, 10), 16);
            var scroll = frame.GetComponentInChildren<ScrollRect>();
            if (scroll != null) UiBuilder.Stretch((RectTransform)scroll.transform);

            var registry = UiServices.Moves;
            var learnable = registry != null ? registry.LearnableBy(entry.speciesId) : null;

            var any = false;
            foreach (var item in session.Items ?? Array.Empty<OwnedItem>())
            {
                if (item == null || item.count <= 0) continue;
                if (item.itemId == null || !item.itemId.StartsWith(DiscPrefix)) continue;

                var moveId = item.itemId.Substring(DiscPrefix.Length);
                var teachable = Contains(learnable, moveId);
                BuildDiscRow(content, entry, moveId, item.count, teachable, moveSlot);
                any = true;
            }

            if (!any)
            {
                var empty = UiBuilder.Text("Empty", content,
                    Loc.Pick("No discs yet. Battles drop them.", "아직 디스크가 없어요. 대전에서 가끔 나와요."),
                    UiTextRole.Secondary, UiPalette.AceTextFaint, TextAlignmentOptions.Center);
                UiBuilder.Size(empty.rectTransform, preferredHeight: 100f, minHeight: 100f);
            }

            var close = UiBuilder.Rect("Close", card, false);
            UiBuilder.Anchor(close, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(0.5f, 0f), new Vector2(0f, 18f), new Vector2(240f, 54f));
            var closePane = UiJuice.Pane("Pane", close, UiPalette.AceGlass.WithAlpha(0.72f), 16,
                true, true, true, UiPalette.AceRim, 54);
            var closeLabel = UiBuilder.Text("Label", close, Loc.Pick("Back", "뒤로"),
                UiTextRole.Body, UiPalette.AceText, TextAlignmentOptions.Center);
            UiBuilder.Anchor(closeLabel.rectTransform, Vector2.zero, Vector2.one,
                new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(-24f, -12f));
            UiBuilder.Button("Take", close, closePane.Fill, CloseDiscs);
        }

        private static bool Contains(IReadOnlyList<MoveData> moves, string moveId)
        {
            if (moves == null || string.IsNullOrEmpty(moveId)) return false;
            for (var i = 0; i < moves.Count; i++)
                if (moves[i] != null && moves[i].Id == moveId) return true;
            return false;
        }

        private void BuildDiscRow(RectTransform parent, RosterEntry entry, string moveId, int count,
                                  bool teachable, int moveSlot)
        {
            var row = UiBuilder.Rect("Disc_" + moveId, parent);
            UiBuilder.Size(row, preferredHeight: 64f, minHeight: 64f);

            var pane = UiJuice.Pane("Pane", row,
                teachable ? UiPalette.AceGlass.WithAlpha(0.72f) : UiPalette.AceGlass.WithAlpha(0.3f),
                14, true, true, true,
                teachable ? UiPalette.AceRim : UiPalette.AceRim.WithAlpha(0.25f), 64);

            var move = UiServices.MoveOf(moveId);
            var accent = move != null ? UiPalette.Type(move.Type) : UiPalette.AceRim;

            var chip = UiBuilder.Image("Type", row, UiSprites.Pill(20),
                teachable ? accent : accent.WithAlpha(0.3f));
            UiBuilder.Anchor(chip.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(16f, 0f), new Vector2(10f, 40f));

            var name = UiBuilder.Text("Name", row, UiServices.MoveName(moveId), UiTextRole.Body,
                teachable ? UiPalette.AceText : UiPalette.AceTextFaint, TextAlignmentOptions.Left);
            UiBuilder.Anchor(name.rectTransform, Vector2.zero, Vector2.one, new Vector2(0f, 0.5f),
                Vector2.zero, Vector2.zero);
            name.rectTransform.offsetMin = new Vector2(38f, 0f);
            name.rectTransform.offsetMax = new Vector2(-210f, 0f);
            name.textWrappingMode = TextWrappingModes.NoWrap;

            var right = UiBuilder.Text("Right", row,
                teachable
                    ? "x" + count
                    : Loc.Pick("cannot learn", "배울 수 없음"),
                UiTextRole.Caption,
                teachable ? UiPalette.AceTextDim : UiPalette.AceRed, TextAlignmentOptions.Right);
            UiBuilder.Anchor(right.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-20f, 0f), new Vector2(190f, 30f));

            if (!teachable) return;

            var slot = entry.slot;
            var id = moveId;
            UiButtonMotion.Attach(row, 14);
            UiBuilder.Button("Take", row, pane.Fill, () =>
            {
                CloseDiscs();
                Act((s, done) => s.Teach(slot, moveSlot, id, done),
                    Loc.Pick($"Learned {UiServices.MoveName(id)}.",
                             $"{UiServices.MoveName(id)}을(를) 배웠어요."));
            });
        }

        private void CloseDiscs()
        {
            if (_discsRoot == null || !_discsRoot.gameObject.activeSelf) return;
            UiSound.MenuClose();
            _discsRoot.gameObject.SetActive(false);
        }

        // --- The party ---------------------------------------------------------------------------

        /// <summary>
        /// Puts this creature into the party, or takes it out.
        ///
        /// The whole party is sent every time rather than a single change, because that is the
        /// shape the route takes and it is the shape that cannot half-apply: two creatures both
        /// claiming slot 3 is a team of five, discovered at the arena door.
        /// </summary>
        private void ToggleParty(RosterEntry entry)
        {
            var session = OnlineSession.Instance;
            if (session == null) return;

            var party = new List<int>(OnlineSession.PartySize);
            foreach (var member in session.Party)
                if (member != null && member.slot != entry.slot) party.Add(member.slot);

            if (!entry.InParty)
            {
                if (party.Count >= OnlineSession.PartySize)
                {
                    UiSound.Error();
                    Say(Loc.Pick("The party is full. Take somebody out first.",
                                 "파티가 가득 찼어요. 먼저 한 마리를 빼 주세요."));
                    return;
                }
                party.Add(entry.slot);
            }
            else if (party.Count == 0)
            {
                // Refused here rather than by the Worker, which answers "empty_party" — a battle
                // with nobody in it cannot start, so the last one out has nowhere to go.
                UiSound.Error();
                Say(Loc.Pick("Somebody has to fight.", "적어도 한 마리는 있어야 해요."));
                return;
            }

            Act((s, done) => s.SetParty(party.ToArray(), done),
                entry.InParty
                    ? Loc.Pick("Taken out of the party.", "파티에서 뺐어요.")
                    : Loc.Pick("Added to the party.", "파티에 넣었어요."));
        }

        // --- Doing it ------------------------------------------------------------------------------

        /// <summary>
        /// Runs one growth call and reports it.
        ///
        /// Every route answers with the whole account and the session absorbs it, so success
        /// needs nothing here but a redraw and a sentence. Failure gets the Worker's own reason
        /// translated — the useful ones are "not enough" answers, and a player who is told which
        /// number was short knows what to go and do about it.
        /// </summary>
        private void Act(Func<OnlineSession, Action<RosterResponse>, System.Collections.IEnumerator> call,
                         string success)
        {
            var session = OnlineSession.Instance;
            if (session == null || session.Busy) { UiSound.Error(); return; }

            UiSound.Confirm();
            Say(Loc.Pick("Working…", "적용하는 중…"));

            StartCoroutine(Run(session, call, success));
        }

        /// <summary>
        /// Runs one growth call and reports what happened.
        ///
        /// <b>Success is read off the REPLY, never inferred.</b> The first version of this
        /// compared LastError against a list and checked whether the purse had moved, and that
        /// is wrong in a way that only shows up after the player has already been refused once:
        /// LastError is sticky — it holds whatever failed most recently and a success does not
        /// clear it — and the two calls that spend nothing, teaching and setting the party, leave
        /// the purse exactly where it was. So a party change made after a failed 강화 would have
        /// been announced as "코인이 부족해요" while quietly succeeding.
        ///
        /// The routes already answer with the whole account on success and null on failure. That
        /// IS the flag; nothing else needs to be deduced.
        /// </summary>
        private System.Collections.IEnumerator Run(OnlineSession session,
            Func<OnlineSession, Action<RosterResponse>, System.Collections.IEnumerator> call,
            string success)
        {
            RosterResponse reply = null;
            yield return call(session, r => reply = r);

            if (reply == null)
            {
                UiSound.Error();
                Say(Explain(session.LastError));
            }
            else
            {
                Say(success);
            }

            Refresh();
        }

        private static string Explain(string error) => error switch
        {
            "not_enough_coins" => Loc.Pick("Not enough coins.", "코인이 부족해요."),
            "not_enough_shards" => Loc.Pick("Not enough pieces. Pull a duplicate.",
                                            "돌파 조각이 부족해요. 같은 포켓몬을 한 번 더 뽑아 보세요."),
            "no_candy" => Loc.Pick("No candy in the bag.", "이상한 사탕이 없어요."),
            "no_disc" => Loc.Pick("That disc is not in the bag.", "그 디스크가 없어요."),
            "cannot_learn" => Loc.Pick("This one cannot learn that.", "이 포켓몬은 그 기술을 배울 수 없어요."),
            "at_level_cap" => Loc.Pick("At its ceiling. Break through first.", "한계예요. 먼저 돌파해 주세요."),
            "at_max_stars" => Loc.Pick("Fully broken through.", "이미 돌파를 마쳤어요."),
            "already_known" => Loc.Pick("It already knows that.", "이미 알고 있어요."),
            "empty_party" => Loc.Pick("Somebody has to fight.", "적어도 한 마리는 있어야 해요."),
            _ => OnlineClient.Explain(error),
        };

        private void Say(string message)
        {
            if (_status != null) _status.text = message ?? "";
        }

        // --- Numbers, restated ---------------------------------------------------------------------

        /// <summary>Level ceiling at a star count. Matches economy.ts levelCap.</summary>
        private static int LevelCap(int stars) => Mathf.Min(100, 40 + 12 * Mathf.Clamp(stars, 0, MaxStars));

        /// <summary>What one 강화 costs. Matches economy.ts enhanceCost.</summary>
        private static int EnhanceCost(int level) =>
            Mathf.Max(40, Mathf.RoundToInt(18f * level + 0.9f * level * level));

        private static string Stars(int stars)
        {
            var filled = Mathf.Clamp(stars, 0, MaxStars);
            return new string('★', filled) + new string('☆', MaxStars - filled);
        }

        // --- Look-ups -------------------------------------------------------------------------------

        /// <summary>Species names, through the same registry every other screen reads.</summary>
        private static class Names
        {
            public static string Of(int speciesId) => UiServices.SpeciesName(speciesId);
        }

        /// <summary>
        /// Portraits, through the same catalog the gacha reveal uses.
        ///
        /// CreatureThumbnail is the one lookup in the project that finds the `*_portrait.png`
        /// files, which sit outside any Resources folder — see GachaPanel.Portrait for why
        /// ICreatureArtRegistry answers null here.
        /// </summary>
        private static class Portraits
        {
            public static Sprite Of(int speciesId) => CreatureThumbnail.Front(speciesId);
        }

        private void Update()
        {
            if (!IsOpen) return;
            var keyboard = UnityEngine.InputSystem.Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.escapeKey.wasPressedThisFrame)
            {
                // Innermost surface first: the disc list, then the panel.
                if (_discsRoot != null && _discsRoot.gameObject.activeSelf) CloseDiscs();
                else if (_reordering) ToggleReorder();
                else Close();
            }
        }
    }
}
