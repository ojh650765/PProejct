using PokeLab.Core;
using PokeLab.Overworld;
using PokeLab.Overworld.People;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Connects the opening's "남자인가? 여자인가?" to the character the player then walks around as.
    ///
    /// The choice already raised <c>profile.body.female</c>; <see cref="PlayerBody"/> already had
    /// somewhere to put it; the manifest already had the art. This is the wire between them, and
    /// it lives in Boot because that is the only assembly that can see the dialogue signal in
    /// Overworld and the profile in Progression at once.
    ///
    /// Two directions, both needed. Forwards: the answer is recorded and written to the save, so
    /// it survives a reload. Backwards: on load, the saved flag is pushed back into
    /// <see cref="PlayerBody"/> before anything draws, so a returning player is still themselves.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerBodyPresenter : MonoBehaviour
    {
        /// <summary>
        /// Installs itself, rather than waiting to be added to a scene.
        ///
        /// The other presenters are added by the editor's rig setup, which means they exist in
        /// whichever scenes have been regenerated since. This one has to be in all of them —
        /// the question is asked in the opening, the answer is used in town, in the field and
        /// on the battle stage — and a wire that only exists where somebody remembered to run a
        /// menu item is how this project keeps ending up with finished systems connected to
        /// nothing.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            var host = new GameObject("~PlayerBody");
            DontDestroyOnLoad(host);
            host.AddComponent<PlayerBodyPresenter>();
        }

        private const string MaleSignal = "profile.body.male";
        private const string FemaleSignal = "profile.body.female";

        private bool _restored;

        private void OnEnable()
        {
            OverworldEvents.DialogueSignal += OnDialogueSignal;
            PlayerBody.Changed += ApplyToWorld;
        }

        private void OnDisable()
        {
            OverworldEvents.DialogueSignal -= OnDialogueSignal;
            PlayerBody.Changed -= ApplyToWorld;
        }

        /// <summary>
        /// Restores from the save once the profile exists.
        ///
        /// Polled in Update rather than read in Start: the profile is registered with the hub by
        /// a coroutine that reads the data files, and on the web build that is several frames
        /// after every Start has run. Reading it too early is how a returning female player got
        /// silently turned back into the boy on load.
        /// </summary>
        private void Update()
        {
            if (_restored) return;
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile)) return;
            if (profile is not PlayerProfile concrete) { _restored = true; return; }

            _restored = true;
            PlayerBody.Restore(concrete.GetFlag(PlayerBody.Flag));
            ApplyToWorld();
        }

        private void OnDialogueSignal(string signalId)
        {
            if (signalId != MaleSignal && signalId != FemaleSignal) return;

            PlayerBody.Set(signalId == FemaleSignal);

            if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
                concrete.SetFlag(PlayerBody.Flag, PlayerBody.FlagValue);

            // Applied here as well as from the Changed event, because Set is idempotent: a
            // player who answers with the value already held raises nothing, and the world would
            // keep whatever it was showing.
            ApplyToWorld();
        }

        /// <summary>
        /// Re-keys every billboard currently drawing a player character.
        ///
        /// Found rather than referenced. The player's own billboard is built by the rig setup in
        /// three different scenes, and the transition director builds a second one on the battle
        /// stage the moment a fight starts — a serialized reference would cover the first and
        /// miss the one that appears later, which is the half-connected case this project keeps
        /// producing.
        /// </summary>
        private static void ApplyToWorld()
        {
            var billboards = Object.FindObjectsByType<PersonBillboard>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var billboard in billboards)
            {
                if (billboard == null) continue;
                var key = billboard.PersonKey;
                if (!PlayerBody.IsPlayerKey(key)) continue;

                var wanted = key.EndsWith("_back") ? PlayerBody.BackKey : PlayerBody.SpriteKey;
                if (key != wanted) billboard.PersonKey = wanted;
            }
        }
    }
}
