using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.UI
{
    /// <summary>
    /// Which interactive surface the keyboard belongs to.
    ///
    /// Several views here poll <c>Keyboard.current</c> directly, and for good reasons: the
    /// command panel owns its own cursor rather than the EventSystem's, and the dialogue's
    /// option list has to keep working while the world's input is switched off. What none of
    /// them can know on their own is whether something else is sitting on top of them. The
    /// command panel went on reading arrows and Enter behind an open party screen, so one
    /// press moved a battle cursor nobody could see and re-fired Party while the player was
    /// navigating the screen it had just opened — and a battle-time conversation and the panel
    /// both answered the same Enter.
    ///
    /// The rule is one line long: a surface that covers others claims while it is up, and a
    /// surface underneath reads keys only when nothing is claiming above it. Claims are held
    /// by the claiming component rather than by a counter, so a screen destroyed or switched
    /// off without releasing — a scene unload in the middle of a conversation — cannot wedge
    /// the keyboard shut. It simply stops counting.
    /// </summary>
    public static class UiFocus
    {
        private static readonly List<Behaviour> Claims = new List<Behaviour>(4);

        /// <summary>
        /// Claims the keyboard for a surface. Idempotent: a screen that re-opens moves to the
        /// top of the stack rather than stacking a second claim it would have to match.
        /// </summary>
        public static void Claim(Behaviour owner)
        {
            if (owner == null) return;
            Claims.Remove(owner);
            Claims.Add(owner);
        }

        /// <summary>Gives the keyboard back. Safe when the caller never claimed.</summary>
        public static void Release(Behaviour owner)
        {
            if (owner == null) return;
            Claims.Remove(owner);
        }

        /// <summary>
        /// True when <paramref name="surface"/> may read keys this frame: either it holds the
        /// topmost live claim, or nothing above it is claiming at all.
        /// </summary>
        public static bool Owns(Behaviour surface)
        {
            var top = Top();
            return top == null || ReferenceEquals(top, surface);
        }

        /// <summary>
        /// The topmost live claim. Claims whose owner has been destroyed or deactivated are
        /// dropped on the way past, which is what makes a missed Release self-correcting.
        /// </summary>
        public static Behaviour Top()
        {
            for (var i = Claims.Count - 1; i >= 0; i--)
            {
                var claim = Claims[i];
                if (claim != null && claim.isActiveAndEnabled) return claim;
                Claims.RemoveAt(i);
            }
            return null;
        }

        /// <summary>Drops every claim. Called from <see cref="UiLifecycle"/> with the other statics.</summary>
        public static void Reset() => Claims.Clear();
    }
}
