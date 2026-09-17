using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem.UI;

namespace PokeLab.UI
{
    internal static class SceneEventSystem
    {
        private static EventSystem owner;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            SceneManager.sceneLoaded -= Loaded;
            SceneManager.sceneLoaded += Loaded;
            SceneManager.sceneUnloaded -= Unloaded;
            SceneManager.sceneUnloaded += Unloaded;
        }

        private static void Loaded(Scene scene, LoadSceneMode mode)
        {
            Ensure();
            if (mode == LoadSceneMode.Single && owner != null)
            {
                // Drop a finger held on a button belonging to the outgoing battle.
                var input = owner.GetComponent<InputSystemUIInputModule>();
                input.enabled = false; input.enabled = true;
                owner.SetSelectedGameObject(null);
            }
        }

        private static void Unloaded(Scene scene) => Ensure();

        public static void Ensure()
        {
            if (!Application.isPlaying) return;
            // A dedicated persistent host must not retain an entire scene's GameHosts root.
            if (owner == null)
            {
                var host = new GameObject("PersistentUiInput");
                Object.DontDestroyOnLoad(host);
                owner = host.AddComponent<EventSystem>();
                host.AddComponent<InputSystemUIInputModule>();
            }
            var systems = Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.InstanceID);
            foreach (var system in systems)
            {
                if (system == owner) continue;
                system.enabled = false;
                foreach (var module in system.GetComponents<BaseInputModule>()) module.enabled = false;
            }
            owner.enabled = true;
            var input = owner.GetComponent<InputSystemUIInputModule>();
            // Browser-synthesised mouse motion must not discard a held touch pointer.
            input.pointerBehavior = UIPointerBehavior.AllPointersAsIs;
            input.enabled = true;
            EventSystem.current = owner;
        }
    }
}
