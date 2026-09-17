using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>A signed junction: preserves the lake path while adding the northbound route.</summary>
    public sealed class ChapterRouteEntrance : MonoBehaviour, IInteractable
    {
        public string InteractionPrompt => StoryProgress.Has(StoryProgress.HomeReady)
            ? Loc.Pick("Read the Route 202 sign", "202번도로 안내를 읽는다")
            : Loc.Pick("Read the Route 202 sign", "202번도로 표지판을 읽는다");
        public bool CanInteract(GameObject player) => isActiveAndEnabled &&
            (EpisodeRunner.Live == null || !EpisodeRunner.Live.IsPlaying) &&
            (DialogueRunner.Instance == null || !DialogueRunner.Instance.IsPlaying);
        public void Interact(GameObject player)
        {
            if (!CanInteract(player)) return;
            if (!StoryProgress.Has(StoryProgress.HomeReady))
            {
                EpisodeRunner.Live?.Play("route202_home_reminder");
                return;
            }
            DialogueRunner.Instance?.Play(DialogueBook.Shared.Build("route202_sign"), gameObject);
        }
    }
}
