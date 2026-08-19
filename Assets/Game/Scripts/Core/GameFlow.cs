using System;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Core
{
    /// <summary>
    /// Top-level modes. Transitions between them are always driven through
    /// <see cref="IGameFlow"/> so the cinematics layer can own the blend and no system
    /// ever hard-cuts the camera.
    /// </summary>
    public enum GameMode
    {
        Boot = 0,
        Exploring = 1,
        EncounterIntro = 2,
        Battle = 3,
        BattleOutro = 4,
        Menu = 5,
        Dialogue = 6,
        Cutscene = 7,
    }

    /// <summary>Everything the battle scene needs to stage an encounter.</summary>
    public sealed class EncounterRequest
    {
        public BattleKind Kind = BattleKind.Wild;
        public int WildSpeciesId;
        public int WildLevel = 5;
        public string TrainerId;
        public Weather Weather = Weather.Clear;
        public TimeOfDay TimeOfDay = TimeOfDay.Day;

        /// <summary>Where the encounter fired, so the battle stage can match the biome.</summary>
        public string BiomeId;
        public Vector3 WorldPosition;
        /// <summary>Facing of the player when the encounter triggered, for the camera swing.</summary>
        public Quaternion PlayerRotation = Quaternion.identity;

        /// <summary>Deterministic seed. Reusing it must reproduce the battle exactly.</summary>
        public int Seed;
    }

    public sealed class EncounterResult
    {
        public BattleOutcome Outcome;
        public CreatureInstance CapturedCreature;
        public int MoneyDelta;
        public List<int> SpeciesSeen = new List<int>();
    }

    /// <summary>
    /// Owns mode transitions. Callers await the returned handle rather than assuming the
    /// transition is instant — the cinematics layer inserts its blend inside it.
    /// </summary>
    public interface IGameFlow
    {
        GameMode Mode { get; }

        event Action<GameMode, GameMode> ModeChanged;

        /// <summary>Runs the full exploration to battle transition and blocks until the field is staged.</summary>
        void RequestEncounter(EncounterRequest request, Action<EncounterResult> onResolved);

        /// <summary>Plays the outro and returns the player to the overworld at their prior position.</summary>
        void ReturnToOverworld();

        void PushMode(GameMode mode);
        void PopMode();
    }

    /// <summary>
    /// Resolves the art for a species. The Blender worker fills the underlying catalogue;
    /// gameplay code only ever asks for a species id and receives a ready prefab.
    /// Returning null must be survivable — callers fall back to the placeholder rig.
    /// </summary>
    public interface ICreatureArtRegistry
    {
        /// <summary>Rigged, animated creature prefab for battle and overworld display.</summary>
        GameObject GetCreaturePrefab(int speciesId);

        /// <summary>UI portrait for menus and the Poké Lab scanner.</summary>
        Sprite GetPortrait(int speciesId);

        /// <summary>
        /// Approximate shoulder height in metres, used to place cameras and health bars
        /// so framing stays correct across wildly different creature sizes.
        /// </summary>
        float GetDisplayHeight(int speciesId);
    }

    /// <summary>
    /// Animation states every creature rig must expose. The Blender worker guarantees a
    /// clip for each; the cinematics worker drives them through this enum only.
    /// </summary>
    public enum CreatureAnimation
    {
        Idle = 0,
        IdleBattle = 1,
        Walk = 2,
        Run = 3,
        AttackPhysical = 4,
        AttackSpecial = 5,
        AttackStatus = 6,
        Hit = 7,
        Dodge = 8,
        Faint = 9,
        Celebrate = 10,
        SentOut = 11,
        Recalled = 12,
        Sleep = 13,
    }

    /// <summary>Implemented by the creature view in the scene; driven by the battle presenter.</summary>
    public interface ICreatureView
    {
        Transform Root { get; }
        /// <summary>Anchor for health bars and scanner tags, at the creature's crown.</summary>
        Transform HeadAnchor { get; }
        /// <summary>Anchor where incoming hit VFX should spawn, at the creature's centre of mass.</summary>
        Transform BodyAnchor { get; }
        /// <summary>Anchor projectiles and melee VFX originate from.</summary>
        Transform MuzzleAnchor { get; }

        void Bind(CreatureInstance creature);
        void Play(CreatureAnimation animation, float crossfade = 0.15f);
        /// <summary>Turn to face a world point over time — never snap.</summary>
        void FaceTowards(Vector3 worldPoint, float duration = 0.3f);
    }

    /// <summary>
    /// One creature's worth of overworld art, owned by whoever asked the factory for it.
    ///
    /// This exists because the sprite billboard lives in PokeLab.Cinematics and the roaming
    /// creature that needs one lives in PokeLab.Overworld, and Cinematics already references
    /// Overworld — so the reference the roamer would need is a cycle the compiler rejects.
    /// The alternative shapes were both worse: a second billboard written inside Overworld
    /// duplicates the mirror-is-a-UV-flip reasoning that took a lighting bug to arrive at,
    /// and a Boot-side component polling <c>RoamingCreature.AllLive</c> means art appears a
    /// frame after the creature does, which reads as a pop.
    ///
    /// Deliberately smaller than <see cref="ICreatureView"/>: a roamer has no head anchor to
    /// hang an HP bar off and no <see cref="CreatureInstance"/> to bind, because it is a
    /// species and a level until the moment a battle actually starts.
    /// </summary>
    public interface IOverworldCreatureArt
    {
        /// <summary>The transform the art hangs off, for parenting and for facing it.</summary>
        Transform Root { get; }

        /// <summary>Points the art at a species and sizes it in metres, feet at the origin.</summary>
        void Bind(int speciesId, float displayHeight);

        /// <summary>Requests an animation state. Unsupported states must degrade, never throw.</summary>
        void Play(CreatureAnimation animation);

        /// <summary>
        /// Ground speed the creature is actually covering, in metres per second, published
        /// every frame by whoever moves it. The art ties its gait to this — frames advance
        /// at a rate the travel earns — and 0 hands pacing back to the authored idle.
        /// Separate from <see cref="Play"/> because a state is discrete and speed is not:
        /// a fleeing Pidgey and an ambling one are both "Walk" to the state machine and
        /// visibly different animals.
        /// </summary>
        void SetMoveSpeed(float metersPerSecond);

        /// <summary>Hides without destroying, so a pooled roamer can be reused.</summary>
        void SetVisible(bool visible);

        /// <summary>Destroys the art. Called when the roamer that owns it is destroyed.</summary>
        void Dispose();
    }

    /// <summary>
    /// Builds <see cref="IOverworldCreatureArt"/>. Registered by the composition root, because
    /// it is the only assembly that can see both halves.
    ///
    /// Absence is survivable on purpose: with nothing registered a roamer still walks, still
    /// reacts and still starts a battle, it is simply invisible. That is a bad build rather
    /// than a broken one, and it keeps the overworld testable before any art exists.
    /// </summary>
    public interface IOverworldCreatureArtFactory
    {
        IOverworldCreatureArt Create(Transform parent);
    }

    /// <summary>Player progression, persisted between sessions.</summary>
    public interface IPlayerProfile
    {
        string TrainerName { get; }
        int Money { get; }
        IReadOnlyList<CreatureInstance> Party { get; }
        IReadOnlyList<CreatureInstance> Storage { get; }

        /// <summary>Species ids the player has encountered and caught, for the dex.</summary>
        IReadOnlyCollection<int> SeenSpecies { get; }
        IReadOnlyCollection<int> CaughtSpecies { get; }

        IReadOnlyDictionary<string, int> Inventory { get; }

        bool TryAddToParty(CreatureInstance creature);
        void AddItem(string itemId, int count);
        bool ConsumeItem(string itemId, int count = 1);
        void MarkSeen(int speciesId);
        void MarkCaught(int speciesId);
    }
}
