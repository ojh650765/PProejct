using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Cinematics
{
    /// <summary>
    /// Which of the four authored backdrops a battle is performed in front of.
    ///
    /// The values mirror <c>PokeLab.Overworld.ZoneKind</c> and this is still its own enum, for
    /// one reason: what arrives on an <see cref="EncounterRequest"/> is
    /// <see cref="EncounterRequest.BiomeId"/>, a string, not the zone kind — so something has to
    /// do the translation anyway, and a set of dressings is a presentation list that should be
    /// free to gain a member the world has no zone kind for.
    /// <see cref="BattleArena.ZoneFor"/> is the single place that translation happens.
    /// </summary>
    public enum BattleZone
    {
        Route = 0,
        Town = 1,
        Cave = 2,
        Lakeside = 3,
    }

    /// <summary>
    /// The battle level: everything <c>Battle.unity</c> contains, and the handle the transition
    /// director picks the scene up by once it is loaded.
    ///
    /// <b>Why the arena is a scene of its own.</b> It used to be staged in the live world at the
    /// spot the encounter fired, which made the backdrop the real place for free and cost one
    /// thing that could not be bought back: the battle camera has to move, and the world was not
    /// built to be moved through. Ground cover is submitted with <c>Graphics.DrawMeshInstanced</c>
    /// — no renderers, no colliders — so there is nothing for a deoccluder to push against, and
    /// in dense grass the camera framed through fronds. That is unsolvable from the camera's
    /// side. Here the camera is the only thing in the level and every piece of scenery was placed
    /// knowing where it would stand.
    ///
    /// <b>Why the level is loaded beside the overworld rather than instead of it.</b> The
    /// encounter round trip is owned by <c>GameFlowController</c>, which lives on <c>GameHosts</c>
    /// in the level scene along with the player's party and profile. A single-mode load destroys
    /// it mid-coroutine: the battle would resolve into a controller that no longer exists, taking
    /// the capture, the experience and the damage the party took with it, and the callback that
    /// releases the player would never fire. So <c>Battle.unity</c> is loaded additively and
    /// authored a long way from the map — see <see cref="BattleArenaSetup"/>'s offset — which is
    /// its own coordinate space in everything but name. The player is carried across to it, which
    /// is what makes it a place the battle happens rather than a camera trick.
    /// </summary>
    [DefaultExecutionOrder(-380)]
    [DisallowMultipleComponent]
    public sealed class BattleArena : MonoBehaviour
    {
        [Header("Arena")]
        [SerializeField] private BattleStage stage;
        [SerializeField] private BattleCameraRig rig;
        [SerializeField] private BattlePresenter presenter;

        [Tooltip("Where the player's body is put down while the battle runs. Behind the camera, " +
                 "not on the trainer mark — see PlayerStand.")]
        [SerializeField] private Transform playerStand;

        [Header("Backdrops")]
        [Tooltip("One dressing root per BattleZone, in enum order. Exactly one is ever active.")]
        [SerializeField] private GameObject[] dressings = new GameObject[4];

        /// <summary>
        /// The arena currently loaded, or null when no battle scene is up.
        ///
        /// A static rather than a service registration: <see cref="ServiceHub"/> holds plain
        /// references that outlive the scene they came from, so a battle scene that has been
        /// unloaded would go on answering as the live arena — and the next encounter would stage
        /// itself onto a destroyed transform.
        /// </summary>
        public static BattleArena Current { get; private set; }

        /// <summary>The arena layout. Never null once this component has woken.</summary>
        public BattleStage Stage { get { EnsureWired(); return stage; } }

        /// <summary>The shot set framing this arena.</summary>
        public BattleCameraRig Rig { get { EnsureWired(); return rig; } }

        /// <summary>The choreographer performing into this arena.</summary>
        public BattlePresenter Presenter { get { EnsureWired(); return presenter; } }

        /// <summary>
        /// Where the player's body is parked for the duration of the battle.
        ///
        /// Deliberately <b>not</b> the trainer mark, even though that is where the player appears
        /// to be standing. The person who throws the ball is a <see cref="TrainerView"/> that is
        /// drawn only around the throw, and the real player rig would be a second copy of the same
        /// sprite standing on the same spot for the whole fight — the exact thing the trainer beat
        /// exists to avoid.
        ///
        /// So the body goes behind the camera. It still travels: everything that asks where the
        /// player is during a battle — the streamer, the zone director, the ambience — sees the
        /// arena rather than a town they are no longer in, and the return is a real restore.
        /// </summary>
        public Transform PlayerStand => playerStand != null ? playerStand : Stage.TrainerMarkOf(BattleSide.Player);

        private void Awake()
        {
            EnsureWired();
            DexDisplayHeights.RegisterIfNothingElseHas();
        }

        private void OnEnable() => Current = this;

        private void OnDisable()
        {
            // Only if we are still the live one. Two arenas never coexist, but an unload racing a
            // load would otherwise leave Current null with a perfectly good arena standing.
            if (Current == this) Current = null;
        }

        private void EnsureWired()
        {
            if (stage == null) stage = GetComponentInChildren<BattleStage>(true);
            if (rig == null) rig = GetComponentInChildren<BattleCameraRig>(true);
            if (presenter == null) presenter = GetComponentInChildren<BattlePresenter>(true);
        }

        /// <summary>
        /// Switches the backdrop to the zone the encounter fired in, and makes the field discs
        /// out of that zone's ground.
        ///
        /// Every dressing is switched off first rather than only the previous one: a scene saved
        /// with two of them enabled — which is what an author leaves behind after looking at them
        /// — would otherwise put a cave inside a lake and read as corrupt geometry.
        /// </summary>
        public void Dress(BattleZone zone)
        {
            EnsureWired();

            for (int i = 0; i < dressings.Length; i++)
            {
                if (dressings[i] == null) continue;
                dressings[i].SetActive(i == (int)zone);
            }

            if (dressings.Length <= (int)zone || dressings[(int)zone] == null)
            {
                Debug.LogWarning($"[Arena] No {zone} dressing in the battle scene, so the fight " +
                                 "happens in an empty room. Run Build Battle Arena Scene.", this);
            }

            Stage.SetGroundSurface(SurfaceFor(zone));
        }

        // --- Zone resolution ------------------------------------------------------------------

        /// <summary>
        /// Which backdrop a biome id asks for.
        ///
        /// Matched on the id rather than on <c>ZoneKind</c> because the id is what
        /// <see cref="EncounterRequest"/> already carries across the assembly boundary. Unknown
        /// ids fall through to Route, which is wrong quietly rather than empty and loud — and it
        /// is the common case today, because no scene authors a <c>WorldZone</c> yet and the
        /// encounter director stamps every request "unknown".
        /// </summary>
        public static BattleZone ZoneFor(string biomeId)
        {
            if (string.IsNullOrEmpty(biomeId)) return BattleZone.Route;
            string id = biomeId.ToLowerInvariant();
            if (id.Contains("cave") || id.Contains("interior") || id.Contains("grotto")) return BattleZone.Cave;
            if (id.Contains("lake") || id.Contains("shore") || id.Contains("beach") || id.Contains("river"))
                return BattleZone.Lakeside;
            if (id.Contains("town") || id.Contains("village") || id.Contains("plaza")) return BattleZone.Town;
            return BattleZone.Route;
        }

        /// <summary>Which terrain layer a zone's field discs are made of.</summary>
        public static BattleFieldSurface SurfaceFor(BattleZone zone)
        {
            switch (zone)
            {
                case BattleZone.Town: return BattleFieldSurface.Dirt;
                case BattleZone.Cave: return BattleFieldSurface.Rock;
                case BattleZone.Lakeside: return BattleFieldSurface.Sand;
                default: return BattleFieldSurface.Grass;
            }
        }

        /// <summary>
        /// The biome id the arena broadcasts while it is up.
        ///
        /// This is the whole of the arena's lighting. <c>LightingDirector</c> owns the sun, the
        /// ambient gradient, the fog and the sky globally and rewrites all four every frame, so a
        /// second directional light in the battle scene would not be a mood — it would be a
        /// second uncontrolled sun on everything, including the map the arena is parked away
        /// from. What the director does listen to is <c>GameEvents.BiomeEntered</c>, and it reads
        /// "cave" out of the id to drive the interior grade that puts the sun out. So a cave
        /// battle goes dark by saying it is in a cave.
        /// </summary>
        public static string GradeIdFor(BattleZone zone)
        {
            switch (zone)
            {
                case BattleZone.Cave: return "battle_cave_interior";
                case BattleZone.Town: return "battle_town";
                case BattleZone.Lakeside: return "battle_lakeside";
                default: return "battle_route";
            }
        }
    }

    /// <summary>
    /// Display heights straight out of the dex, standing in for the art catalogue nobody has
    /// built yet.
    ///
    /// No <see cref="ICreatureArtRegistry"/> is registered anywhere in the project —
    /// <c>CreatureArtCatalogBuilder</c> exists but its manifest, <c>Assets/Game/Art/Creatures</c>,
    /// does not — so <c>CreatureView.ResolveDisplayHeight</c> returned its 0.8 m fallback for
    /// every species. That is not a cosmetic gap: the arena separation solve, the disc radius and
    /// the lens all take display height as their only input, so a Caterpie and a Gyarados were
    /// framed identically and the whole size-agnostic camera had nothing to be agnostic about.
    ///
    /// <see cref="SpeciesData.Height"/> is decimetres from PokeAPI and is loaded at boot for
    /// every species, which makes it the one height source that is already correct and already
    /// present. Only the height is answered: the prefab and the portrait stay null, so
    /// <c>CreatureView</c> falls through to the sprite manifest exactly as it did before.
    ///
    /// Registered only when nothing else has claimed the contract, so the real catalogue
    /// supersedes this the moment it is built and this file needs no change when it is.
    /// </summary>
    public sealed class DexDisplayHeights : ICreatureArtRegistry
    {
        /// <summary>Metres. What a species with no dex row is drawn at.</summary>
        private const float Fallback = 0.8f;

        public static void RegisterIfNothingElseHas()
        {
            if (ServiceHub.Has<ICreatureArtRegistry>()) return;
            ServiceHub.Register<ICreatureArtRegistry>(new DexDisplayHeights());
            Debug.Log("[Arena] No creature art registry was registered, so display heights are " +
                      "being answered from the dex. Build the art catalogue to replace this.");
        }

        public GameObject GetCreaturePrefab(int speciesId) => null;

        public Sprite GetPortrait(int speciesId) => null;

        public float GetDisplayHeight(int speciesId)
        {
            if (!ServiceHub.TryGet<ISpeciesRegistry>(out var species) || species == null) return Fallback;
            if (!species.TryGet(speciesId, out var row) || row == null) return Fallback;

            // Decimetres to metres, and clamped to the same band the arena's separation solve
            // uses. A dex row with a zero height would otherwise size a disc to nothing and the
            // creature would stand on a point.
            float metres = row.Height * 0.1f;
            return metres > 0.05f ? Mathf.Clamp(metres, 0.2f, 6.5f) : Fallback;
        }
    }
}
