using System.Collections;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Overworld
{
    /// <summary>
    /// The town's healing machine. Restores the party and optionally lets the player rest until
    /// morning.
    ///
    /// It used to write a save when the heal finished, as the genre's checkpoint convention.
    /// That is gone by request: the game now saves only when the player writes a report from
    /// the menu, and a machine that quietly stamped the file on every heal would undercut
    /// exactly the thing that rule buys — the player always knowing what their last record
    /// holds. The machine heals; it does not checkpoint.
    ///
    /// The heal is not instant: a short sequence with hook points gives the audio and VFX workers
    /// somewhere to put the jingle and the glow. Gameplay-wise the party is already restored when
    /// the sequence starts, so a player who quits mid-animation still gets their heal.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HealingMachine : MonoBehaviour, IInteractable
    {
        [Header("Prompt")]
        [Tooltip("String table keys, not the text. The machine is the first thing a beaten " +
                 "player is pointed at, so the label on it has to be readable to them.")]
        [SerializeField] private string _promptKey = "ui.heal_party";
        [SerializeField] private string _promptAlreadyHealthyKey = "ui.rest";

        [Header("Behaviour")]
        [Tooltip("Seconds the heal sequence plays. Purely presentational; the heal itself is instant.")]
        [SerializeField] private float _sequenceSeconds = 2.4f;
        [Tooltip("Also advances the clock to morning. Turn off if the town should not be a bed.")]
        [SerializeField] private bool _restsUntilMorning;

        [Header("Wiring")]
        [SerializeField] private DayNightCycle _clock;
        [SerializeField] private PlayerLocomotion _player;

        [Header("Dialogue")]
        [Tooltip("Played before the heal. Leave null for a silent machine.")]
        [SerializeField] private DialogueSequence _greeting;
        [SerializeField] private DialogueRunner _dialogueRunner;

        [Header("Presentation hooks")]
        [Tooltip("Wire the VFX glow and the audio jingle here.")]
        [SerializeField] private FloatEvent _healSequenceStarted = new FloatEvent();
        [SerializeField] private StringEvent _healCompleted = new StringEvent();

        private bool _busy;

        public string InteractionPrompt
        {
            get
            {
                if (!ServiceHub.TryGet<IPlayerProfile>(out var profile)) return Loc.Get(_promptKey);
                return Loc.Get(NeedsHealing(profile) ? _promptKey : _promptAlreadyHealthyKey);
            }
        }

        public bool CanInteract(GameObject instigator) => !_busy;

        public void Interact(GameObject instigator)
        {
            if (_busy) return;
            StartCoroutine(RunHeal());
        }

        private static bool NeedsHealing(IPlayerProfile profile)
        {
            var party = profile.Party;
            for (var i = 0; i < party.Count; i++)
            {
                var creature = party[i];
                if (creature == null) continue;
                if (creature.CurrentHp < creature.MaxHp) return true;
                if (creature.Status != StatusCondition.None) return true;
                for (var m = 0; m < creature.Moves.Count; m++)
                    if (creature.Moves[m].CurrentPp < creature.Moves[m].MaxPp) return true;
            }
            return false;
        }

        private IEnumerator RunHeal()
        {
            _busy = true;
            if (_player != null) _player.SetMotionFrozen(true);

            if (_greeting != null && _dialogueRunner != null)
            {
                _dialogueRunner.Play(_greeting, gameObject);
                while (_dialogueRunner.IsPlaying) yield return null;
            }

            // Apply the mechanical effect first, then play the show. A player who alt-F4s during
            // the animation keeps their heal, which is the forgiving failure mode.
            if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
            {
                concrete.HealParty();
            }

            _healSequenceStarted.Invoke(_sequenceSeconds);
            if (_sequenceSeconds > 0f) yield return new WaitForSeconds(_sequenceSeconds);

            if (_restsUntilMorning && _clock != null) _clock.SetPhase(TimeOfDay.Dawn);

            _healCompleted.Invoke(name);
            OverworldEvents.RaisePartyChanged();

            if (_player != null) _player.SetMotionFrozen(false);
            _busy = false;
        }
    }
}
