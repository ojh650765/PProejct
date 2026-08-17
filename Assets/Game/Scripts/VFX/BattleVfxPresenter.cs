using System.Collections;
using System.Collections.Generic;
using PokeLab.Core;
using UnityEngine;

namespace PokeLab.Vfx
{
    /// <summary>
    /// Plays the right effect for every battle event.
    ///
    /// Presentation is advisory by contract: state has already changed inside the
    /// engine by the time an event arrives here, so every path in this class is
    /// written to degrade rather than to throw. A missing creature view, an unknown
    /// VfxKey, an exhausted pool and a battle that starts before the catalogue is
    /// assigned all resolve to "play something reasonable, or play nothing", never
    /// to an exception that would take the battle down with it.
    ///
    /// Register it on ServiceHub as IBattleEventListener from the boot scene, or let
    /// it register itself: it does so in Awake, so the battle presenter can find it
    /// without either assembly referencing the other.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("PokeLab/Battle VFX Presenter")]
    public sealed class BattleVfxPresenter : MonoBehaviour, IBattleEventListener
    {
        [Header("Catalogue")]
        [Tooltip("Leave empty to use the built-in procedural library.")]
        [SerializeField] private VfxCatalogue catalogue;

        [Header("Fallback anchors")]
        [Tooltip("Used when no ICreatureView is bound for the player's side.")]
        [SerializeField] private Transform playerFallbackAnchor;
        [SerializeField] private Transform opponentFallbackAnchor;

        [Header("Tuning")]
        [Tooltip("Seconds a projectile takes to cross the field at the reference distance.")]
        [SerializeField] private float projectileTravelSeconds = 0.42f;

        [Tooltip("Height of the projectile's arc as a fraction of the distance travelled.")]
        [Range(0f, 0.6f)]
        [SerializeField] private float projectileArc = 0.16f;

        [Tooltip("Seconds the creature's hit flash lasts.")]
        [SerializeField] private float hitFlashSeconds = 0.12f;

        [Tooltip("Seconds the faint dissolve takes.")]
        [SerializeField] private float faintDissolveSeconds = 1.4f;

        [Tooltip("Pooled instances built per frame while warming up after boot. " +
                 "Higher warms sooner and costs more per frame.")]
        [Range(1, 16)]
        [SerializeField] private int prewarmInstancesPerFrame = 3;

        [Header("Debug")]
        [SerializeField] private bool logUnresolvedKeys;

        // --- Runtime ---------------------------------------------------------
        private VfxPool pool;
        private bool ownsCatalogue;
        private readonly List<string> warmQueue = new List<string>();

        private readonly Dictionary<BattleSide, ICreatureView> views =
            new Dictionary<BattleSide, ICreatureView>();

        // One persistent status aura per side. Replacing a status stops the old one.
        private readonly Dictionary<BattleSide, VfxHandle> statusAuras =
            new Dictionary<BattleSide, VfxHandle>();

        private readonly Dictionary<BattleSide, Coroutine> flashRoutines =
            new Dictionary<BattleSide, Coroutine>();

        private static readonly int FlashAmountId = Shader.PropertyToID("_FlashAmount");
        private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
        private static readonly int DissolveAmountId = Shader.PropertyToID("_DissolveAmount");

        private MaterialPropertyBlock propertyBlock;

        /// <summary>
        /// True while a cinematic battle presenter is performing the event stream.
        ///
        /// This class was written to be the whole visual response to a battle, with its own
        /// guessed timings — an impact 0.18s after the cast, capture flashes on a 0.55s
        /// metronome. When the choreography is live those moments arrive frame-accurately
        /// through <see cref="ICinematicVfxHook"/> instead (the contact frame, the ball's
        /// actual open, each real shake), and playing both versions doubles every effect a
        /// half-beat apart. So the AV composition host sets this when it wires the presenter,
        /// and the handlers below split the work: everything with a frame-accurate twin
        /// stands down, everything only this class can do — the hit flash on the creature's
        /// own material, the persistent status auras, the parented heal and level-up bursts —
        /// keeps running off the event stream.
        /// </summary>
        public bool ChoreographyOwnsBeats { get; set; }

