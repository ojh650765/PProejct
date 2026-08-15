using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using PokeLab.Core;

namespace PokeLab.Cinematics
{
    /// <summary>Which sprite frames a state draws from.</summary>
    public enum SpriteTrack
    {
        /// <summary>The looping Gen 5 idle animation for the current facing. Real authored frames.</summary>
        IdleLoop = 0,
        /// <summary>
        /// The idle loop, held on a single frame rather than advancing. Used where the creature
        /// must be visibly still — sleep, a held faint pose, the moment of a hit.
        /// </summary>
        IdleFrozen = 1,
        /// <summary>
        /// The idle loop played faster than authored. Gen 5 has no run cycle; speeding the
        /// idle is what the traditional games do for urgency and it reads correctly.
        /// </summary>
        IdleFast = 2,
        /// <summary>No sprite drawn at all — the creature is inside its ball.</summary>
        Hidden = 3,
    }

    /// <summary>
    /// The procedural recipe layered on top of the frames. Every one of these is a runtime
    /// transform of authored frames, which is exactly the vocabulary the traditional games
    /// use: Gen 1-5 never drew an attack pose, they slid, shook, squashed and flashed the one
    /// sprite they had.
    /// </summary>
    public enum MotionRecipe
    {
        None = 0,
        /// <summary>Travel into contact and back. <see cref="CreatureMotionLayer.Lunge"/>.</summary>
        Lunge = 1,
        /// <summary>Compress, hold, throw the body into the release. <see cref="CreatureMotionLayer.Plant"/>.</summary>
        Plant = 2,
        /// <summary>A rising swell with no travel. <see cref="CreatureMotionLayer.Swell"/>.</summary>
        Swell = 3,
        /// <summary>Knocked back and wobbling, scaled by damage. <see cref="CreatureMotionLayer.Recoil"/>.</summary>
        Recoil = 4,
        /// <summary>Clear the line of the attack and return. <see cref="CreatureMotionLayer.Dodge"/>.</summary>
        Dodge = 5,
        /// <summary>Fall, compress on contact, rebound. <see cref="CreatureMotionLayer.Land"/>.</summary>
        Land = 6,
        /// <summary>Slide down, squash and fade out. <see cref="CreatureMotionLayer.Sink"/>.</summary>
        Sink = 7,
        /// <summary>Scale in from the ball with a bounce. <see cref="CreatureMotionLayer.Pop"/>.</summary>
        Pop = 8,
        /// <summary>Shrink toward the ball and vanish. <see cref="CreatureMotionLayer.RecallShrink"/>.</summary>
        Shrink = 9,
        /// <summary>Decaying hops. <see cref="CreatureMotionLayer.Celebrate"/>.</summary>
        Hop = 10,
        /// <summary>A slow horizontal drift, driven by the presenter's own tween.</summary>
        Drift = 11,
        /// <summary>The idle breathing bob and nothing else.</summary>
        Breathe = 12,
    }

    /// <summary>
    /// One row of the animation contract: what a <see cref="CreatureAnimation"/> value looks
    /// like once creatures are pixel sprites.
    /// </summary>
    public readonly struct CreatureAnimationPlan
    {
        public readonly CreatureAnimation Animation;
        public readonly SpriteTrack Track;
        public readonly MotionRecipe Recipe;
        /// <summary>Playback rate multiplier applied to the idle loop's authored fps.</summary>
        public readonly float FrameRateScale;
        /// <summary>White flash strength on entry, 0-1. Drives <c>_FlashAmount</c> on the billboard.</summary>
        public readonly float Flash;
        /// <summary>
        /// Alpha the state <i>ends</i> on. Descriptive, not applied: opacity is driven frame by
        /// frame from <see cref="CreatureMotionLayer.Fade"/>, because a faint has to fade over
        /// the course of its collapse rather than on the frame the state is entered.
        /// </summary>
        public readonly float Alpha;
        /// <summary>Crossfade in seconds, used for both Animator states and sprite-sheet swaps.</summary>
        public readonly float Crossfade;
        /// <summary>Why this row is what it is. Printed by the editor tool.</summary>
        public readonly string Notes;

        public CreatureAnimationPlan(CreatureAnimation animation, SpriteTrack track, MotionRecipe recipe,
            float frameRateScale, float flash, float alpha, float crossfade, string notes)
        {
            Animation = animation;
            Track = track;
            Recipe = recipe;
            FrameRateScale = frameRateScale;
            Flash = flash;
            Alpha = alpha;
            Crossfade = crossfade;
            Notes = notes;
        }

        /// <summary>True when the state draws authored frames that advance on their own.</summary>
        public bool UsesRealFrames => Track == SpriteTrack.IdleLoop || Track == SpriteTrack.IdleFast;
    }

