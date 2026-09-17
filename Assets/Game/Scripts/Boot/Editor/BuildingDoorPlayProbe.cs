using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using PokeLab.Core;
using PokeLab.Overworld;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PokeLab.Boot.Editor
{
    [InitializeOnLoad]
    public static class BuildingDoorPlayProbe
    {
        private const string Key = "PokeLab.BuildingDoorProbe";
        static BuildingDoorPlayProbe() => EditorApplication.playModeStateChanged += Changed;

        [MenuItem("Tools/Poké Lab/Repair/Play Test All Building Doors")]
        public static void Begin()
        {
            if (EditorApplication.isPlaying) return;
            File.WriteAllText("Temp/pokelab_jump.txt", "free");
            GameViewPresentation.HideGizmos();
            SessionState.SetBool(Key, true);
            EditorSceneManager.OpenScene("Assets/Game/Scenes/Town.unity", OpenSceneMode.Single);
            EditorApplication.isPlaying = true;
        }

        private static void Changed(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Key, false)) return;
            SessionState.SetBool(Key, false);
            var host = new GameObject("BuildingDoorPlayProbe").AddComponent<Probe>();
            UnityEngine.Object.DontDestroyOnLoad(host.gameObject);
            host.StartCoroutine(host.Run());
        }

        [Serializable] public sealed class Door
        {
            public string name, scene;
            public float[] position;
            public float facingYaw;
        }
        [Serializable] public sealed class Layout { public Door[] buildingDoors; }
        [Serializable] public sealed class Result
        {
            public string state;
            public List<string> checks = new List<string>();
            public List<string> failures = new List<string>();
        }

        public sealed class Probe : MonoBehaviour
        {
            private readonly Result result = new Result { state = "running" };
            private IPlayerProfile sessionProfile;
            private void Save() => File.WriteAllText("Temp/building_doors_play.json", JsonUtility.ToJson(result, true));

            public IEnumerator Run()
            {
                Application.runInBackground = true;
                Save();
                yield return new WaitForSeconds(3);
                ServiceHub.TryGet(out sessionProfile);
                var layout = JsonUtility.FromJson<Layout>(File.ReadAllText("Assets/Game/Data/Levels/slice_town_unity.json"));
                yield return CheckPickups();
                foreach (var door in layout.buildingDoors)
                {
                    if (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile concrete)
                    {
                        foreach (var flag in new[] { "story.opening_seen", "story.gate_open", "story.kes_summons_done" })
                            concrete.SetFlagBool(flag, true);
                    }
                    var p = new Vector3(door.position[0], door.position[1], door.position[2]);
                    var forward = Quaternion.Euler(0, door.facingYaw, 0)*Vector3.forward;
                    var player = FindFirstObjectByType<PlayerLocomotion>();
                    if (player == null) { result.failures.Add("Missing player"); break; }
                    player.Warp(p+forward*1.6f+Vector3.up*.03f, Quaternion.LookRotation(-forward));
                    yield return new WaitForSeconds(.3f);
                    yield return WalkThrough(-forward, "Town", door.scene);
                    if (SceneManager.GetActiveScene().name != door.scene)
                    {
                        result.failures.Add(door.name+": entrance did not load "+door.scene);
                        result.failures.Add($"position={player.transform.position}, frozen={player.IsMotionFrozen}, episode={EpisodeRunner.Live?.PlayingEpisodeId}, dialogue={DialogueRunner.Instance?.CurrentSequenceId}");
                        ScreenCapture.CaptureScreenshot("previews/door_failure.png");
                        yield return new WaitForEndOfFrame();
                        break;
                    }
                    yield return new WaitForSeconds(1);
                    if (!ServiceHub.TryGet<IPlayerProfile>(out var arrivedProfile) || !ReferenceEquals(arrivedProfile, sessionProfile))
                        result.failures.Add(door.name + ": the scene load replaced the live player profile");
                    var inside = FindFirstObjectByType<PlayerLocomotion>();
                    var arrival = GameObject.Find("Spawn_FromDoor");
                    if (inside == null || arrival == null || Vector3.Distance(inside.transform.position, arrival.transform.position)>1f)
                        result.failures.Add(door.name+": wrong interior arrival");
                    {
                        ScreenCapture.CaptureScreenshot("previews/"+door.scene+"_play.png");
                        yield return new WaitForEndOfFrame();
                    }
                    if (door.scene == "Interior_PokeCentre") yield return CheckHealing();
                    yield return WalkThrough(Vector3.back, door.scene, "Town");
                    if (SceneManager.GetActiveScene().name != "Town")
                    {
                        result.failures.Add(door.name+": return door did not load Town");
                        break;
                    }
                    yield return new WaitForSeconds(.8f);
                    var outside = FindFirstObjectByType<PlayerLocomotion>();
                    if (outside == null || Vector3.Distance(outside.transform.position, p)>3f)
                        result.failures.Add(door.name+": returned to a different building");
                    result.checks.Add(door.name+": entered "+door.scene+" and returned");
                    Save();
                }
                result.state = result.failures.Count == 0 && result.checks.Count >= 16 ? "passed" : "failed";
                Save();
                EditorApplication.isPlaying = false;
            }

            private IEnumerator CheckPickups()
            {
                var player = FindFirstObjectByType<PlayerLocomotion>();
                if (player == null || !ServiceHub.TryGet<IPlayerProfile>(out var profile)) yield break;
                foreach (var trigger in FindObjectsByType<StoryEncounter>(FindObjectsSortMode.None)) trigger.enabled = false;
                if (profile is PlayerProfile concrete)
                {
                    concrete.SetFlagBool("story.opening_seen", true);
                    concrete.SetFlagBool("story.gate_open", true);
                    concrete.SetFlagBool("story.kes_summons_done", true);
                    // Keep ambient trainer challenges out of this door/terrain test.
                    concrete.SetFlagBool("story.lab_invited", false);
                    concrete.SetFlagBool("story.pokedex", false);
                }
                yield return CheckBridge();
                foreach (var pickup in FindObjectsByType<ItemPickup>(FindObjectsSortMode.None))
                {
                    if (!WorldRepairVerification.TryReachPickup(player.transform.position, pickup, out var stand))
                    { result.failures.Add(pickup.name + ": no route during play"); continue; }
                    var direction = pickup.transform.position - stand; direction.y = 0;
                    player.Warp(stand, Quaternion.LookRotation(direction.normalized));
                    yield return new WaitForSeconds(.25f);
                    var interactor = player.GetComponent<PlayerInteractor>();
                    if (interactor == null) interactor = player.GetComponentInChildren<PlayerInteractor>();
                    if (interactor == null || !ReferenceEquals(interactor.Current, pickup))
                    { result.failures.Add(pickup.name + ": no interaction prompt at reachable standing point"); continue; }
                    profile.Inventory.TryGetValue(pickup.ItemId, out int before);
                    int expected = before + pickup.Count;
                    string name = pickup.name;
                    pickup.Interact(player.gameObject);
                    pickup.Interact(player.gameObject);
                    yield return null;
                    profile.Inventory.TryGetValue(pickup.ItemId, out int after);
                    if (after != expected || pickup.gameObject.activeSelf)
                        result.failures.Add(name + ": pickup did not grant exactly once and disappear");
                    else result.checks.Add(name + ": reachable prompt, granted once, hidden after collection");
                    Save();
                }
            }

            private IEnumerator CheckBridge()
            {
                var bridge = GameObject.Find("Route_Bridge_01");
                var player = FindFirstObjectByType<PlayerLocomotion>();
                if (bridge == null || player == null)
                { result.failures.Add("Bridge test: streamed route missing"); yield break; }
                var start = bridge.transform.TransformPoint(new Vector3(6, 1.25f, 0));
                var end = bridge.transform.TransformPoint(new Vector3(-6, 1.25f, 0));
                if (!WalkableGround.TryFind(start, .5f, out var floor))
                { result.failures.Add("Bridge test: dry approach missing"); yield break; }
                var direction = end - start; direction.y = 0; direction.Normalize();
                player.Warp(floor + Vector3.up*.03f, Quaternion.LookRotation(direction));
                var controller = player.GetComponent<CharacterController>();
                float deadline = Time.realtimeSinceStartup + 16f;
                bool fell = false;
                while (Vector3.Dot(end-player.transform.position, direction) > .25f && Time.realtimeSinceStartup < deadline)
                {
                    if (player.transform.position.y < -.35f) { fell = true; break; }
                    controller.Move(direction * 2f * Mathf.Min(Time.deltaTime, .05f));
                    yield return null;
                }
                if (fell || Vector3.Distance(player.transform.position, end) > .8f)
                    result.failures.Add($"Bridge: controller could not cross; stopped at {player.transform.position}");
                else result.checks.Add("Bridge: character controller crossed between both dry banks without falling");
                Save();
            }

            private IEnumerator CheckHealing()
            {
                var player = FindFirstObjectByType<PlayerLocomotion>();
                var machine = FindFirstObjectByType<HealingMachine>();
                if (player == null || machine == null || !ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile is not PlayerProfile concrete)
                { result.failures.Add("Pokemon Center: healing service missing"); yield break; }
                if (concrete.Party.Count == 0) concrete.TryAddToParty(PokeLab.Overworld.CreatureFactory.Create(433, 5, 91));
                var creature = concrete.Party[0];
                creature.CurrentHp = 1;
                if (creature.Moves.Count > 0)
                {
                    var move = creature.Moves[0]; move.CurrentPp = 0; creature.Moves[0] = move;
                }
                player.Warp(new Vector3(0, .03f, .15f), Quaternion.identity);
                yield return new WaitForSeconds(.3f);
                var interactor = player.GetComponent<PlayerInteractor>();
                if (interactor == null || !ReferenceEquals(interactor.Current, machine))
                    result.failures.Add("Pokemon Center: no healing prompt in front of reception");
                machine.Interact(player.gameObject);
                yield return new WaitForSeconds(2.8f);
                if (creature.CurrentHp != creature.MaxHp || player.IsMotionFrozen ||
                    (creature.Moves.Count > 0 && creature.Moves[0].CurrentPp != creature.Moves[0].MaxPp))
                    result.failures.Add("Pokemon Center: healing or control release failed");
                else result.checks.Add("Pokemon Center: reception restores HP/PP and releases player controls");
                player.Warp(GameObject.Find("Spawn_FromDoor").transform.position, Quaternion.identity);
                Save();
            }

            private IEnumerator WalkThrough(Vector3 direction, string from, string to)
            {
                float until = Time.realtimeSinceStartup+12f;
                while (SceneManager.GetActiveScene().name == from && Time.realtimeSinceStartup < until)
                {
                    var player = FindFirstObjectByType<PlayerLocomotion>();
                    if (player != null && !player.IsMotionFrozen)
                    {
                        var controller = player.GetComponent<CharacterController>();
                        if (controller != null && controller.enabled)
                            controller.Move(direction*2f*Mathf.Min(Time.deltaTime,.05f));
                    }
                    yield return null;
                }
            }
        }
    }
}