        // ---------------------------------------------------------------------
        // Lifecycle
        // ---------------------------------------------------------------------
        private void Awake()
        {
            propertyBlock = new MaterialPropertyBlock();

            if (catalogue == null)
            {
                catalogue = VfxCatalogue.CreateRuntimeDefault();
                ownsCatalogue = true;
            }

            var poolRoot = new GameObject("PL_VfxPool").transform;
            poolRoot.SetParent(transform, false);
            pool = new VfxPool(poolRoot);

            WarmCatalogue();

            // Register under the Core interface so any worker can find us without a
            // reference to this assembly. VfxApi is the convenience path for anyone
            // who is willing to add the reference.
            ServiceHub.Register<IBattleEventListener>(this);
            VfxApi.Bind(this);
        }

        private void Start() => StartCoroutine(PrewarmOverFrames());

        private void OnDestroy()
        {
            VfxApi.Unbind(this);
            pool?.Dispose();
            if (ownsCatalogue && catalogue != null) Destroy(catalogue);

            // The generated textures and materials are HideAndDontSave, which means
            // nothing else will ever collect them. Leaving them behind leaks a few
            // megabytes per Play Mode session until the editor is restarted.
            VfxMaterialLibrary.Clear();
            ProceduralVfxTextures.Clear();
        }

        private void Update() => pool?.Tick();

        /// <summary>
        /// Registers every key the catalogue can serve. Registration itself is a
        /// dictionary insert, so this is free; the instances are built afterwards by
        /// PrewarmOverFrames.
        /// </summary>
        private void WarmCatalogue()
        {
            warmQueue.Clear();
            foreach (string key in catalogue.RecipeKeys)
            {
                if (!catalogue.TryGetRecipe(key, out var recipe)) continue;
                catalogue.TryGetPrefab(key, out var prefab);
                pool.Register(key, recipe, prefab);
                warmQueue.Add(key);
            }
        }

        /// <summary>
        /// Builds the pooled instances a few at a time.
        ///
        /// The catalogue serves around ninety effects and building all of them at
        /// once would mean several hundred ParticleSystems in a single frame, which
        /// is a visible stall on load. Building them on first use instead would move
        /// the stall into the first attack of the battle, which is worse. Spreading
        /// the work over the frames after boot costs nothing anyone can see and still
        /// has every effect warm long before a battle starts.
        /// </summary>
        private IEnumerator PrewarmOverFrames()
        {
            for (int i = 0; i < warmQueue.Count; i++)
            {
                string key = warmQueue[i];
                int budget = prewarmInstancesPerFrame;
                while (budget > 0 && !pool.IsWarm(key))
                {
                    int created = pool.Prewarm(key, budget);
                    if (created <= 0) break;
                    budget -= created;
                }
                if (budget <= 0) yield return null;
            }
        }

        // ---------------------------------------------------------------------
        // Binding
        // ---------------------------------------------------------------------

        /// <summary>
        /// Tells the presenter which view belongs to which side. The battle staging
        /// code calls this when it spawns the creatures.
        /// </summary>
        public void BindView(BattleSide side, ICreatureView view)
        {
            if (view == null) views.Remove(side);
            else views[side] = view;
        }

        public void ClearViews()
        {
            foreach (var kv in statusAuras)
                if (kv.Value != null) pool.Stop(kv.Value, immediate: true);
            statusAuras.Clear();
            views.Clear();
        }

        private ICreatureView ResolveView(BattleSide side)
        {
            if (views.TryGetValue(side, out var bound) && bound != null) return bound;

            // A scene that has registered exactly one view is the common case during
            // partial integration; using it for both sides is wrong but visible,
            // which beats silently playing nothing.
            if (ServiceHub.TryGet<ICreatureView>(out var single) && single != null) return single;

            return null;
        }

        private Vector3 MuzzlePosition(BattleSide side)
        {
            var view = ResolveView(side);
            if (view?.MuzzleAnchor != null) return view.MuzzleAnchor.position;
            if (view?.Root != null) return view.Root.position + Vector3.up;
            return FallbackPosition(side) + Vector3.up * 1.1f;
        }

