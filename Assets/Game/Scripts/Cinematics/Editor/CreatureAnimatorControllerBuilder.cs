using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using PokeLab.Core;
using PokeLab.Cinematics;

namespace PokeLab.Cinematics.EditorTools
{
    /// <summary>
    /// Builds the creature Animator Controller from code.
    ///
    /// Written as a generator rather than committed as a hand-authored <c>.controller</c>
    /// for three reasons: there is one state per <see cref="CreatureAnimation"/> value and
    /// the enum is the contract, so the controller should be derived from it rather than
    /// kept in sync with it by hand; the transition durations are the "animation snapping"
    /// QA item and belong next to the reasoning that chose them
    /// (<see cref="CreatureView.DefaultCrossfade"/>); and a merge conflict inside a serialized
    /// controller asset is not resolvable, whereas a conflict in this file is.
    ///
    /// The controller it produces is driven entirely by
    /// <c>Animator.CrossFadeInFixedTime</c> from <see cref="CreatureView"/>. There are no
    /// parameters and no transitions between states — the code owns sequencing, and an
    /// Animator that also owns sequencing fights it.
    /// </summary>
    public static class CreatureAnimatorControllerBuilder
    {
        private const string DefaultOutputFolder = "Assets/Game/Scripts/Cinematics/Generated";
        private const string ControllerName = "CreatureBattle.controller";

        [MenuItem("Poké Lab/Cinematics/Build Creature Animator Controller")]
        public static void BuildDefault()
        {
            string path = Build(null, DefaultOutputFolder, ControllerName);
            if (string.IsNullOrEmpty(path)) return;

            Debug.Log($"[Cinematics] Built creature animator controller at {path}. " +
                      "Assign it to each creature prefab's Animator, then run " +
                      "'Poké Lab/Cinematics/Bind Clips To Controller' with the rig's clips selected.");
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimatorController>(path));
        }

        /// <summary>
        /// Creates or overwrites the controller.
        ///
        /// <paramref name="clipsByAnimation"/> is optional; when supplied, each state is
        /// bound to its clip. When a value is missing the state is still created with an
        /// empty motion, because a controller missing a state causes
        /// <see cref="CreatureView"/> to fall back to the battle idle for that beat, and a
        /// silently substituted beat is far harder to notice than an empty one.
        /// </summary>
        /// <returns>The asset path, or null on failure.</returns>
        public static string Build(
            IReadOnlyDictionary<CreatureAnimation, AnimationClip> clipsByAnimation,
            string folder, string fileName)
        {
            if (string.IsNullOrEmpty(folder)) folder = DefaultOutputFolder;
            if (string.IsNullOrEmpty(fileName)) fileName = ControllerName;

            if (!Directory.Exists(folder))
            {
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }

            string path = Path.Combine(folder, fileName).Replace('\\', '/');

            // Delete rather than edit in place. Regenerating over an existing controller
            // leaves orphaned states behind if the CreatureAnimation enum ever shrinks, and
            // a stale state silently keeps working until someone wonders why it never plays.
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(path) != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            var layer = controller.layers[0];
            var machine = layer.stateMachine;

            // A single unnamed layer. Additive layers would let the Animator blend two beats
            // at once, which is exactly what the presenter's sequential queue is designed to
            // prevent, so the rig deliberately does not offer that option.
            layer.name = "Battle";
            controller.layers = new[] { layer };

            var states = new Dictionary<CreatureAnimation, AnimatorState>();
            float y = 0f;

            foreach (CreatureAnimation animation in Enum.GetValues(typeof(CreatureAnimation)))
            {
                string stateName = animation.ToString();
                var state = machine.AddState(stateName, new Vector3(260f, y, 0f));
                y += 55f;

                state.speed = 1f;
                state.writeDefaultValues = false; // explicit poses; avoids leaking a previous beat's pose
                state.tag = stateName;

                if (clipsByAnimation != null && clipsByAnimation.TryGetValue(animation, out var clip) && clip != null)
                {
                    state.motion = clip;
                }

                states[animation] = state;
            }

            // Battle idle is the default: a creature only ever has this controller during a
            // battle, and defaulting to the overworld idle produces one wrong frame at bind.
            if (states.TryGetValue(CreatureAnimation.IdleBattle, out var idle)) machine.defaultState = idle;

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return path;
        }

        /// <summary>
        /// Binds clips in the selected folder to the states of a selected controller by name.
        ///
        /// Matching is by suffix rather than by exact name, because the art contract names
        /// clips <c>Creature_&lt;SpeciesId&gt;_&lt;NameEn&gt;_&lt;Animation&gt;</c> while the
        /// states are named after the enum alone.
        /// </summary>
        [MenuItem("Poké Lab/Cinematics/Bind Clips To Controller")]
        public static void BindSelectedClips()
        {
            AnimatorController controller = null;
            var clips = new List<AnimationClip>();

            foreach (var obj in Selection.objects)
            {
                if (obj is AnimatorController c) controller = c;
                else if (obj is AnimationClip clip) clips.Add(clip);
                else if (obj is GameObject go)
                {
                    // An imported FBX carries its clips as sub-assets.
                    string assetPath = AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(assetPath)) continue;
                    foreach (var sub in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                    {
                        if (sub is AnimationClip subClip && !subClip.name.StartsWith("__preview__"))
                            clips.Add(subClip);
                    }
                }
            }

            if (controller == null)
            {
                Debug.LogWarning("[Cinematics] Select an AnimatorController along with the clips or the FBX to bind.");
                return;
            }
            if (clips.Count == 0)
            {
                Debug.LogWarning("[Cinematics] No AnimationClips found in the selection.");
                return;
            }

            int bound = 0;
            var missing = new List<string>();

            foreach (var state in controller.layers[0].stateMachine.states)
            {
                string wanted = state.state.name;
                AnimationClip match = null;

                foreach (var clip in clips)
                {
                    if (clip.name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                        clip.name.EndsWith("_" + wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        match = clip;
                        break;
                    }
                }

                if (match != null) { state.state.motion = match; bound++; }
                else missing.Add(wanted);
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();

            Debug.Log($"[Cinematics] Bound {bound} clip(s) to {controller.name}.");
            if (missing.Count > 0)
            {
                // Reported as a warning, not an error: a stub rig legitimately lacks clips
                // early on, and the choreography still reads through the procedural layer.
                Debug.LogWarning($"[Cinematics] No clip found for: {string.Join(", ", missing)}. " +
                                 "Those states will fall back to IdleBattle at runtime.");
            }
        }

        /// <summary>
        /// Reports the crossfade each state is driven with, so the animation authors can see
        /// how long their clip has to establish itself before it is fully weighted.
        /// </summary>
        [MenuItem("Poké Lab/Cinematics/Log Crossfade Table")]
        public static void LogCrossfadeTable()
        {
            var sb = new System.Text.StringBuilder("[Cinematics] Creature crossfade durations (seconds, fixed time):\n");
            foreach (CreatureAnimation animation in Enum.GetValues(typeof(CreatureAnimation)))
            {
                sb.AppendLine($"  {animation,-16} {CreatureView.DefaultCrossfade(animation):0.000}");
            }
            Debug.Log(sb.ToString());
        }
    }
}
