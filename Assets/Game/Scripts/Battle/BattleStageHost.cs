using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Battle
{
    /// <summary>
    /// Scene-side owner of the <see cref="BattleStage"/>: creates it, registers it as
    /// <see cref="IBattleStage"/>, and surfaces failures that the pure-C# stage cannot log
    /// itself. Drop one into the boot scene and the overworld's flow controller picks up
    /// the real battle path automatically.
    ///
    /// It holds no simulation logic — every rule lives in <see cref="BattleEngine"/>, which
    /// stays free of UnityEngine so it can be tested headlessly. This is only the wiring.
    ///
    /// Registered early, because the flow controller resolves the stage lazily and a
    /// registration that lands after the first encounter would silently take the degraded
    /// path for that battle.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-400)]
    public sealed class BattleStageHost : MonoBehaviour
    {
        [Header("Opponent")]
        [Tooltip("How hard trainers play. Wild encounters always use the erratic wild policy.")]
        [SerializeField] private AiDifficulty _trainerDifficulty = AiDifficulty.Standard;

        [Tooltip("Levels added to every authored trainer party. Use for a late-game rematch pass.")]
        [SerializeField] private int _trainerLevelOffset;

        [Header("Diagnostics")]
        [Tooltip("Log each battle's outcome and every event the engine emits. Editor only.")]
        [SerializeField] private bool _logBattles;

        private BattleStage _stage;

        /// <summary>The live stage. Null only before Awake.</summary>
        public BattleStage Stage => _stage;

        private void Awake()
        {
            // Defer to a stage the boot scene already registered; two stages would both
            // answer the flow controller and the second battle would fight the first.
            if (ServiceHub.TryGet<IBattleStage>(out var existing) && existing is BattleStage registered)
            {
                _stage = registered;
                return;
            }

            _stage = new BattleStage
            {
                TrainerDifficulty = _trainerDifficulty,
                TrainerLevelOffset = _trainerLevelOffset,
            };

            _stage.Register();

            if (_logBattles) _stage.EventsProduced += LogEvents;
        }

        private void OnDestroy()
        {
            if (_stage == null) return;

            _stage.EventsProduced -= LogEvents;

            // A scene teardown mid-battle would otherwise leave the flow controller waiting
            // on a callback that can never come, freezing the player until its watchdog.
            if (_stage.IsBattleActive) _stage.Abort("The battle scene was torn down.");

            if (!string.IsNullOrEmpty(_stage.LastFailureReason))
                Debug.LogWarning($"[BattleStage] Last encounter aborted: {_stage.LastFailureReason}", this);
        }

        private void LogEvents(System.Collections.Generic.IReadOnlyList<BattleEvent> stream)
        {
            for (var i = 0; i < stream.Count; i++)
                Debug.Log($"[Battle] T{stream[i].Turn} {stream[i].GetType().Name}", this);
        }
    }
}
