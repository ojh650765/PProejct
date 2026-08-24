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

        private RectTransform _listContent;
        private RectTransform _detail;
        private RectTransform _discsRoot;
        private TextMeshProUGUI _status;
        private TextMeshProUGUI _purse;

        /// <summary>The collection slot being looked at. -1 before anything is selected.</summary>
        private int _selected = -1;

        private void Awake() => gameObject.SetActive(false);

        public void Open()
        {
            OnlineSession.Ensure();
            gameObject.SetActive(true);
            IsOpen = true;
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

            // Left column: the collection.
            var listFrame = UiBuilder.Rect("ListFrame", safe, false);
            UiBuilder.Anchor(listFrame, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(0f, 0.5f),
                new Vector2(0f, -70f), new Vector2(560f, -260f));
            UiJuice.Pane("Pane", listFrame, UiPalette.AceGlass.WithAlpha(0.6f), 18,
                true, true, true, UiPalette.AceRim, 200);

            UiBuilder.ScrollList("List", listFrame, out _listContent, 10f,
                new RectOffset(14, 14, 14, 14), 18);
            var scroll = listFrame.GetComponentInChildren<ScrollRect>();
            if (scroll != null) UiBuilder.Stretch((RectTransform)scroll.transform, 6f);

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

            BuildList(roster);
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

        private void BuildList(RosterEntry[] roster)
        {
            UiBuilder.ClearChildren(_listContent);

            if (roster == null || roster.Length == 0)
            {
                var empty = UiBuilder.Text("Empty", _listContent,
                    Loc.Pick("Nothing collected yet. Open the gacha.", "아직 아무도 없어요. 가챠에서 뽑아 보세요."),
                    UiTextRole.Secondary, UiPalette.AceTextFaint, TextAlignmentOptions.Center);
                UiBuilder.Size(empty.rectTransform, preferredHeight: 120f, minHeight: 120f);
                return;
            }

            // Party first, then the bench, each in slot order. The six that fight are the six a
            // player is here to look after, and a collection sorted only by draw order buries
            // them under whatever was pulled last.
            var ordered = new List<RosterEntry>(roster);
            ordered.Sort((a, b) =>
            {
                var partyA = a.InParty ? a.partySlot : int.MaxValue;
                var partyB = b.InParty ? b.partySlot : int.MaxValue;
                if (partyA != partyB) return partyA.CompareTo(partyB);
                return a.slot.CompareTo(b.slot);
            });

            foreach (var entry in ordered) BuildListRow(entry);
        }

        private void BuildListRow(RosterEntry entry)
        {
            var selected = entry.slot == _selected;

            var row = UiBuilder.Rect("Row_" + entry.slot, _listContent);
            UiBuilder.Size(row, preferredHeight: 84f, minHeight: 84f);

            var pane = UiJuice.Pane("Pane", row,
                selected ? UiPalette.AceCyan.WithAlpha(0.88f) : UiPalette.AceGlass.WithAlpha(0.66f),
                14, true, true, true,
                selected ? UiPalette.AceCyan : UiPalette.AceRim, 84);

            var ink = selected ? UiPalette.AceInk : UiPalette.AceText;

            var portrait = Portraits.Of(entry.speciesId);
            if (portrait != null)
            {
                var image = UiBuilder.Image("Portrait", row, portrait, Color.white, Image.Type.Simple);
                UiBuilder.Anchor(image.rectTransform, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                    new Vector2(0.5f, 0.5f), new Vector2(48f, 0f), new Vector2(64f, 64f));
                image.preserveAspect = true;
            }

            var name = UiBuilder.Text("Name", row, Names.Of(entry.speciesId), UiTextRole.Body,
                ink, TextAlignmentOptions.Left);
            UiBuilder.Anchor(name.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(90f, 12f), new Vector2(-260f, 34f));
            name.textWrappingMode = TextWrappingModes.NoWrap;

            var sub = UiBuilder.Text("Sub", row,
                Stars(entry.stars) + (entry.shards > 0 ? "   ◆ " + entry.shards : ""),
                UiTextRole.Caption,
                selected ? UiPalette.AceInk.WithAlpha(0.72f) : UiPalette.AceTextDim,
                TextAlignmentOptions.Left);
            UiBuilder.Anchor(sub.rectTransform, new Vector2(0f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(0f, 0.5f), new Vector2(90f, -16f), new Vector2(-260f, 28f));
            sub.textWrappingMode = TextWrappingModes.NoWrap;

            var level = UiBuilder.Text("Level", row, "Lv " + entry.level, UiTextRole.Numeric,
                ink, TextAlignmentOptions.Right);
            UiBuilder.Anchor(level.rectTransform, new Vector2(1f, 0.5f), new Vector2(1f, 0.5f),
                new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(150f, 40f));

            if (entry.InParty)
            {
                var badge = UiBuilder.Rect("Party", row, false);
                UiBuilder.Anchor(badge, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(0f, 1f), new Vector2(10f, -8f), new Vector2(34f, 26f));
                var badgeFace = UiBuilder.Image("Face", badge, UiSprites.Pill(22), UiPalette.AceGold);
                UiBuilder.Stretch(badgeFace.rectTransform);
                var badgeText = UiBuilder.Text("Text", badge, (entry.partySlot + 1).ToString(),
                    UiTextRole.Caption, UiPalette.AceInk, TextAlignmentOptions.Center);
                UiBuilder.Stretch(badgeText.rectTransform);
            }

            var slot = entry.slot;
            UiButtonMotion.Attach(row, 14);
            UiBuilder.Button("Take", row, pane.Fill, () => Select(slot));
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
                else Close();
            }
        }
    }
}
