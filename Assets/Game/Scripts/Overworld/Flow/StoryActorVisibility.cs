using PokeLab.Core;
using PokeLab.Overworld.People;
using UnityEngine;

namespace PokeLab.Overworld
{
    /// <summary>Authored actor availability, including NPCs who enter between chained scenes.</summary>
    public sealed class StoryActorVisibility : MonoBehaviour
    {
        [SerializeField] private string _appears, _leaves;
        [SerializeField] private bool _mentor;
        private bool? _visible;
        public void Configure(string appears, string leaves, bool mentor)
        { _appears = appears; _leaves = leaves; _mentor = mentor; }

        private void Update()
        {
            var show = StoryProgress.Has(_appears) && (string.IsNullOrEmpty(_leaves) || !StoryProgress.Has(_leaves));
            if (_visible == show) return;
            _visible = show;
            foreach (Transform child in transform) child.gameObject.SetActive(show);
            foreach (var collider in GetComponents<Collider>()) collider.enabled = show;
            var encounter = GetComponent<StoryEncounter>();
            if (encounter != null) encounter.enabled = show;
            if (_mentor)
            {
                var person = GetComponentInChildren<PersonBillboard>(true);
                if (person != null) person.PersonKey = PlayerBody.IsFemale ? "player" : "player_f";
            }
        }
    }
}
