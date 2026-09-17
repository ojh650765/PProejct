using System;
using System.IO;
using PokeLab.Overworld;
using PokeLab.Overworld.People;
using UnityEngine;
using UnityEngine.AI;

namespace PokeLab.Boot.Editor
{
    /// <summary>Reusable scene/actor/episode bindings for generated interiors.</summary>
    internal static class InteriorStoryBuilder
    {
        [Serializable] private sealed class Book { public Binding[] actors; }
        [Serializable] private sealed class Binding
        {
            public string scene, actor, npcId, displayName, personKey;
            public string episode, requiresFlag, completionFlag;
            public string appearsOnFlag, leavesOnFlag;
            public bool mentor;
            public float[] position;
            public float radius = 3;
        }

        public static void Build(string scene, Transform parent)
        {
            const string path = "Assets/Game/Data/Story/interior_actors.json";
            if (!File.Exists(path)) return;
            var book = JsonUtility.FromJson<Book>(File.ReadAllText(path));
            foreach (var binding in book.actors ?? Array.Empty<Binding>())
            {
                if (binding.scene != scene) continue;
                if (binding.position == null || binding.position.Length != 3)
                    throw new InvalidDataException($"{binding.actor}: expected a position with three numbers.");
                var go = new GameObject(binding.actor);
                go.transform.SetParent(parent, false);
                go.transform.localPosition = new Vector3(binding.position[0], binding.position[1], binding.position[2]);
                go.transform.localRotation = Quaternion.Euler(0,180,0);
                go.layer = LayerMask.NameToLayer("Interactable");
                var capsule = go.AddComponent<CapsuleCollider>();
                capsule.radius = .27f; capsule.height = 1.6f; capsule.center = Vector3.up*.8f;
                var agent = go.AddComponent<NavMeshAgent>();
                agent.enabled = false; agent.radius = .27f; agent.height = 1.6f;
                go.AddComponent<NpcController>().Configure(binding.npcId, binding.displayName,
                    Array.Empty<NpcScheduleEntry>());
                var visual = new GameObject("Visual");
                visual.transform.SetParent(go.transform, false);
                visual.AddComponent<PersonBillboard>().PersonKey = binding.personKey;
                go.AddComponent<StoryEncounter>().Configure(binding.episode, binding.requiresFlag,
                    binding.completionFlag, binding.radius, 0);
                if (!string.IsNullOrEmpty(binding.appearsOnFlag) || !string.IsNullOrEmpty(binding.leavesOnFlag) || binding.mentor)
                    go.AddComponent<StoryActorVisibility>().Configure(binding.appearsOnFlag, binding.leavesOnFlag, binding.mentor);
            }
        }
    }
}
