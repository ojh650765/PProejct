using System.Collections.Generic;
using System.Text;
using PokeLab.Core;
using PokeLab.Overworld;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PokeLab.Boot
{
    /// <summary>
    /// Escape opens the trainer's menu: 도감, 포켓몬, 가방, their own card, 리포트, 설정, 닫는다.
    ///
    /// The games put everything the player owns behind one button, and until now this one had
    /// no such button at all — the party, the bag, the dex and the save all existed as data
    /// with nothing to open them.
    ///
    /// Each entry opens a real panel filled from the profile. Where a system genuinely has
    /// nothing in it yet, the panel says which system and that it is empty, rather than
    /// showing a plausible-looking blank: an empty bag and a bag that failed to load must not
    /// look the same.
    ///
    /// In PokeLab.Boot because it reads Overworld state and builds PokeLab.UI widgets, and
    /// those two assemblies cannot see each other.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class StartMenuPresenter : MonoBehaviour
    {
        // Above the dialogue box at 400: the menu is the frontmost thing when it is open.
        private const int SortingOrder = 460;

        private StartMenuView _menu;
        private RectTransform _detail;
        private TextMeshProUGUI _detailTitle;
        private TextMeshProUGUI _detailBody;
        private Canvas _canvas;

        private GameFlowController _flow;
        private OverworldInputReader _input;
        private PlayerProfileHost _profileHost;

        private bool _open;
        private bool _detailOpen;
        private bool _dexOpen;
        private DexView _dex;
        private GameObject _dexRoot;
        private bool _pushedMode;

        private readonly List<StartMenuView.Entry> _entries = new List<StartMenuView.Entry>();

        private void Update()
        {
            Rebind();

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (_open) { UpdateOpen(keyboard); return; }

            // Only from ordinary play. Escape during a battle, a cutscene or a line of dialogue
            // belongs to whatever is running, and a menu that opened over the opening scene
            // would let the player save before the game had decided who they are.
            if (!keyboard.escapeKey.wasPressedThisFrame) return;
            if (_flow != null && _flow.Mode != GameMode.Exploring) return;

            Open();
        }

        private void UpdateOpen(Keyboard keyboard)
        {
            if (_detailOpen)
            {
                // The dex is a list the player moves through, so it takes the arrows before it
                // takes a dismissal — otherwise the only thing it does is close.
                if (_dexOpen && _dex != null)
                {
                    if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame) _dex.Move(1);
                    if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame) _dex.Move(-1);
                    if (keyboard.pageDownKey.wasPressedThisFrame) _dex.Move(8);
                    if (keyboard.pageUpKey.wasPressedThisFrame) _dex.Move(-8);
                    if (keyboard.escapeKey.wasPressedThisFrame || keyboard.xKey.wasPressedThisFrame) CloseDetail();
                    return;
                }

                if (keyboard.escapeKey.wasPressedThisFrame
                    || keyboard.enterKey.wasPressedThisFrame
                    || keyboard.zKey.wasPressedThisFrame)
                    CloseDetail();
                return;
            }

            if (keyboard.escapeKey.wasPressedThisFrame || keyboard.xKey.wasPressedThisFrame) { Close(); return; }

            if (keyboard.downArrowKey.wasPressedThisFrame || keyboard.sKey.wasPressedThisFrame) _menu.Move(1);
            if (keyboard.upArrowKey.wasPressedThisFrame || keyboard.wKey.wasPressedThisFrame) _menu.Move(-1);
            if (keyboard.enterKey.wasPressedThisFrame || keyboard.zKey.wasPressedThisFrame) _menu.Take();
        }

        private void Open()
        {
            EnsureUi();
            if (_menu == null) return;

            BuildEntries();
            _menu.Open(_entries);
            _open = true;

            // The world stops and the cursor comes back, because every row here is a thing to
            // click and a locked invisible cursor made an earlier choice screen unusable.
            if (_flow != null) { _flow.PushMode(GameMode.Menu); _pushedMode = true; }
            if (_input != null) _input.InputEnabled = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Close()
        {
            CloseDetail();
            if (_menu != null) _menu.Close();
            _open = false;

            if (_pushedMode && _flow != null) { _flow.PopMode(); _pushedMode = false; }
            if (_input != null) _input.InputEnabled = true;
        }

        private void Chose(int index)
        {
            switch (index)
            {
                case 0: ShowDex(); break;
                case 1: ShowDetail(Loc.Pick("Pokémon", "포켓몬"), Party()); break;
                case 2: ShowDetail(Loc.Pick("Bag", "가방"), Bag()); break;
                case 3: ShowDetail(TrainerName(), Card()); break;
                case 4: ShowDetail(Loc.Pick("Report", "리포트"), Report()); break;
                case 5: ShowDetail(Loc.Pick("Options", "설정"), Options()); break;
                default: Close(); break;
            }
        }

        // --- panels ----------------------------------------------------------------------

        private IPlayerProfile Profile =>
            _profileHost != null ? _profileHost.Profile as IPlayerProfile : null;

        private string TrainerName()
        {
            var name = Profile?.TrainerName;
            return string.IsNullOrEmpty(name) ? Loc.Pick("Trainer", "트레이너") : name;
        }

        /// <summary>
        /// Opens the dex on its own screen.
        ///
        /// Every species the registry knows is listed, not just the ones met: the numbers a
        /// player has not filled in are what a dex is for, and a list that grows as you catch
        /// things has no shape to complete.
        /// </summary>
        private void ShowDex()
        {
            var profile = Profile;
            if (profile == null || _dex == null)
            {
                ShowDetail(Loc.Pick("Pokédex", "도감"), Missing("도감"));
                return;
            }

            var seen = new HashSet<int>();
            if (profile.SeenSpecies != null) foreach (var id in profile.SeenSpecies) seen.Add(id);
            var caught = new HashSet<int>();
            if (profile.CaughtSpecies != null) foreach (var id in profile.CaughtSpecies) caught.Add(id);

            var entries = new List<DexView.Entry>();
            if (ServiceHub.TryGet<ISpeciesRegistry>(out var species) && species != null)
            {
                foreach (var data in species.All)
                {
                    if (data == null) continue;
                    var wasSeen = seen.Contains(data.Id);
                    entries.Add(new DexView.Entry(data.Id, data.DisplayName, wasSeen,
                        caught.Contains(data.Id), wasSeen ? ArtFor(data) : null));
                }
            }

            _dex.Show(Loc.Pick("Pokédex", "도감"), entries, seen.Count, caught.Count);
            _dexRoot.SetActive(true);
            _detailOpen = true;
            _dexOpen = true;
        }

        /// <summary>
        /// The portrait for a species, or null.
        ///
        /// Loaded from Resources by art key rather than through the creature art registry,
        /// because that registry answers with world prefabs and display heights — things a
        /// list of names has no use for.
        /// </summary>
        private static Sprite ArtFor(SpeciesData data)
        {
            var key = !string.IsNullOrEmpty(data.ArtKey) ? data.ArtKey : data.NameEn;
            if (string.IsNullOrEmpty(key)) return null;
            return Resources.Load<Sprite>("Creatures/" + key.ToLowerInvariant() + "_portrait");
        }

        private string Party()
        {
            var party = Profile?.Party;
            if (party == null) return Missing("포켓몬");
            if (party.Count == 0)
                return Loc.Pick("You have no Pokémon with you.", "데리고 다니는 포켓몬이 없다.");

            var text = new StringBuilder();
            foreach (var creature in party)
            {
                if (creature == null) continue;
                text.Append(creature.Nickname ?? ("No." + creature.SpeciesId.ToString("000")))
                    .Append("   Lv.").Append(creature.Level)
                    .Append("   HP ").Append(creature.CurrentHp).Append('/').Append(creature.MaxHp)
                    .Append('\n');
            }
            return text.ToString();
        }

        private string Bag()
        {
            var inventory = Profile?.Inventory;
            if (inventory == null) return Missing("가방");
            if (inventory.Count == 0)
                return Loc.Pick("The bag is empty.", "가방이 비어 있다.");

            var text = new StringBuilder();
            foreach (var pair in inventory)
                text.Append(pair.Key).Append("   x").Append(pair.Value).Append('\n');
            return text.ToString();
        }

        private string Card()
        {
            var profile = Profile;
            if (profile == null) return Missing("트레이너 카드");

            var text = new StringBuilder();
            text.Append(Loc.Pick("Name", "이름")).Append("   ").Append(TrainerName()).Append('\n');
            text.Append(Loc.Pick("Money", "소지금")).Append("   ").Append(profile.Money).Append('\n');
            text.Append(Loc.Pick("Pokémon", "포켓몬")).Append("   ").Append(profile.Party?.Count ?? 0).Append('\n');
            return text.ToString();
        }

        private string Report()
        {
            if (_profileHost == null) return Missing("리포트");

            // Written on the spot rather than offered as a confirmation, because 리포트 in the
            // games is the act of saving, not a screen that asks whether you meant it.
            var saved = _profileHost.SaveGame();
            return saved
                ? Loc.Pick("Saved.", "기록했다.")
                : Loc.Pick("The report could not be written.", "기록하지 못했다.");
        }

        private string Options()
        {
            return Loc.Pick("Language  English", "언어  한국어")
                 + "\n\n" + Loc.Pick("Move  W/S or arrows\nConfirm  Enter or Z\nBack  Escape or X",
                                     "이동  W/S 또는 방향키\n결정  Enter 또는 Z\n취소  Escape 또는 X");
        }

        private static string Missing(string what) =>
            Loc.Pick($"{what} is not available in this session.",
                     $"{what}을(를) 열 수 없다. 저장 데이터를 찾지 못했다.");

        // --- detail panel ----------------------------------------------------------------

        private void ShowDetail(string title, string body)
        {
            if (_detail == null) return;
            _detailTitle.text = title;
            _detailBody.text = body;
            _detail.gameObject.SetActive(true);
            _detailOpen = true;
        }

        private void CloseDetail()
        {
            if (_detail != null) _detail.gameObject.SetActive(false);
            if (_dexRoot != null) _dexRoot.SetActive(false);
            _detailOpen = false;
            _dexOpen = false;
        }

        // --- construction ----------------------------------------------------------------

        private void BuildEntries()
        {
            _entries.Clear();
            _entries.Add(new StartMenuView.Entry(Loc.Pick("Pokédex", "도감"), UiPalette.ScannerCyan));
            _entries.Add(new StartMenuView.Entry(Loc.Pick("Pokémon", "포켓몬"), new Color(0.95f, 0.42f, 0.36f)));
            _entries.Add(new StartMenuView.Entry(Loc.Pick("Bag", "가방"), new Color(0.86f, 0.66f, 0.32f)));
            _entries.Add(new StartMenuView.Entry(TrainerName(), new Color(0.44f, 0.62f, 0.92f)));
            _entries.Add(new StartMenuView.Entry(Loc.Pick("Report", "리포트"), new Color(0.62f, 0.55f, 0.85f)));
            _entries.Add(new StartMenuView.Entry(Loc.Pick("Options", "설정"), new Color(0.55f, 0.60f, 0.66f)));
            _entries.Add(new StartMenuView.Entry(Loc.Pick("Close", "닫는다"), new Color(0.42f, 0.46f, 0.52f)));
        }

        private void EnsureUi()
        {
            if (_menu != null) return;

            var canvasGo = new GameObject("StartMenuCanvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            _canvas = canvasGo.AddComponent<Canvas>();
            UiBuilder.ConfigureCanvas(_canvas, SortingOrder);
            UiBuilder.EnsureEventSystem();

            var menuGo = new GameObject("StartMenu", typeof(RectTransform));
            menuGo.transform.SetParent(canvasGo.transform, false);
            _menu = menuGo.AddComponent<StartMenuView>();
            _menu.Chosen = Chose;
            _menu.Cancelled = Close;
            _menu.Close();

            BuildDetail(canvasGo.transform);

            var dexGo = new GameObject("Dex", typeof(RectTransform));
            dexGo.transform.SetParent(canvasGo.transform, false);
            _dex = dexGo.AddComponent<DexView>();
            _dexRoot = dexGo;
            _dexRoot.SetActive(false);
        }

        /// <summary>
        /// One panel every entry reuses.
        ///
        /// A screen each would be six layouts to keep consistent for six lists of text, and the
        /// content genuinely is the same shape in every case: a heading and lines under it.
        /// </summary>
        private void BuildDetail(Transform parent)
        {
            _detail = UiBuilder.Rect("Detail", parent, false);
            UiBuilder.Anchor(_detail, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
                new Vector2(60f, 0f), new Vector2(760f, 620f));

            var backing = UiBuilder.Panel("Backing", _detail, UiPalette.Surface, 22);
            UiBuilder.Stretch(backing.rectTransform);

            _detailTitle = UiBuilder.Text("Title", _detail, string.Empty, UiTextRole.Title,
                UiPalette.TextPrimary, TextAlignmentOptions.Left);
            UiBuilder.Anchor(_detailTitle.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f),
                new Vector2(0f, 1f), new Vector2(28f, -24f), new Vector2(-56f, 56f));

            _detailBody = UiBuilder.Text("Body", _detail, string.Empty, UiTextRole.Body,
                UiPalette.TextSecondary, TextAlignmentOptions.TopLeft);
            UiBuilder.Stretch(_detailBody.rectTransform);
            _detailBody.rectTransform.offsetMin = new Vector2(28f, 28f);
            _detailBody.rectTransform.offsetMax = new Vector2(-28f, -92f);

            _detail.gameObject.SetActive(false);
        }

        private void Rebind()
        {
            if (_flow == null) _flow = FindFirstObjectByType<GameFlowController>();
            if (_input == null) _input = FindFirstObjectByType<OverworldInputReader>();
            if (_profileHost == null) _profileHost = FindFirstObjectByType<PlayerProfileHost>();
        }
    }
}
