using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>The lesson's grass becomes ordinary encounter grass after the demonstration.</summary>
    public sealed class CaptureLessonGrass : MonoBehaviour
    {
        private TallGrassPatch _patch;
        private Collider _trigger;
        private void Awake() { _patch=GetComponent<TallGrassPatch>(); _trigger=GetComponent<Collider>(); Refresh(); }
        private void Update() => Refresh();
        private void Refresh() { bool enabledNow=StoryProgress.Has(StoryProgress.CaptureLearned); if(_patch!=null)_patch.enabled=enabledNow; if(_trigger!=null)_trigger.enabled=enabledNow; }
    }
}
