using UnityEditor;
using UnityEditor.SceneManagement;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// Opens the two streamed bands together in edit mode, the way WorldStreamer holds them
    /// in play.
    ///
    /// The walkthrough capture opens exactly one scene, and that is right for reviewing one
    /// band — but the opening act's gate shot looks from Town across the seam into Field,
    /// and reviewing it against a single band renders the far half of the frame as empty
    /// sky. Three tuning passes were spent nudging a shot that was never wrong. This menu
    /// makes the editor hold what the player's streamer holds, so a capture taken with no
    /// scene in its request photographs the world the shot actually plays against.
    /// </summary>
    public static class IntegrationScenes
    {
        [MenuItem("Tools/Poké Lab/Rebuild/Open Town+Field (integration)", priority = 24)]
        public static void OpenTownAndField()
        {
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity", OpenSceneMode.Single);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Field.unity", OpenSceneMode.Additive);
        }
    }
}
