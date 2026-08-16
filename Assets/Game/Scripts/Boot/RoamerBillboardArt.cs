using PokeLab.Cinematics;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Gives a roaming wild creature its pixel sprite, by wrapping the same
    /// <see cref="CreatureBillboard"/> the battle draws with.
    ///
    /// Lives in PokeLab.Boot for the same reason <see cref="SurfMount"/> does: the billboard and
    /// the sprite library are PokeLab.Cinematics, the roamer is PokeLab.Overworld, and Cinematics
    /// already references Overworld — so composition has to happen in the one assembly that can
    /// see both, not in a new reference that would close the cycle.
    ///
    /// The mirror is <see cref="MirrorMode.Auto"/> rather than the battle's <c>Off</c>. That is
    /// the whole difference between the two uses: a battler is drawn at the three-quarter the
    /// arena already views it from and must never be flipped, but a creature wandering a route
    /// genuinely walks left and right in front of the player, and one that faces the same way
    /// whichever direction it is travelling reads as sliding rather than walking.
    /// </summary>
    public sealed class RoamerBillboardArt : IOverworldCreatureArt
    {
        private readonly CreatureBillboard _billboard;
        private readonly GameObject _host;

        internal RoamerBillboardArt(Transform parent)
        {
            _host = new GameObject("RoamerSprite");
            _host.transform.SetParent(parent, false);
            _host.transform.localPosition = Vector3.zero;
            _host.transform.localRotation = Quaternion.identity;

            _host.AddComponent<MeshFilter>();
            _host.AddComponent<MeshRenderer>();
            _billboard = _host.AddComponent<CreatureBillboard>();
            _billboard.Mirror = MirrorMode.Auto;
        }

        public Transform Root => _host != null ? _host.transform : null;

        public void Bind(int speciesId, float displayHeight)
        {
            if (_billboard == null) return;
            // No fallback portrait. In battle a portrait is better than a grey ellipsoid because
            // everything around it stays reviewable; out here a species with no sprite would be a
            // single static frame gliding across a route, which is worse than an absence the
            // player never notices.
            _billboard.Bind(speciesId, displayHeight, null);
        }

        public void Play(CreatureAnimation animation)
        {
            if (_billboard == null) return;
            // Through the shared plan table rather than setting the sheet directly, so the
            // frozen-versus-looping decisions the battle already made hold in the overworld too.
            _billboard.ApplyPlan(CreatureAnimationPlans.For(animation));
        }

        public void SetVisible(bool visible)
        {
            if (_billboard != null) _billboard.SetVisible(visible);
        }

        public void Dispose()
        {
            if (_host != null) Object.Destroy(_host);
        }
    }

    /// <summary>
    /// Registers the billboard art path for roamers.
    ///
    /// Claims the contract only when nothing else has, matching
    /// <c>DexDisplayHeights.RegisterIfNothingElseHas</c>: a project that later grows a rigged
    /// 3D overworld creature supersedes this by registering first, and this file needs no change.
    /// </summary>
    public sealed class RoamerBillboardArtFactory : IOverworldCreatureArtFactory
    {
        public static void RegisterIfNothingElseHas()
        {
            if (ServiceHub.Has<IOverworldCreatureArtFactory>()) return;
            ServiceHub.Register<IOverworldCreatureArtFactory>(new RoamerBillboardArtFactory());
        }

        public IOverworldCreatureArt Create(Transform parent) => new RoamerBillboardArt(parent);
    }
}
