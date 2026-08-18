using PokeLab.Cinematics;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Stands the conversation camera up at runtime, so no scene needs to contain it.
    ///
    /// Same reasoning as <see cref="AvPresenterHost"/>: a fully written system that no scene
    /// carries is a system that does not exist, and fixing that in the scenes means fixing it
    /// in every scene forever. One persistent object composed from code is the whole
    /// integration — enter Play Mode and conversations have a camera.
    ///
    /// AfterSceneLoad on purpose, and the director's own first-one-wins guard does the rest:
    /// a scene that someday authors its own <see cref="DialogueCameraDirector"/> with tuned
    /// inspector values is found here before a stand-in is created, and wins.
    /// </summary>
    public static class DialogueCameraBoot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (Object.FindAnyObjectByType<DialogueCameraDirector>(FindObjectsInactive.Include) != null) return;

            var go = new GameObject("PL_DialogueCameraHost");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<DialogueCameraDirector>();
        }
    }
}
