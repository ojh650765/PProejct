using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld.World
{
    /// <summary>
    /// Hides named overworld props for the length of a battle and puts them back after.
    ///
    /// The battle plays on the same ground the encounter started on, and the professor's
    /// bag sat in frame through the whole fight — a story prop photobombing a battle it
    /// has no part in. Renderers are disabled rather than the object: the StoryEncounter
    /// host and its trigger radius live on the prop and must keep running.
    /// </summary>
    public sealed class BattlePropCurtain : MonoBehaviour
    {
        private static readonly string[] HiddenDuringBattle = { "Prop_ProfessorBag" };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var host = new GameObject("~BattlePropCurtain") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(host);
            host.AddComponent<BattlePropCurtain>();
        }

        private readonly List<Renderer> _hidden = new List<Renderer>();
        private IGameFlow _flow;
        private bool _covered;

        private void Update()
        {
            if (_flow == null && !ServiceHub.TryGet(out _flow)) return;

            var inBattle = _flow.Mode == GameMode.Battle || _flow.Mode == GameMode.BattleOutro;
            if (inBattle == _covered) return;
            _covered = inBattle;

            if (inBattle) Hide();
            else Restore();
        }

        private void Hide()
        {
            _hidden.Clear();
            foreach (var name in HiddenDuringBattle)
            {
                var prop = GameObject.Find(name);
                if (prop == null) continue;
                foreach (var r in prop.GetComponentsInChildren<Renderer>())
                {
                    if (r == null || !r.enabled) continue;
                    r.enabled = false;
                    _hidden.Add(r);
                }
            }
        }

        private void Restore()
        {
            foreach (var r in _hidden)
                if (r != null) r.enabled = true;
            _hidden.Clear();
        }
    }
}