    /// <summary>
    /// The complete <see cref="CreatureAnimation"/> → sprite mapping.
    ///
    /// <b>What we actually have.</b> The official Gen 5 Black/White sprite set gives a looping
    /// idle animation, front and back, for every species in the slice — and nothing else.
    /// There is no attack frame, no hit frame, no faint frame, because Gen 5 did not draw one:
    /// the battle engine slid, shook, squashed and flashed the idle sprite. So exactly two of
    /// the fourteen states in <see cref="CreatureAnimation"/> are real frames, and the other
    /// twelve are runtime transforms of those frames.
    ///
    /// That is not a shortfall to apologise for. It is the format, and matching it is what
    /// makes the result read as Pokémon rather than as a 3D game wearing sprites.
    ///
    /// <b>Two conventions worth stating.</b> First, "frozen" is a real choice, not an absence:
    /// a creature that keeps breathing through its own knockout undoes the beat, so
    /// <see cref="CreatureAnimation.Faint"/>, <see cref="CreatureAnimation.Hit"/> and
    /// <see cref="CreatureAnimation.Sleep"/> deliberately stop the loop. Second, where a state
    /// has no authored frames <i>and</i> a rigged 3D prefab happens to be bound instead of a
    /// billboard, <see cref="CreatureView"/> still drives the Animator by name — the two paths
    /// coexist, so a 3D actor left in the project keeps working.
    /// </summary>
    public static class CreatureAnimationPlans
    {
        private static readonly Dictionary<CreatureAnimation, CreatureAnimationPlan> Table = Build();

        /// <summary>The plan for a state. Falls back to a breathing idle for an unknown value.</summary>
        public static CreatureAnimationPlan For(CreatureAnimation animation)
            => Table.TryGetValue(animation, out var plan)
                ? plan
                : new CreatureAnimationPlan(animation, SpriteTrack.IdleLoop, MotionRecipe.Breathe,
                    1f, 0f, 1f, 0.18f, "unmapped state; treated as a battle idle");

        /// <summary>Every row, in enum order. Used by the editor report and by integration checks.</summary>
        public static IEnumerable<CreatureAnimationPlan> All()
        {
            foreach (CreatureAnimation a in Enum.GetValues(typeof(CreatureAnimation)))
                yield return For(a);
        }

