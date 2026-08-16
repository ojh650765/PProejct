using System;
using System.IO;
using PokeLab.Overworld;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PokeLab.Boot.Editor
{
    /// <summary>
    /// One menu item per moment worth looking at.
    ///
    /// Each opens the scene that moment happens in, clears the save when the moment only
    /// exists in a new game, writes the request <see cref="DebugJump"/> reads, and presses
    /// Play. What runs afterwards is the game's own flow — these are doors into it, not
    /// re-creations of it, so a fault seen through one of them is a real fault.
    /// </summary>
    public static class DebugJumpMenu
    {
        private const string Town = "Assets/Game/Scenes/Town.unity";
        private const string Field = "Assets/Game/Scenes/Field.unity";

        [MenuItem("Tools/Poké Lab/Debug/Opening Dialogue", priority = 100)]
        public static void Opening() => Jump("opening", Town, freshSave: true);

        [MenuItem("Tools/Poké Lab/Debug/Starter Selection", priority = 101)]
        public static void Starter() => Jump("starter", Field, freshSave: true);

        [MenuItem("Tools/Poké Lab/Debug/Talk To Nearest NPC", priority = 102)]
        public static void Npc() => Jump("npc", Town, freshSave: false);

        // One door per beat of the restructured opening act, so each can be looked at without
        // playing the three in front of it. They are episode ids and nothing more: DebugJump
        // treats an unrecognised request as one, so a beat added to episodes.json is reachable
        // by adding a line here and touching nothing else.
        //
        // Every one of them clears the save. An episode whose completion flag is already set is
        // refused by the runner — correctly, it has been seen — and without the clear these
        // doors would open onto whatever the last session left behind.

        [MenuItem("Tools/Poké Lab/Debug/Act 1/1 · Gate Refusal (Bram)", priority = 200)]
        public static void GateRefusal() => Jump("gate_wait_for_kes", Town, freshSave: true);

        [MenuItem("Tools/Poké Lab/Debug/Act 1/2 · Kes' Send-Off", priority = 201)]
        public static void KesSummons() => Jump("kes_summons", Town, freshSave: true);

        [MenuItem("Tools/Poké Lab/Debug/Act 1/3 · Gate Opens", priority = 202)]
        public static void GateOpens() => Jump("gate_opens", Town, freshSave: true);

        [MenuItem("Tools/Poké Lab/Debug/Act 1/4 · Professor On The Bank", priority = 203)]
        public static void ProfessorMutters() => Jump("field_professor_mutters", Field, freshSave: true);

        // The bag beat, not the ambush beat: it is the one that stages the creature, and it
        // chains into field_ambush with no frame in between. Entering at field_ambush would show
        // the dialogue and the choice with nothing standing in the grass.
        [MenuItem("Tools/Poké Lab/Debug/Act 1/5 · Staged Ambush", priority = 204)]
        public static void StagedAmbush() => Jump("field_bag_left", Field, freshSave: true);

        // Reachable on its own, but not representative on its own: entered this way the player
        // has no party, so Linden's "it went with you" is spoken over an empty belt. For a
        // capture that has to read correctly, come in through the ambush door and let it chain.
        [MenuItem("Tools/Poké Lab/Debug/Act 1/6 · Professor Returns", priority = 205)]
        public static void ProfessorReturns() => Jump("field_professor_returns", Field, freshSave: true);

        // Reachable, and degraded on a fresh save for a reason worth knowing: the battle fields
        // the counter to the player's starter, and entered this way there is no starter to
        // counter — the runner warns and Kes falls back to the definition's placeholder party.
        // A capture of the real fight has to come through the act.
        [MenuItem("Tools/Poké Lab/Debug/Act 1/7 · Rival Battle", priority = 206)]
        public static void RivalBattle() => Jump("rival_first_battle", Field, freshSave: true);

        [MenuItem("Tools/Poké Lab/Debug/Wild Battle", priority = 120)]
        public static void WildBattle() => Jump("wild", Field, freshSave: false);

        [MenuItem("Tools/Poké Lab/Debug/Trainer Battle", priority = 121)]
        public static void TrainerBattle() => Jump("trainer", Field, freshSave: false);

        [MenuItem("Tools/Poké Lab/Debug/Free Roam (no jump)", priority = 140)]
        public static void FreeRoam() => Jump(null, Town, freshSave: false);

        [MenuItem("Tools/Poké Lab/Debug/Delete Save", priority = 160)]
        public static void DeleteSave()
        {
            var removed = ClearSave();
            Debug.Log(removed > 0
                ? $"[Debug] Deleted {removed} save file(s); the next Play starts a new game."
                : "[Debug] There was no save to delete.");
        }

        /// <summary>
        /// Opens <paramref name="scene"/>, arms the jump and enters Play.
        /// </summary>
        private static void Jump(string request, string scene, bool freshSave)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning("[Debug] Already in Play mode. Stop first, then pick a jump.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (!File.Exists(scene))
            {
                Debug.LogError($"[Debug] {scene} does not exist, so there is nowhere to jump into.");
                return;
            }

            // Some moments only exist in a new game — the opening will not replay once its
            // completion flag is set, and the starter choice is gone once a starter is held.
            // Clearing the save is the difference between the door working and it silently
            // dropping the player into the middle of their own saved game.
            if (freshSave) ClearSave();

            EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);

            var temp = Path.Combine(Directory.GetCurrentDirectory(), "Temp");
            Directory.CreateDirectory(temp);
            var path = Path.Combine(temp, "pokelab_jump.txt");

            if (string.IsNullOrEmpty(request))
            {
                // Free roam has to delete a request left over from a previous jump, or the
                // "no jump" button would perform whichever one was pressed last.
                if (File.Exists(path)) File.Delete(path);
            }
            else File.WriteAllText(path, request);

            EditorApplication.isPlaying = true;
        }

        /// <summary>Removes the save and its backup. Returns how many files went.</summary>
        private static int ClearSave()
        {
            var removed = 0;
            foreach (var path in new[] { SaveSystem.SavePath, SaveSystem.BackupPath })
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Debug] Could not delete {path}: {e.Message}");
                }
            }
            return removed;
        }
    }
}