        private Vector3 BodyPosition(BattleSide side)
        {
            var view = ResolveView(side);
            if (view?.BodyAnchor != null) return view.BodyAnchor.position;
            if (view?.Root != null) return view.Root.position + Vector3.up * 0.8f;
            return FallbackPosition(side) + Vector3.up * 0.8f;
        }

        private Vector3 FallbackPosition(BattleSide side)
        {
            Transform t = side == BattleSide.Player ? playerFallbackAnchor : opponentFallbackAnchor;
            if (t != null) return t.position;
            // Last resort: two points either side of this object, so an effect is at
            // least visible and clearly in the wrong place rather than invisible.
            return transform.position + (side == BattleSide.Player ? Vector3.back : Vector3.forward) * 3f;
        }

        // ---------------------------------------------------------------------
        // Event entry point
        // ---------------------------------------------------------------------
        public void OnBattleEvent(BattleEvent evt)
        {
            if (evt == null) return;

            switch (evt)
            {
                case MoveExecutedEvent move: OnMoveExecuted(move); break;
                case DamageDealtEvent hit: OnDamageDealt(hit); break;
                case HealedEvent heal: OnHealed(heal); break;
                case StatusChangedEvent status: OnStatusChanged(status); break;
                case StatStageChangedEvent stat: OnStatStageChanged(stat); break;
                case CreatureFaintedEvent faint: OnFainted(faint); break;
                case CaptureAttemptEvent capture: OnCaptureAttempt(capture); break;
                case ExperienceGainedEvent xp: OnExperienceGained(xp); break;
                case MoveMissedEvent miss: OnMoveMissed(miss); break;
                case AbilityTriggeredEvent ability: OnAbilityTriggered(ability); break;
                case CreatureSentOutEvent sent: OnSentOut(sent); break;
                case CreatureWithdrawnEvent withdrawn: OnWithdrawn(withdrawn); break;
                case BattleEndedEvent ended: OnBattleEnded(ended); break;
            }
        }

        // ---------------------------------------------------------------------
        // Moves
        // ---------------------------------------------------------------------
        private void OnMoveExecuted(MoveExecutedEvent move)
        {
            Vector3 muzzle = MuzzlePosition(move.Attacker);
            Vector3 target = BodyPosition(move.Target);
            Vector3 toTarget = target - muzzle;
            Quaternion facing = toTarget.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(toTarget.normalized, Vector3.up)
                : Quaternion.identity;

            // Cast is played for every move, including melee: it is what tells the
            // player which type is coming before anything else happens.
            PlayResolved(move.VfxKey, move.MoveType, VfxPhase.Cast, muzzle, facing);

            // Choreographed battles own travel and impact: the presenter flies a real
            // ProjectileActor between live anchors and raises the impact on the actual
            // contact frame through the hook. Only the cast survives from here — the
            // event is dispatched as the wind-up beat opens, which is exactly when the
            // type telegraph should glow, and the choreography has no cast of its own.
            if (ChoreographyOwnsBeats) return;

            if (move.IsProjectile)
            {
                StartCoroutine(FlyProjectile(move, muzzle, target, facing));
            }
            else if (move.MakesContact)
            {
                // A contact move has no travel phase, so the impact lands on the
                // attacker's timing rather than waiting for a projectile.
                StartCoroutine(DelayedImpact(move, target, facing, 0.18f));
            }
            else
            {
                StartCoroutine(DelayedImpact(move, target, facing, 0.25f));
            }
        }

        private IEnumerator FlyProjectile(MoveExecutedEvent move, Vector3 from, Vector3 to,
                                          Quaternion facing)
        {
            var handle = PlayResolved(move.VfxKey, move.MoveType, VfxPhase.Travel, from, facing);

            float distance = Vector3.Distance(from, to);
            // Scale the flight time with distance so a close-quarters shot does not
            // crawl and a long one does not teleport, but clamp it: the event stream's
            // SuggestedDuration is a floor, and a projectile that outlives its beat
            // desynchronises the whole exchange.
            float duration = Mathf.Clamp(projectileTravelSeconds * (distance / 6f), 0.16f, 0.9f);
            float arcHeight = distance * projectileArc;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                Vector3 position = Vector3.Lerp(from, to, t);
                // A parabola peaking at the midpoint. Sin gives it for free and is
                // cheaper to read than the quadratic.
                position.y += Mathf.Sin(t * Mathf.PI) * arcHeight;

                if (handle != null && handle.IsAlive)
                {
                    Vector3 previous = handle.Transform.position;
                    handle.SetPosition(position);
                    Vector3 velocity = position - previous;
                    if (velocity.sqrMagnitude > 1e-6f)
                        handle.SetRotation(Quaternion.LookRotation(velocity.normalized, Vector3.up));
                }

                yield return null;
            }

