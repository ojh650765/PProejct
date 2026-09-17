using System.Collections;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>Keeps the door's load and reveal alive after the source scene is unloaded.</summary>
    internal sealed class LevelTransitionRunner : MonoBehaviour
    {
        private static bool _busy;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState() => _busy = false;

        public static bool TryRun(IEnumerator transition)
        {
            if (_busy) return false;
            _busy = true;
            var host = new GameObject("LevelTransitionRoutine").AddComponent<LevelTransitionRunner>();
            DontDestroyOnLoad(host.gameObject);
            host.StartCoroutine(host.Run(transition));
            return true;
        }

        private IEnumerator Run(IEnumerator transition)
        {
            try { yield return transition; }
            finally
            {
                _busy = false;
                Destroy(gameObject);
            }
        }
    }
}
