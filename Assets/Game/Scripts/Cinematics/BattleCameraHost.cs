using Unity.Cinemachine;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Stands up a camera the arena can actually be drawn through, for battles that arrive over
    /// a scene whose camera cannot do it.
    ///
    /// <b>Why this is needed at all.</b> Battle.unity contains no Camera. That is deliberate —
    /// it is laid over a scene that has one, and the overworld's camera carries a
    /// <see cref="CinemachineBrain"/> that <see cref="BattleCameraRig"/>'s eight virtual cameras
    /// drive. Loading the arena over the TITLE screen breaks both halves of that arrangement:
    ///
    ///   * MainMenu's camera has no CinemachineBrain, so the rig's virtual cameras have nothing
    ///     to steer and the shot list never reaches the screen; and
    ///   * its culling mask is 0 — it renders no layers at all. The menu never noticed because
    ///     its canvas is ScreenSpaceOverlay and draws without a camera, but an arena put in
    ///     front of it is invisible by construction.
    ///
    /// Adding a brain to the menu's camera would fix only the first, which is why this makes a
    /// camera of its own instead.
    ///
    /// <b>Order matters.</b> BattleCameraRig caches its brain in Awake, and Awake runs while the
    /// arena scene is loading — so this has to run BEFORE the load is started, not after it
    /// finishes. Getting that backwards leaves the rig holding a null brain and looks exactly
    /// like the bug it was meant to fix.
    /// </summary>
    public static class BattleCameraHost
    {
        private const string HostName = "PL_BattleCamera";

        private static GameObject s_host;
        private static Camera s_suppressed;

        /// <summary>
        /// Makes sure a Cinemachine-driven camera exists and is the one <c>Camera.main</c>
        /// resolves to. Cheap and idempotent when the current camera is already suitable — which
        /// is the case for every battle that arrives over the overworld, so the story path is
        /// left exactly as it was.
        /// </summary>
        public static void Ensure()
        {
            if (s_host != null) return;

            var current = Camera.main;
            if (current != null &&
                current.GetComponent<CinemachineBrain>() != null &&
                current.cullingMask != 0)
            {
                // The overworld's camera. Nothing to do, and nothing to undo later.
                return;
            }

            // The incumbent has to stop being Camera.main, which means losing the tag as well as
            // being switched off: Camera.main resolves by tag, and two MainCamera-tagged objects
            // is a coin toss.
            if (current != null)
            {
                s_suppressed = current;
                current.enabled = false;
                current.gameObject.tag = "Untagged";
            }

            s_host = new GameObject(HostName) { tag = "MainCamera" };
            Object.DontDestroyOnLoad(s_host);

            var camera = s_host.AddComponent<Camera>();
            // Skybox, not a solid fill. A black clear colour is what made the arena's sky read
            // as a void: the scene underneath carries Unity's default skybox material, and
            // clearing to black threw it away.
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.backgroundColor = Color.black;
            camera.cullingMask = ~0;          // everything; the arena decides what is on screen
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 1000f;
            camera.fieldOfView = 60f;

            s_host.AddComponent<CinemachineBrain>();
            EnsureSun();

            Debug.Log("[BattleCamera] stood up a Cinemachine-driven camera; the scene underneath " +
                      "had " + (current == null ? "none" : "one that could not draw the arena") + ".");
        }

        /// <summary>
        /// Gives the arena a sun when the scene it landed in has none.
        ///
        /// <c>LightingDirector</c> owns the sun in this game — but it only ever POSITIONS one:
        /// ApplySun calls FindSunLight and returns if the search comes back empty. That is fine
        /// in the overworld, where Town and Field each author a directional light. It is not
        /// fine for a battle launched from the title screen: MainMenu has no Light and
        /// Battle.unity has none either, so the arena arrived lit by nothing but ambient and
        /// read as almost black.
        ///
        /// Parented to the host so Release takes it away with everything else, and only created
        /// when the scene really has no directional light — a battle over the overworld keeps
        /// the sun the world already has, and the time of day it is set to.
        /// </summary>
        private static void EnsureSun()
        {
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (light != null && light.enabled && light.type == LightType.Directional) return;

            var go = new GameObject("PL_BattleSun");
            go.transform.SetParent(s_host.transform, false);
            go.transform.rotation = Quaternion.Euler(50f, 35f, 0f);

            var sun = go.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.96f, 0.88f);
            sun.intensity = 1.1f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.6f;

            // Ambient too: a directional light alone leaves everything facing away from it
            // black, and the menu's ambient is a dark navy chosen to sit behind a UI.
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.55f, 0.60f, 0.68f);
            RenderSettings.ambientEquatorColor = new Color(0.40f, 0.42f, 0.46f);
            RenderSettings.ambientGroundColor = new Color(0.22f, 0.22f, 0.24f);

            Debug.Log("[BattleCamera] the scene had no directional light; stood one up for the arena.");
        }

        /// <summary>Puts the previous camera back and removes ours. Safe when Ensure did nothing.</summary>
        public static void Release()
        {
            if (s_host != null)
            {
                Object.Destroy(s_host);
                s_host = null;
            }

            if (s_suppressed != null)
            {
                s_suppressed.gameObject.tag = "MainCamera";
                s_suppressed.enabled = true;
                s_suppressed = null;
            }
        }
    }
}