            if (handle != null) pool.Stop(handle);
            PlayResolved(move.VfxKey, move.MoveType, VfxPhase.Impact, to, facing);
        }

        private IEnumerator DelayedImpact(MoveExecutedEvent move, Vector3 at, Quaternion facing,
                                          float delay)
        {
            yield return new WaitForSeconds(delay);
            PlayResolved(move.VfxKey, move.MoveType, VfxPhase.Impact, at, facing);
        }

        private void OnMoveMissed(MoveMissedEvent miss)
        {
            // Choreographed: the miss beat plays its own immunity flare on the staged
            // impact point (and a dodge streak for a true miss), so the shield here would
            // be the same statement twice at two slightly different heights.
            if (ChoreographyOwnsBeats) return;

            Vector3 at = BodyPosition(miss.Target);
            if (miss.WasImmune)
            {
                // An immunity is a block, not a whiff: show the shield.
                Play(BattleStateVfxLibrary.KeyImmune, at, Quaternion.identity);
            }
            // A pure accuracy miss gets no VFX on purpose. The absence is the read.
        }

        // ---------------------------------------------------------------------
        // Damage
        // ---------------------------------------------------------------------
        private void OnDamageDealt(DamageDealtEvent hit)
        {
            Vector3 at = BodyPosition(hit.Target);

            // Choreographed: PlayDamage fires impact_critical / impact_super_effective on
            // the rig's own impact point (this class serves them via the hook), so only the
            // material flash — which nothing else can do — remains this handler's job.
            if (ChoreographyOwnsBeats)
            {
                if (hit.Effectiveness != Effectiveness.Immune) FlashCreature(hit.Target, hit.Critical);
                return;
            }

            switch (hit.Effectiveness)
            {
                case Effectiveness.SuperEffective:
                    Play(BattleStateVfxLibrary.KeySuperEffective, at, Quaternion.identity);
                    break;
                case Effectiveness.NotVeryEffective:
                    Play(BattleStateVfxLibrary.KeyNotVeryEffective, at, Quaternion.identity);
                    break;
                case Effectiveness.Immune:
                    Play(BattleStateVfxLibrary.KeyImmune, at, Quaternion.identity);
                    return;   // no flash, no crit: nothing landed
            }

            // The crit flash layers on top of the effectiveness variant rather than
            // replacing it, so a super-effective critical reads as both.
            if (hit.Critical)
                Play(BattleStateVfxLibrary.KeyCritical, at, Quaternion.identity, scale: 1.15f);

            FlashCreature(hit.Target, hit.Critical);
        }

        /// <summary>
        /// Drives _FlashAmount on the target's creature material. The creature shader
        /// exposes it precisely so a hit can be read on the creature itself and not
        /// only in the particles in front of it.
        /// </summary>
        private void FlashCreature(BattleSide side, bool critical)
        {
            var view = ResolveView(side);
            if (view?.Root == null) return;

            if (flashRoutines.TryGetValue(side, out var running) && running != null)
                StopCoroutine(running);

            flashRoutines[side] = StartCoroutine(FlashRoutine(view.Root, critical));
        }

