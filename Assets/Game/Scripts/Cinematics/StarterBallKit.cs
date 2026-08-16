using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Serialized references to the two capture-ball models the starter case needs: the
    /// closed one that sits in the case, and the sprung-open one it becomes.
    ///
    /// An asset rather than a path, for <c>CreatureArtCatalog</c>'s stated reason:
    /// <c>Env_Prop_CaptureBall.fbx</c> lives in <c>Assets/Game/Art/Props</c>, which is
    /// outside Resources and outside Addressables, so nothing but a serialized reference gets
    /// it into a build — and nothing but a Resources-resident owner of that reference gets it
    /// to a component that the scene rebuild creates with <c>AddComponent</c> and therefore
    /// cannot hand any inspector wiring to.
    ///
    /// Built and kept up to date by <c>StarterBallKitBuilder</c>. Absent, the case still opens
    /// and still plays: <see cref="StarterCaseStage"/> falls back to a primitive sphere, which
    /// is a legible stand-in and a visibly wrong one, rather than three empty pedestals.
    /// </summary>
    public sealed class StarterBallKit : ScriptableObject
    {
        /// <summary>Where <c>Resources.Load</c> finds it. The builder writes to a matching path.</summary>
        public const string ResourceName = "StarterBallKit";

        [Tooltip("Env_Prop_CaptureBall — the plain livery. The starters are not in Greats.")]
        public GameObject Closed;

        [Tooltip("Env_Prop_CaptureBall_Open — the same ball sprung apart. Pivot is the volume " +
                 "centre on both, so one can be swapped for the other in place.")]
        public GameObject Open;

        public static StarterBallKit Load() => Resources.Load<StarterBallKit>(ResourceName);
    }
}
