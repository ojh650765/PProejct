using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Compatibility marker for existing scenes. Ordinary dialogue preserves the exploration
    /// camera. Authored story shots belong exclusively to EpisodeShotRig.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DialogueCameraDirector : MonoBehaviour
    {
        public bool IsEngaged => false;
    }
}