        private IEnumerator FlashRoutine(Transform root, bool critical)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            Color colour = critical ? new Color(1.6f, 1.5f, 1.3f) : new Color(1f, 0.85f, 0.85f);
            float duration = hitFlashSeconds * (critical ? 1.7f : 1f);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                // Hold then release: an instant on and a quick decay reads as a hit,
                // a symmetric fade in and out reads as a pulse.
                float t = Mathf.Clamp01(elapsed / duration);
                float amount = 1f - t * t;

                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    r.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetFloat(FlashAmountId, amount);
                    propertyBlock.SetColor(FlashColorId, colour);
                    r.SetPropertyBlock(propertyBlock);
                }
                yield return null;
            }

            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(propertyBlock);
                propertyBlock.SetFloat(FlashAmountId, 0f);
                r.SetPropertyBlock(propertyBlock);
            }
        }

        // ---------------------------------------------------------------------
        // State
        // ---------------------------------------------------------------------
        private void OnHealed(HealedEvent heal)
        {
            var view = ResolveView(heal.Target);
            Vector3 at = view?.Root != null ? view.Root.position : FallbackPosition(heal.Target);
            Play(BattleStateVfxLibrary.KeyHeal, at, Quaternion.identity,
                 parent: view?.Root);
        }

        private void OnStatusChanged(StatusChangedEvent status)
        {
            // Whatever was there stops, whatever is new starts. Fainting is handled by
            // CreatureFaintedEvent, so it does not get an aura.
            if (statusAuras.TryGetValue(status.Target, out var existing) && existing != null)
            {
                pool.Stop(existing);
                statusAuras.Remove(status.Target);
            }

            if (status.Current == StatusCondition.None || status.Current == StatusCondition.Fainted)
                return;

            var view = ResolveView(status.Target);
            Vector3 at = view?.Root != null ? view.Root.position : FallbackPosition(status.Target);

            // Parented so the aura follows the creature as it moves and animates.
            var handle = Play(BattleStateVfxLibrary.StatusKey(status.Current), at,
                              Quaternion.identity, parent: view?.Root);
            if (handle != null) statusAuras[status.Target] = handle;
        }

        private void OnStatStageChanged(StatStageChangedEvent stat)
        {
            if (stat.Delta == 0) return;

            // Choreographed: PlayStatStage fires stat_up / stat_down through the hook,
            // scaled by the swell/flinch gesture it plays alongside. One sparkle per change.
            if (ChoreographyOwnsBeats) return;

            var view = ResolveView(stat.Target);
            Vector3 at = view?.Root != null ? view.Root.position : FallbackPosition(stat.Target);
            string key = stat.Delta > 0
                ? BattleStateVfxLibrary.KeyStatUp
                : BattleStateVfxLibrary.KeyStatDown;

            // A two-stage change is visibly bigger than a one-stage change.
            float scale = 1f + Mathf.Min(Mathf.Abs(stat.Delta) - 1, 2) * 0.22f;
            Play(key, at, Quaternion.identity, scale, view?.Root);
        }

        private void OnAbilityTriggered(AbilityTriggeredEvent ability)
        {
            // Choreographed: PlayAbility fires ability_flare through the hook on the same
            // beat, so the shield here is the doubled version of that flare.
            if (ChoreographyOwnsBeats) return;

            var view = ResolveView(ability.Side);
            Vector3 at = view?.BodyAnchor != null ? view.BodyAnchor.position : BodyPosition(ability.Side);
            Play(BattleStateVfxLibrary.KeyShield, at, Quaternion.identity, parent: view?.Root);
        }

        private void OnFainted(CreatureFaintedEvent faint)
        {
            if (statusAuras.TryGetValue(faint.Side, out var aura) && aura != null)
            {
                pool.Stop(aura, immediate: true);
                statusAuras.Remove(faint.Side);
            }

            // Choreographed: the faint is a staged set piece — collapse animation, dust on
            // the ground frame (faint_collapse via the hook), then a recall beam. A shader
            // dissolve running under a creature that is being physically recalled reads as
            // two different deaths happening to one body.
            if (ChoreographyOwnsBeats) return;

            var view = ResolveView(faint.Side);
            Vector3 at = view?.Root != null ? view.Root.position : FallbackPosition(faint.Side);
            Play(BattleStateVfxLibrary.KeyFaint, at, Quaternion.identity);

            if (view?.Root != null) StartCoroutine(DissolveRoutine(view.Root, faintDissolveSeconds, 1f));
        }

        private void OnSentOut(CreatureSentOutEvent sent)
        {
            // Choreographed: the entrance is the ball flight, the burst (ball_burst via the
            // hook) and a real fall with landing dust. The materialise dissolve belongs to
            // the un-staged version only.
            if (ChoreographyOwnsBeats) return;

            var view = ResolveView(sent.Side);
            if (view?.Root == null) return;

            // Materialise: run the dissolve backwards.
            StartCoroutine(DissolveRoutine(view.Root, 0.7f, 0f, startAt: 1f));
            Play(BattleStateVfxLibrary.KeyCaptureFlash, view.Root.position + Vector3.up * 0.6f,
                 Quaternion.identity);
        }

        private void OnWithdrawn(CreatureWithdrawnEvent withdrawn)
        {
            if (statusAuras.TryGetValue(withdrawn.Side, out var aura) && aura != null)
            {
                pool.Stop(aura, immediate: true);
                statusAuras.Remove(withdrawn.Side);
            }

            // Choreographed: BallActor's recall plays its own beam (ball_recall via the
            // hook) aimed at the creature it is actually absorbing.
            if (ChoreographyOwnsBeats) return;

            var view = ResolveView(withdrawn.Side);
            if (view?.Root == null) return;
            StartCoroutine(DissolveRoutine(view.Root, 0.5f, 1f));
            Play(BattleStateVfxLibrary.KeyCaptureBeam, view.Root.position, Quaternion.identity);
        }

        private IEnumerator DissolveRoutine(Transform root, float duration, float endValue,
                                            float startAt = 0f)
        {
            var renderers = root.GetComponentsInChildren<Renderer>();
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float amount = Mathf.Lerp(startAt, endValue, Mathf.Clamp01(elapsed / duration));
                for (int i = 0; i < renderers.Length; i++)
                {
                    var r = renderers[i];
                    if (r == null) continue;
                    r.GetPropertyBlock(propertyBlock);
                    propertyBlock.SetFloat(DissolveAmountId, amount);
                    r.SetPropertyBlock(propertyBlock);
                }
                yield return null;
            }
        }

        private void OnCaptureAttempt(CaptureAttemptEvent capture)
        {
            // Choreographed: the capture is BallActor's set piece end to end — absorb beam,
            // real shakes on the ball's own wobble frames, click flash or break-out burst,
            // all arriving through the hook. This metronome version is for bare scenes.
            if (ChoreographyOwnsBeats) return;

            Vector3 at = BodyPosition(BattleSide.Opponent);
            Play(BattleStateVfxLibrary.KeyCaptureBeam, at, Quaternion.identity);
            StartCoroutine(CaptureShakes(capture, at));
        }

        private IEnumerator CaptureShakes(CaptureAttemptEvent capture, Vector3 at)
        {
            // One flash per shake, evenly spread through the event's suggested beat,
            // then the success burst. Four shakes plus Succeeded is a catch.
            yield return new WaitForSeconds(0.55f);

            int shakes = Mathf.Clamp(capture.Shakes, 0, 4);
            for (int i = 0; i < shakes; i++)
            {
                Play(BattleStateVfxLibrary.KeyCaptureFlash, at, Quaternion.identity);
                yield return new WaitForSeconds(0.55f);
            }

            if (capture.Succeeded)
                Play(BattleStateVfxLibrary.KeyCaptureSuccess, at, Quaternion.identity, scale: 1.3f);
        }

        private void OnExperienceGained(ExperienceGainedEvent xp)
        {
            if (!xp.LeveledUp) return;
            var view = ResolveView(BattleSide.Player);
            Vector3 at = view?.Root != null ? view.Root.position : FallbackPosition(BattleSide.Player);
            Play(BattleStateVfxLibrary.KeyLevelUp, at, Quaternion.identity, parent: view?.Root);
        }

        private void OnBattleEnded(BattleEndedEvent ended)
        {
            foreach (var kv in statusAuras)
                if (kv.Value != null) pool.Stop(kv.Value);
            statusAuras.Clear();
        }

        // ---------------------------------------------------------------------
        // Playback
        // ---------------------------------------------------------------------

        /// <summary>Plays a catalogue key directly. The cinematics worker's entry point.</summary>
        public VfxHandle Play(string key, Vector3 position, Quaternion rotation,
                              float scale = 1f, Transform parent = null)
        {
            if (string.IsNullOrEmpty(key) || pool == null) return null;

            if (!pool.IsRegistered(key))
            {
                // A key that was not in the catalogue at warm time can still be served
                // if the catalogue knows it now; registering here costs one hitch and
                // never happens twice.
                //
                // Either source is enough. A hand-authored prefab with no matching
                // built-in recipe is a complete answer — it is the whole point of the
                // override list, and PlayResolved has always accepted one — but this
                // asked only for a recipe, so an integrator who overrode a key the
                // libraries do not define got nothing at all from the direct entry point.
                bool hasRecipe = catalogue.TryGetRecipe(key, out var recipe);
                bool hasPrefab = catalogue.TryGetPrefab(key, out var prefab);
                if (!hasRecipe && !hasPrefab)
                {
                    if (logUnresolvedKeys) Debug.LogWarning($"[BattleVfxPresenter] Unknown VFX key '{key}'.");
                    return null;
                }
                pool.Register(key, recipe, prefab);
            }

            return pool.Play(key, position, rotation, scale, parent);
        }

        /// <summary>
        /// Plays a move phase, resolving the bespoke key first and the element type
        /// second. This is the fallback chain the brief asks for and the reason an
        /// incomplete move table still produces a correct-looking battle.
        /// </summary>
        public VfxHandle PlayResolved(string vfxKey, ElementType type, VfxPhase phase,
                                      Vector3 position, Quaternion rotation, float scale = 1f)
        {
            if (catalogue == null) return null;

            if (!catalogue.Resolve(vfxKey, type, phase, out string poolKey, out var prefab, out var recipe))
            {
                if (logUnresolvedKeys)
                    Debug.LogWarning($"[BattleVfxPresenter] Could not resolve '{vfxKey}' ({type}, {phase}).");
                return null;
            }

            // Registered under the resolved identity, not the requested key, so two
            // moves that both fall back to Fire share one pool bucket.
            if (!pool.IsRegistered(poolKey)) pool.Register(poolKey, recipe, prefab);

            return pool.Play(poolKey, position, rotation, scale);
        }

        /// <summary>Stops a persistent effect obtained from Play.</summary>
        public void StopEffect(VfxHandle handle, bool immediate = false) => pool?.Stop(handle, immediate);

        /// <summary>
        /// Builds a stand-alone, destroy-safe instance of a catalogue key.
        ///
        /// This exists for <see cref="PokeLab.Core.ICinematicVfxHook.AttachVfx"/>: the
        /// cinematics layer parents the returned object to a projectile or a ball and later
        /// calls <c>Destroy</c> on it. Handing out a pooled instance there would let the
        /// caller destroy an object the pool still owns and believes it can reuse, which
        /// corrupts the pool for the rest of the session — so attached effects are built
        /// fresh, outside the pool, and the pool never hears about them.
        ///
        /// Falls back through the same chain as move resolution (bare key, then a travel
        /// phase, since attachment is almost always a projectile) so an engine VfxKey with
        /// no exact recipe still yields something visible.
        /// </summary>
        public GameObject BuildDetached(string key, Transform parent)
        {
            if (string.IsNullOrEmpty(key) || catalogue == null) return null;

            if (catalogue.TryGetPrefab(key, out var prefab) && prefab != null)
                return Instantiate(prefab, parent, false);
            if (catalogue.TryGetRecipe(key, out var recipe) && recipe != null)
                return ProceduralVfxFactory.Build(recipe, parent);

            if (catalogue.Resolve(key, ElementType.Normal, VfxPhase.Travel,
                                  out _, out var resolvedPrefab, out var resolvedRecipe))
            {
                if (resolvedPrefab != null) return Instantiate(resolvedPrefab, parent, false);
                if (resolvedRecipe != null) return ProceduralVfxFactory.Build(resolvedRecipe, parent);
            }

            if (logUnresolvedKeys) Debug.LogWarning($"[BattleVfxPresenter] No detached build for '{key}'.");
            return null;
        }
    }
}
