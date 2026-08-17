using System.Collections;
using System.IO;
using PokeLab.Core;
using PokeLab.Overworld;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Starts the game already inside one moment of it.
    ///
    /// Reaching the starter scene the ordinary way means sitting through the opening, the name
    /// prompt and a walk across town — every time, for a change to the two seconds where the
    /// case opens. Reaching a battle means walking in grass until one happens. Most of the
    /// faults found in this project have been at the far end of a sequence like that, and the
    /// cost of getting there is why several of them survived so long.
    ///
    /// So each moment gets a door. The editor menu writes <c>Temp/pokelab_jump.txt</c> and
    /// presses Play; this reads it on the first frame and drives the flow to that point using
    /// the same public entry points the game itself uses. Nothing here is a shortcut *through*
    /// a system — a battle started this way is a real battle, staged by the real director — so
    /// what you see after the jump is what the game does.
    ///
    /// The file is deleted as soon as it is read, so the next plain Play is a plain Play.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class DebugJump : MonoBehaviour
    {
        private const string RequestName = "pokelab_jump.txt";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            // Compiled out of a web player, because there is no disk behind this path there.
            //
            // A browser build runs on Emscripten's in-memory filesystem: there is no project
            // folder, no Temp, and no working directory in the sense this code means. The best
            // case is that GetCurrentDirectory answers a virtual root and File.Exists answers
            // false, and the door simply never opens — the worst case is that a stripped
            // IL2CPP filesystem throws out of a RuntimeInitializeOnLoadMethod, which runs
            // before any scene script and would take the whole page down on load with a
            // stack trace nobody can read and no way to attach a debugger.
            //
            // Guarded at compile time rather than with an Application.platform check so the
            // file I/O is not merely skipped but absent: this is a development door, and a
            // published page is not somewhere it should be reachable at all.
#if !UNITY_WEBGL || UNITY_EDITOR
            var path = Path.Combine(Directory.GetCurrentDirectory(), "Temp", RequestName);
            if (!File.Exists(path)) return;

            string request;
            try { request = File.ReadAllText(path).Trim(); }
            catch { return; }
            finally { try { File.Delete(path); } catch { /* a stale file is harmless */ } }

            if (string.IsNullOrEmpty(request)) return;

            // Raised here rather than in the coroutine, because this callback runs before any
            // component's Start — including the one that decides to play the opening. Waiting
            // for the runner to be idle could not win that race; not entering it does.
            // Every jump but the opening itself. The starter case lives inside
            // opening_departure, which this drives directly, so the prologue playing underneath
            // it would be two episodes fighting over the player's control flag.
            PokeLab.Core.DebugFlow.SuppressOpening = request != "opening";

            var host = new GameObject("~DebugJump");
            DontDestroyOnLoad(host);
            host.AddComponent<DebugJump>().Begin(request);
#endif
        }

        private string _request;

        private void Begin(string request)
        {
            _request = request;
            StartCoroutine(Run());
        }

        private IEnumerator Run()
        {
            // The hosts build themselves across the first few frames and the streamer brings in
            // the neighbouring band after that. Jumping before any of it exists finds a null
            // runner and reports "no such episode" for one that is about to load.
            yield return WaitForHosts();

            // Jumps that are not the opening say the opening is already done.
            //
            // A fresh save auto-plays it, and every other door then sat behind twelve lines of
            // prologue waiting for it to finish — so "take me to a wild battle" delivered the
            // professor instead. Marking the flag is the same thing finishing the opening would
            // do, so nothing downstream can tell the difference.
            if (_request != "opening" && _request != "starter") SkipOpening();

            // Doors that are not a story beat also say the opening act is over.
            //
            // The prologue holds three ambient systems down until the Pokédex is granted — grass
            // encounters, the roamer population and trainer challenges (see StoryPhase). A jump
            // labelled "wild battle" or "free roam on the route" that landed inside that window
            // would show an empty field with nothing in the grass and no explanation, which is
            // the door lying about the game. Story jumps deliberately do not do this: they are
            // the act, and several of them are gated on the flag not being set yet.
            if (_request == "wild" || _request == "trainer" || _request == "npc") EndPrologue();

            Debug.Log($"[DebugJump] Entering '{_request}'.");

            switch (_request)
            {
                case "opening": yield return Episode("opening"); break;
                case "starter": yield return Starter(); break;
                case "wild": yield return Battle(wild: true); break;
                case "trainer": yield return Battle(wild: false); break;
                case "npc": yield return TalkToSomebody(); break;

                default:
                    // Anything else is treated as an episode id, so a beat added to the book
                    // is reachable without this file changing.
                    yield return Episode(_request);
                    break;
            }
        }

        private static void SkipOpening()
        {
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile == null) return;
            if (profile is PlayerProfile concrete) concrete.SetFlagBool("story.opening_seen", true);


            // Not cancelled if it has already begun. The runner deliberately offers no way to
            // stop an episode mid-beat, because half its beats take the player's control away
            // and give it back at the end — killing one between those two points leaves the
            // player frozen. WaitForIdle sits it out instead, and the flag means the next jump
            // does not have to.
        }

        private static void EndPrologue()
        {
            if (!ServiceHub.TryGet<IPlayerProfile>(out var profile) || profile == null) return;
            if (profile is PlayerProfile concrete) concrete.SetFlagBool(StoryPhase.PokedexFlag, true);
        }

        private IEnumerator WaitForHosts()
        {
            var deadline = Time.realtimeSinceStartup + 12f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (FindFirstObjectByType<EpisodeRunner>() != null
                    && FindFirstObjectByType<GameFlowController>() != null) break;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.6f);
        }

        private IEnumerator Episode(string id)
        {
            var runner = FindFirstObjectByType<EpisodeRunner>();
            if (runner == null) { Warn("no EpisodeRunner in the scene"); yield break; }

            // Waited out rather than cancelled. A fresh save auto-plays the opening, and the
            // runner deliberately offers no way to stop one mid-beat: half its beats take the
            // player's control away and give it back at the end, so an episode killed between
            // those two points leaves the player frozen. Yielding costs a moment on the jumps
            // that collide with the opening and cannot strand anybody.
            yield return WaitForIdle(runner);

            if (!runner.Play(id)) Warn($"'{id}' is not an episode in the book");
        }

        /// <summary>
        /// The starter choice on its own, without the episode that normally wraps it.
        ///
        /// Asked for through StarterSelection rather than by playing the opening from the top,
        /// because the point of this door is to reach the case in a second and not to re-test
        /// the twelve lines in front of it.
        /// </summary>
        private IEnumerator Starter()
        {
            var runner = FindFirstObjectByType<EpisodeRunner>();
            if (runner == null) { Warn("no EpisodeRunner in the scene"); yield break; }

            yield return WaitForIdle(runner);

            // field_bag_left, not the episode that holds the ChooseStarter beat itself.
            //
            // The choice moved out of the town gate and onto the lake bank, and it is no longer a
            // case being opened in front of the player: the beat that stages the creature lives
            // in field_bag_left and chains into field_ambush, and field_ambush's own approach and
            // battle beats are both about a creature that beat placed. Entering at field_ambush
            // would open the desk with nothing standing over it — which is the half of the moment
            // that changed, so it is the half this door has to show.
            if (!runner.Play("field_bag_left"))
                Warn("'field_bag_left' is not in the book, so the starter choice cannot open");
        }

        private IEnumerator Battle(bool wild)
        {
            var flow = FindFirstObjectByType<GameFlowController>();
            if (flow == null) { Warn("no GameFlowController in the scene"); yield break; }

            var runner = FindFirstObjectByType<EpisodeRunner>();
            yield return WaitForIdle(runner);

            var request = new EncounterRequest
            {
                Kind = wild ? BattleKind.Wild : BattleKind.Trainer,
                WildSpeciesId = 19,          // Rattata: common on the route this starts in
                WildLevel = 5,
                TrainerId = wild ? null : "rival_first",
                BiomeId = "route_01",
            };

            flow.RequestEncounter(request, result =>
                Debug.Log($"[DebugJump] Battle finished: {result}."));
        }

        /// <summary>Walks up to the nearest person and talks to them.</summary>
        private IEnumerator TalkToSomebody()
        {
            var runner = FindFirstObjectByType<EpisodeRunner>();
            yield return WaitForIdle(runner);

            var player = FindFirstObjectByType<PlayerLocomotion>();
            var speakers = FindObjectsByType<NpcController>(FindObjectsSortMode.None);
            if (player == null || speakers == null || speakers.Length == 0)
            {
                Warn("nobody in this scene has anything to say");
                yield break;
            }

            // Nearest rather than first, so the jump lands on somebody standing in the part of
            // town the player spawns in instead of whichever object the scene happens to list
            // first — which may be a band away and invisible.
            NpcController nearest = null;
            var best = float.MaxValue;
            foreach (var speaker in speakers)
            {
                if (speaker == null) continue;
                var distance = (speaker.transform.position - player.transform.position).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                nearest = speaker;
            }

            if (nearest == null) { Warn("nobody in this scene has anything to say"); yield break; }

            // Put in front of them and turned to face, because an interaction that fires from
            // across the square would not show whether the approach reads correctly.
            var toPlayer = (player.transform.position - nearest.transform.position);
            toPlayer.y = 0f;
            if (toPlayer.sqrMagnitude < 0.01f) toPlayer = Vector3.back;
            player.Warp(nearest.transform.position + toPlayer.normalized * 1.4f,
                Quaternion.LookRotation(-toPlayer.normalized, Vector3.up));

            yield return new WaitForSecondsRealtime(0.4f);
            nearest.Interact(player.gameObject);
        }

        /// <summary>Waits until no episode is running, or gives up after a while.</summary>
        private static IEnumerator WaitForIdle(EpisodeRunner runner)
        {
            if (runner == null) yield break;

            // Idle for a continuous stretch, not idle for one frame.
            //
            // The opening auto-plays, but it waits for the player profile to be registered
            // first — so on a fresh save it starts *after* this check would have run. A single
            // sample found the runner idle, the jump fired an encounter, and the prologue then
            // opened on top of a live battle. Requiring the idleness to hold means an episode
            // that is merely late still gets caught.
            var deadline = Time.realtimeSinceStartup + 45f;
            var idleSince = Time.realtimeSinceStartup;

            while (Time.realtimeSinceStartup < deadline)
            {
                if (runner.IsPlaying) idleSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - idleSince >= 1.5f) yield break;
                yield return null;
            }
        }

        private void Warn(string why) =>
            Debug.LogWarning($"[DebugJump] '{_request}' could not start: {why}.", this);
    }
}
