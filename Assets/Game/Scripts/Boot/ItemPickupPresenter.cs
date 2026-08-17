using System.Collections.Generic;
using PokeLab.Core;
using PokeLab.Overworld;
using PokeLab.UI;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Says what the player just picked up.
    ///
    /// <see cref="ItemPickup"/> was complete — trigger, profile flag, the bag the battle engine
    /// spends from — and every ball in every scene was taken in total silence: the object
    /// disappeared, nothing appeared, and the only way to know it had worked was to open the bag
    /// and count. Reported, reasonably, as the feature not existing.
    ///
    /// The series has said this line since the first game, and it is the whole of the feedback:
    /// the box comes up, it names what you found, you press on. Drawn through the same
    /// <see cref="DialogueView"/> as every other line so an item on the ground and a person in
    /// the road speak in one voice rather than two.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ItemPickupPresenter : MonoBehaviour
    {
        /// <summary>
        /// Korean names for the items that can be found in the field.
        ///
        /// Here rather than in ItemCatalog because that table is the battle engine's registry
        /// and is keyed by mechanics — adding a language column to it would make every worker
        /// touching an item's numbers also touch its wording. Anything missing falls back to the
        /// catalogue's English name, which is wrong but readable, and never blank.
        /// </summary>
        private static readonly Dictionary<string, string> KoreanNames = new()
        {
            { "poke-ball", "몬스터볼" },
            { "great-ball", "수퍼볼" },
            { "ultra-ball", "하이퍼볼" },
            { "potion", "상처약" },
            { "super-potion", "좋은상처약" },
            { "hyper-potion", "고급상처약" },
            { "max-potion", "풀회복약" },
            { "antidote", "해독제" },
            { "burn-heal", "화상치료제" },
            { "ice-heal", "얼음상태치료제" },
            { "awakening", "잠깨는약" },
            { "paralyze-heal", "마비치료제" },
            { "full-heal", "만병통치제" },
            { "revive", "기력의조각" },
            { "repel", "벌레회피스프레이" },
        };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var host = new GameObject("~ItemPickupMessage");
            DontDestroyOnLoad(host);
            host.AddComponent<ItemPickupPresenter>();
        }

        private void OnEnable() => OverworldEvents.ItemCollected += OnCollected;

        private void OnDisable() => OverworldEvents.ItemCollected -= OnCollected;

        private void OnCollected(string itemId, int count)
        {
            var view = FindFirstObjectByType<DialogueView>();
            if (view == null) return;

            // Narration, so no speaker: nobody is talking, the game is telling you what
            // happened. The box hides its name plate when the name is empty.
            //
            // The advance callback is what dismisses it, and it is not optional. The box closes
            // through this or through an explicit Close, and nothing else was going to call
            // either: DialoguePresenter only closes what the runner opened, and the view will
            // not even arm AUTO without a handler. Passed nothing, the line sat there for good,
            // over the battle HUD it outranks, with every press doing nothing.
            view.Show(string.Empty, Line(itemId, count), null, () => { if (view != null) view.Close(); });
        }

        private static string Line(string itemId, int count)
        {
            var name = DisplayName(itemId);
            var player = PlayerName();

            // Josa, because the name is typed by the player and the particle after it cannot be
            // authored into the string: "케이트는" and "렌은" take different ones, and the item
            // does too -- 몬스터볼을 against 상처약을.
            var subject = Josa.WithTopic(player);
            var found = Josa.WithObject(count > 1 ? $"{name} {count}개" : name);

            return Loc.Pick($"{player} found {(count > 1 ? name + " x" + count : name)}!",
                            $"{subject} {found} 주웠다!");
        }

        private static string DisplayName(string itemId)
        {
            if (Loc.Language == Language.Korean && KoreanNames.TryGetValue(itemId, out var korean))
                return korean;
            return PokeLab.Battle.ItemCatalog.TryGetBag(itemId, out var data) && data != null
                ? data.DisplayName
                : itemId;
        }

        private static string PlayerName() =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) && !string.IsNullOrEmpty(profile.TrainerName)
                ? profile.TrainerName
                : Loc.Pick("You", "당신");
    }
}
