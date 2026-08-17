using UnityEngine;
using PokeLab.Core;
using PokeLab.Overworld.World;

namespace PokeLab.Overworld
{
    /// <summary>
    /// An item lying in the world: walk up to it, interact, it goes in the bag and the
    /// ball is gone.
    ///
    /// The bag it goes into is <see cref="IPlayerProfile.Inventory"/>, through
    /// <see cref="IPlayerProfile.AddItem"/> — the same dictionary the battle engine
    /// spends from. There is no separate field inventory and there must not be one:
    /// the overworld and the engine already disagreed once about how an item id is
    /// spelled, and a ball that could be picked up and never thrown is the shape that
    /// failure takes.
    ///
    /// Taken-ness is a profile flag rather than a local bool, for the reason
    /// <see cref="DialogueTrigger"/> gives about cutscenes: a local bool resets on
    /// reload, and an item ball that refills every time the player loads a save is an
    /// infinite supply of whatever is in it.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ItemPickup : MonoBehaviour, IInteractable
    {
        [Header("Contents")]
        [Tooltip("Item id as the battle engine spells it — hyphenated. See FieldItems.")]
        [SerializeField] private string _itemId = FieldItems.PokeBall;
        [Min(1)][SerializeField] private int _count = 1;

        [Header("Prompt")]
        [SerializeField] private string _prompt = "Pick up";

        [Header("Wiring")]
        [Tooltip("Turned off the moment the item is taken, so the highlight cannot outlive it.")]
        [SerializeField] private PickupGlow _glow;
        [Tooltip("Carries the item id. Wire the UI worker's 'found an item' banner here.")]
        [SerializeField] private StringEvent _collected = new StringEvent();

        /// <summary>The item this grants, so the level builder can set it from layout data.</summary>
        public string ItemId
        {
            get => _itemId;
            set => _itemId = value;
        }

        public int Count
        {
            get => _count;
            set => _count = Mathf.Max(1, value);
        }

        private string TakenFlagKey => "item_taken_" + name;

        public string InteractionPrompt => _prompt;

        public bool CanInteract(GameObject instigator) => !string.IsNullOrEmpty(_itemId);

        private void Start()
        {
            if (_glow == null) _glow = GetComponentInChildren<PickupGlow>(true);

            // Hidden rather than destroyed. The level is rebuilt from data by
            // LevelLayoutBuilder and this object has to come back with the scene; what
            // must not come back is the item, and the flag is what remembers that.
            var profile = ResolveProfile();
            if (profile != null && profile.GetFlagBool(TakenFlagKey)) gameObject.SetActive(false);
        }

        private static PlayerProfile ResolveProfile() =>
            ServiceHub.TryGet<IPlayerProfile>(out var profile) ? profile as PlayerProfile : null;

        public void Interact(GameObject instigator)
        {
            if (!CanInteract(instigator)) return;

            // No profile means no bag to put it in, so the ball stays where it is. Taking
            // it anyway would delete the item and leave the player nothing.
            if (!ServiceHub.TryGet<IPlayerProfile>(out var bag))
            {
                Debug.LogWarning($"[ItemPickup] {name} has no IPlayerProfile to give " +
                                 $"'{_itemId}' to; leaving it on the ground.", this);
                return;
            }

            bag.AddItem(_itemId, _count);
            if (bag is PlayerProfile concrete) concrete.SetFlagBool(TakenFlagKey, true);

            if (_glow != null) _glow.SetActive(false);

            // Both, because they answer different questions. The UnityEvent is the per-instance
            // hook a designer can wire in the inspector; the static one is the announcement, and
            // it fires whether or not anybody remembered to wire the first — which, across every
            // item in every scene, nobody had.
            _collected.Invoke(_itemId);
            OverworldEvents.RaiseItemCollected(_itemId, _count);

            gameObject.SetActive(false);
        }
    }
}