        private static Dictionary<CreatureAnimation, CreatureAnimationPlan> Build()
        {
            var t = new Dictionary<CreatureAnimation, CreatureAnimationPlan>();

            void Add(CreatureAnimation a, SpriteTrack track, MotionRecipe recipe,
                float rate, float flash, float alpha, float crossfade, string notes)
                => t[a] = new CreatureAnimationPlan(a, track, recipe, rate, flash, alpha, crossfade, notes);

            // --- Real frames -------------------------------------------------------------
            Add(CreatureAnimation.Idle, SpriteTrack.IdleLoop, MotionRecipe.Breathe,
                0.85f, 0f, 1f, 0.30f,
                "REAL FRAMES. The Gen 5 idle loop, slowed slightly; this is the overworld idle, " +
                "so it should read calmer than the battle one.");

            Add(CreatureAnimation.IdleBattle, SpriteTrack.IdleLoop, MotionRecipe.Breathe,
                1.00f, 0f, 1f, 0.26f,
                "REAL FRAMES. The authored Gen 5 idle at its authored rate. This is the state the " +
                "creature spends most of the battle in and the only one the art was drawn for.");

            // --- Locomotion: no authored cycle exists ------------------------------------
            Add(CreatureAnimation.Walk, SpriteTrack.IdleLoop, MotionRecipe.Drift,
                1.15f, 0f, 1f, 0.20f,
                "PROCEDURAL. Gen 5 has no walk cycle. The idle plays slightly fast while the " +
                "presenter tweens the root; a sprite translating with a live idle reads as walking.");

            Add(CreatureAnimation.Run, SpriteTrack.IdleFast, MotionRecipe.Drift,
                1.55f, 0f, 1f, 0.16f,
                "PROCEDURAL. Same as Walk with the loop pushed to 1.55×, which is the traditional " +
                "urgency cue — speed of the existing cycle, not a different cycle.");

            // --- Attacks -----------------------------------------------------------------
            Add(CreatureAnimation.AttackPhysical, SpriteTrack.IdleLoop, MotionRecipe.Lunge,
                1.30f, 0f, 1f, 0.07f,
                "PROCEDURAL. Lunge: travel into contact, stretch along the direction of travel, " +
                "recover slowly. Exactly the Gen 1-5 physical attack — the sprite moves, it does not repose.");

            Add(CreatureAnimation.AttackSpecial, SpriteTrack.IdleLoop, MotionRecipe.Plant,
                1.20f, 0.12f, 1f, 0.10f,
                "PROCEDURAL. Plant: compress and sink, hold, extend past neutral on release. A small " +
                "charge flash sells the energy without needing a charged pose.");

            Add(CreatureAnimation.AttackStatus, SpriteTrack.IdleLoop, MotionRecipe.Swell,
                1.10f, 0.08f, 1f, 0.12f,
                "PROCEDURAL. Swell: rise and scale up briefly. No travel — a status move should not " +
                "look like an attack that missed.");

            // --- Reactions ---------------------------------------------------------------
            Add(CreatureAnimation.Hit, SpriteTrack.IdleFrozen, MotionRecipe.Recoil,
                1f, 1.00f, 1f, 0.045f,
                "PROCEDURAL. Recoil offset plus a full white flash, and the loop freezes for the " +
                "duration. The freeze is the half people forget: a creature that keeps breathing " +
                "through the hit does not read as having been hit.");

            Add(CreatureAnimation.Dodge, SpriteTrack.IdleLoop, MotionRecipe.Dodge,
                1.35f, 0f, 1f, 0.05f,
                "PROCEDURAL. Lateral clear plus a hop and a screen-plane roll. Must start before the " +
                "attack arrives, which is why the presenter reads the miss ahead of the playhead.");

            Add(CreatureAnimation.Faint, SpriteTrack.IdleFrozen, MotionRecipe.Sink,
                1f, 0.25f, 0f, 0.13f,
                "PROCEDURAL. Slide down through the ground plane with a vertical squash and a fade to " +
                "zero alpha. This is the Gen 5 faint, and it is the right one: a billboard tipped over " +
                "on its side shows the player a rectangle, because a quad has no underside.");

            Add(CreatureAnimation.Celebrate, SpriteTrack.IdleLoop, MotionRecipe.Hop,
                1.25f, 0f, 1f, 0.22f,
                "PROCEDURAL. Three decaying hops with a slight squash on each landing.");

            // --- Ball ---------------------------------------------------------------------
            Add(CreatureAnimation.SentOut, SpriteTrack.IdleLoop, MotionRecipe.Pop,
                1.20f, 0.55f, 1f, 0.06f,
                "PROCEDURAL. Scale in from near zero at the burst point with an overshoot and a " +
                "settle, under a bright entry flash. The flash is what hides the first frames, which " +
                "are the ones where a scaled-up sprite looks worst.");

            Add(CreatureAnimation.Recalled, SpriteTrack.IdleLoop, MotionRecipe.Shrink,
                1f, 0.45f, 0f, 0.08f,
                "PROCEDURAL. The exact reverse of SentOut: shrink toward the ball, drawn upward, " +
                "fading, with the recall-beam flash rising as the sprite goes.");

            // --- Status --------------------------------------------------------------------
            Add(CreatureAnimation.Sleep, SpriteTrack.IdleFrozen, MotionRecipe.Breathe,
                1f, 0f, 1f, 0.40f,
                "PROCEDURAL. The loop freezes and only the breathing bob runs, at the motion layer's " +
                "own slow rate. A frozen sprite that still rises and falls is unmistakably asleep.");

            return t;
        }

        /// <summary>
        /// A human-readable version of the whole table. Logged by the editor tool so the
        /// mapping can be reviewed without reading this file.
        /// </summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.AppendLine("CreatureAnimation → sprite mapping (frames vs procedural)");
            sb.AppendLine($"{"State",-16} {"Track",-12} {"Recipe",-9} {"Rate",5} {"Flash",6} {"Alpha",6} {"Xfade",6}  Notes");
            foreach (var plan in All())
            {
                sb.AppendLine($"{plan.Animation,-16} {plan.Track,-12} {plan.Recipe,-9} " +
                              $"{plan.FrameRateScale,5:0.00} {plan.Flash,6:0.00} {plan.Alpha,6:0.00} " +
                              $"{plan.Crossfade,6:0.000}  {plan.Notes}");
            }
            return sb.ToString();
        }
    }
}
