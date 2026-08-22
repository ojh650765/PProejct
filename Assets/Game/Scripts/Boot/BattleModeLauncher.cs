using System;
using System.Collections;
using System.Collections.Generic;
using PokeLab.Battle;
using PokeLab.Core;
using PokeLab.Online;
using PokeLab.Overworld;
using PokeLab.UI;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Two assemblies define a CreatureFactory and both are in scope here. The battle one is
// the right one: it is what BattleStage itself builds parties with, it takes the `ordinal`
// that keeps two same-species party members from sharing an instance id, and a party built
// by the other would differ from the one the engine expects in exactly the way nobody would
// look for.
using CreatureFactory = PokeLab.Battle.CreatureFactory;

namespace PokeLab.Boot
{
    /// <summary>
    /// A battle outside the story: your gacha team against somebody, from the title screen.
    ///
    /// <b>It reuses the whole existing battle, deliberately.</b> The arena, the camera rig, the
    /// creature views, the HUD and the turn pacing all already exist and are all in Battle.unity
    /// — which the overworld loads additively for every wild encounter. Building a second,
    /// simpler battle for this mode would have meant a second set of bugs and a second thing to
    /// keep in step with the engine. So this does exactly what the overworld's transition does:
    /// load that scene, let <c>BattlePresenter</c> claim the registered stage, and hand it an
    /// <see cref="EncounterRequest"/>.
    ///
    /// <b>What it has to fake, and how.</b> The stage builds the player's party from
    /// <c>IPlayerProfile.Party</c> and the opponent's from <c>ITrainerRegistry</c>. Neither of
    /// those exists on the title screen, and neither should be the story's — a battle-mode fight
    /// must not touch the save. So this stands up a throwaway profile holding the gacha team and
    /// a throwaway registry holding the opponent, registers both for the length of the battle,
    /// and puts back whatever was there when it is over.
    ///
    /// <b>The result is reported to the server, not computed here.</b> Experience for both modes
    /// is the Worker's business (see Server/pokelab-online/src/battle.ts); this sends what
    /// happened and shows what came back.
    /// </summary>
    public static class BattleModeLauncher
    {
        /// <summary>Starts a battle in the named mode. "ai" today; "pvp" once the room is wired.</summary>
        public static void Launch(string mode)
        {
            var host = UnityEngine.Object.FindAnyObjectByType<BattleModeSession>();
            if (host == null)
            {
                var go = new GameObject("BattleModeSession");
                UnityEngine.Object.DontDestroyOnLoad(go);
                host = go.AddComponent<BattleModeSession>();
            }

            host.Begin(mode);
        }
    }

    /// <summary>Runs one battle-mode fight from start to reported result.</summary>
    [DisallowMultipleComponent]
    public sealed class BattleModeSession : MonoBehaviour
    {
        private const string BattleSceneName = "Battle";

        /// <summary>Where a battle-mode fight came from, and where Finish sends the player back.</summary>
        private const string MenuSceneName = "MainMenu";

        private bool _running;
        private Canvas _canvas;
        private TextMeshProUGUI _status;
        private BattleExpSummary _summary;

        /// <summary>What was registered before we replaced it, so it can be put back.</summary>
        private IPlayerProfile _previousProfile;
        private ITrainerRegistry _previousTrainers;
        private bool _hadProfile;
        private bool _hadTrainers;

        public void Begin(string mode)
        {
            if (_running) return;
            var session = OnlineSession.Instance;
            if (session == null || !session.HasTeam) return;

            _running = true;
            StartCoroutine(Run(mode == "pvp" ? "pvp" : "ai"));
        }

        private IEnumerator Run(string mode)
        {
            BuildOverlay();
            Say(Loc.Pick("Preparing the battle…", "대전을 준비하는 중…"));

            var session = OnlineSession.Instance;
            var roster = session.Roster;

            // 1. Who we are fighting, resolved BEFORE either party is built.
            //
            // In PvP the opponent is a real person's six, read from the database by the Worker
            // and handed over the match socket — never chosen locally, because a team the client
            // picks is a team that means nothing across the network. In AI mode there is nobody
            // to be fair to, so it is drawn here.
            var pvp = mode == "pvp" ? PvpSession.Ensure() : null;
            var matchId = "";
            var opponentName = Loc.Pick("Challenger", "도전자");

            if (pvp != null)
            {
                if (pvp.State != PvpSession.Phase.Ready || pvp.OpponentRoster.Length == 0)
                {
                    // Reached without a match — the matchmaking screen was skipped, or the
                    // opponent left between the VS board and here. Refusing is the only honest
                    // outcome: quietly substituting an AI team would report a PvP result, at the
                    // PvP experience rate, for a fight nobody else was in.
                    Say(PvpSession.Explain(pvp.State == PvpSession.Phase.OpponentLeft
                        ? "disconnected" : "bad_match"));
                    yield return Wait(2.5f);
                    Finish();
                    yield break;
                }

                matchId = pvp.MatchId;
                if (!string.IsNullOrEmpty(pvp.OpponentName)) opponentName = pvp.OpponentName;
            }

            // 2. ONE salt for both parties in a PvP match, and it has to be the match's own.
            //
            // This is the difference between a synchronised battle and a desync that only shows
            // up as the two players disagreeing about who won. CreatureFactory derives IVs from
            // the seed it is handed, so a creature is only identical on both machines if both
            // machines salted it the same way. With a fixed "mine" salt and a different "theirs"
            // salt, MY copy of your Pikachu and YOUR copy of your Pikachu would roll different
            // IVs, take different damage, and faint on different turns — from the same shared
            // engine seed, which is what makes it so hard to see. The room mints Seed precisely
            // so both sides can agree on something neither of them chose.
            var partySalt = pvp != null ? pvp.Seed : 0x51DE;

            var playerParty = BuildParty(roster, partySalt);
            if (playerParty.Count == 0)
            {
                Say(Loc.Pick("Your team could not be built.", "팀을 만들 수 없었어요."));
                yield return Wait(2f);
                Finish();
                yield break;
            }

            var opponent = pvp != null
                ? BuildParty(pvp.OpponentRoster, partySalt)
                : BuildOpponent(roster);

            // 2. Swap in the throwaway services. Recorded first, and restored in Finish on
            //    every path — a battle-mode fight that left its fake profile registered would
            //    be a story save quietly replaced by six gacha creatures.
            _hadProfile = ServiceHub.TryGet<IPlayerProfile>(out _previousProfile);
            _hadTrainers = ServiceHub.TryGet<ITrainerRegistry>(out _previousTrainers);

            var profile = new PlayerProfile();
            profile.SetTrainerName(session.TrainerName);
            foreach (var creature in playerParty) profile.TryAddToParty(creature);
            ServiceHub.Register<IPlayerProfile>(profile);

            var trainers = new BattleModeTrainers(BattleModeTrainers.OpponentId,
                opponentName, opponent);
            ServiceHub.Register<ITrainerRegistry>(trainers);

            // 3. The stage has to be registered before the arena's presenter wakes, or the
            //    presenter finds nothing to claim and the battle plays itself out unattended.
            EnsureComponent<BattleStageHost>();
            EnsureComponent<BattleHudPresenter>();
            EnsureComponent<AvPresenterHost>();

            // 4. The arena. SINGLE, which is the opposite of what this used to do and the
            //    reason the web build could not enter a battle.
            //
            //    It was additive, "exactly as TransitionDirector loads it, so the title screen
            //    stays underneath and there is somewhere to come back to". That reasoning is
            //    right for a story battle -- the town has to be there afterwards, with the
            //    player standing where they left. It is wrong here. The title screen is not a
            //    place; it is a menu, and coming back to it means building it again, which is
            //    all it ever does. Keeping it underneath bought nothing and cost everything it
            //    had loaded: every canvas, the roster, the six team portraits, and whatever the
            //    gacha reveal touched on the way in.
            //
            //    On a desktop that is untidy. On the web it is fatal, because Unity keeps the
            //    whole data file resident and the heap can only grow by allocating a bigger
            //    contiguous block and copying -- a move that fails once the block is large
            //    enough, and that failure is abort("OOM"). The arena itself only wants 14 MB of
            //    textures; it was dying on top of everything it did not need.
            //
            //    Safe because this session is DontDestroyOnLoad and its status canvas is
            //    parented to it, so both survive the load. Finish() puts the menu back.
            Say(Loc.Pick("Loading the arena…", "경기장을 불러오는 중…"));

            // The web build aborts with OOM somewhere in here and the browser's stack says
            // nothing about which allocation did it. These three lines bracket the load, so the
            // console shows what the engine was holding just before it died -- and whether the
            // arena is one fat allocation or a slow climb.
            MemoryRelief.Report("before arena load");
            if (MemoryRelief.Trace) MemoryCensus.Dump("before arena load");

            // Give back what the menu is holding, immediately before the peak.
            //
            // The arena is loaded additively so the title screen survives underneath and there
            // is somewhere to come back to -- but "survives" is not "is being looked at". The
            // menu behind a battle is covered completely, and everything it had loaded to draw
            // itself is dead weight for the length of the fight: six team portraits, whatever
            // the gacha reveal touched on the way here, every creature the roster screen showed.
            // On the web that weight is the difference between the arena fitting and the heap
            // asking to grow, and a grow that fails is the abort("OOM") this whole path keeps
            // dying on.
            //
            // dropCreatureArt is safe here for the same reason: the pictures it blanks are
            // behind the arena, and the menu rebuilds them from its own Refresh when the battle
            // hands control back.
            MemoryRelief.Reclaim("entering a battle", dropCreatureArt: true);
            MemoryRelief.Report("after pre-battle reclaim");
            if (MemoryRelief.Trace) MemoryCensus.Dump("after pre-battle reclaim");

            // The menu's canvas goes dark for the length of the fight, and this is the part
            // that actually pays.
            //
            // Measured on the web build: a main menu with its Canvas enabled costs 798.5 MB
            // more than the same menu with the Canvas component switched off -- same objects,
            // same 147 CanvasRenderers, only the drawing prevented. So a battle loaded over a
            // live menu is paying for two canvases at once, which is what the OOM reports have
            // been. Disabling is not destroying: the menu is still there to come back to, and
            // Finish turns it on again.
            DimTheMenu();

            if (!SceneManager.GetSceneByName(BattleSceneName).isLoaded)
            {
                // ADDITIVE, and the camera is the reason.
                //
                // Battle.unity contains no Camera at all -- it is built to be laid over a scene
                // that has one, which is how the overworld uses it for wild encounters. Loading
                // it single-mode takes the menu's camera down with the menu, BattleCameraRig
                // falls back to Camera.main, Camera.main is null, and the battle renders
                // nothing. That regression was mine, from an earlier attempt to save memory by
                // unloading the menu; the saving is real but the way to take it is to stop the
                // menu DRAWING, not to delete the camera the arena borrows.
                var load = SceneManager.LoadSceneAsync(BattleSceneName, LoadSceneMode.Additive);
                if (load == null)
                {
                    Say(Loc.Pick("The battle scene is not in the build settings.",
                                 "대전 씬이 빌드 설정에 없어요."));
                    yield return Wait(2.5f);
                    Finish();
                    yield break;
                }
                // Reported as it goes, because a load that dies at 60% and one that dies on the
                // last activation frame are different problems.
                var nextMark = 0.25f;
                while (!load.isDone)
                {
                    if (load.progress >= nextMark)
                    {
                        MemoryRelief.Report($"arena load {load.progress:P0}");
                        nextMark += 0.25f;
                    }
                    yield return null;
                }
            }

            MemoryRelief.Report("after arena load");

            // A frame for the arena's own Awake/Start to run and for BattlePresenter to claim
            // the stage. Beginning in the same frame is the race that makes a battle resolve
            // itself in one synchronous loop with nothing drawn.
            yield return null;
            yield return null;

            MemoryRelief.Report("arena awake done");

            if (!ServiceHub.TryGet<IBattleStage>(out var stage))
            {
                Say(Loc.Pick("No battle stage is registered.", "대전 스테이지가 없어요."));
                yield return Wait(2.5f);
                Finish();
                yield break;
            }

            HideOverlay();

            // 5. The fight.
            EncounterResult result = null;
            stage.BeginEncounter(new EncounterRequest
            {
                Kind = BattleKind.Trainer,
                TrainerId = BattleModeTrainers.OpponentId,
                WildLevel = AverageLevel(roster),
                Seed = UnityEngine.Random.Range(int.MinValue, int.MaxValue),
                BiomeId = "arena",
            }, resolved => result = resolved);

            var elapsed = 0f;
            while (result == null && elapsed < 900f)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            // 6. Report it and show what it earned.
            ShowOverlay();
            var won = result != null && result.Outcome == BattleOutcome.PlayerVictory;

            Say(won
                ? Loc.Pick("You won.", "승리했어요.")
                : Loc.Pick("You lost.", "패배했어요."));

            var participants = new List<BattleParticipant>();
            for (var i = 0; i < roster.Length; i++)
            {
                var creature = i < profile.Party.Count ? profile.Party[i] : null;
                participants.Add(new BattleParticipant
                {
                    slot = roster[i].slot,
                    fainted = creature != null && creature.IsFainted,
                });
            }

            BattleResultResponse report = null;
            var reported = false;
            // The match id is what the Worker settles a PvP result against: it records the
            // battle once per account and refuses a replay, and a PvP report without one is
            // rejected outright (see saves/battle.ts). Empty for AI, where the server mints
            // its own.
            yield return session.ReportBattle(mode, won, matchId, participants.ToArray(), r =>
            {
                report = r;
                reported = true;
            });

            // The result screen replaces what used to be a one-line text dump — "경험치 +240,
            // 레벨 2회 상승." — with the sequence the gains deserve: one creature at a time, the
            // figure counting, the bar filling, and a rollover with its own flourish for every
            // level crossed. Skippable, because the player has seen it before.
            //
            // The plain overlay stays for the failure path only. When the server could not be
            // reached there are no gains to show, and a results screen full of zeroes would be
            // a worse lie than a sentence saying so.
            Say(string.Empty);
            var entries = BuildSummary(report, profile);

            if (reported && report != null && entries.Count > 0)
            {
                yield return Summary().Play(won, entries);
            }
            else
            {
                var note = reported && report != null
                    ? Loc.Pick("No experience was awarded.", "획득한 경험치가 없어요.")
                    : OnlineClient.Explain(session.LastError);
                yield return Summary().Play(won, entries, note);
            }

            Finish();
        }

        /// <summary>
        /// Maps the server's gains onto the neutral rows the UI understands.
        ///
        /// PokeLab.UI cannot see PokeLab.Online — the summary would drag the whole network
        /// contract into the UI assembly — so the translation happens here in Boot, which is
        /// the one place that can see both. The party is only consulted for the nickname and
        /// the species: every number on screen is the server's.
        /// </summary>
        private static List<ExperienceSummaryEntry> BuildSummary(BattleResultResponse report, PlayerProfile profile)
        {
            var entries = new List<ExperienceSummaryEntry>();
            var gains = report?.gains;
            if (gains == null) return entries;

            for (var i = 0; i < gains.Length; i++)
            {
                var gain = gains[i];
                if (gain == null) continue;

                // The slot is the server's index into the roster, which is the order the party
                // was built in — so it is also the party index, and the two only disagree if a
                // roster entry failed to build, in which case the name is simply omitted.
                var member = profile != null && gain.slot >= 0 && gain.slot < profile.Party.Count
                    ? profile.Party[gain.slot]
                    : null;

                entries.Add(new ExperienceSummaryEntry
                {
                    SpeciesId = gain.speciesId > 0 ? gain.speciesId : (member?.SpeciesId ?? 0),
                    DisplayName = member != null ? UiServices.NameOf(member) : null,
                    Gained = gain.experienceGained,
                    NewTotal = gain.experience,
                    NewLevel = gain.level,
                    LevelsGained = gain.levelsGained,
                });
            }

            return entries;
        }

        private BattleExpSummary Summary()
        {
            if (_summary != null) return _summary;
            BuildOverlay();
            _summary = BattleExpSummary.Build(_canvas.transform);
            return _summary;
        }

        // --- Parties ---------------------------------------------------------------------

        private static List<CreatureInstance> BuildParty(RosterEntry[] roster, int seedSalt)
        {
            var party = new List<CreatureInstance>();
            if (roster == null) return party;

            for (var i = 0; i < roster.Length && party.Count < PlayerProfile.MaxPartySize; i++)
            {
                var entry = roster[i];
                if (entry == null) continue;
                // The instance is rebuilt from species and level rather than stored: the server
                // owns the two numbers that matter and everything else about a creature — its
                // IVs, its moves — is derived, so there is nothing else to persist.
                party.Add(CreatureFactory.Create(entry.speciesId, entry.level,
                    entry.speciesId * 7919 + entry.slot + seedSalt, ordinal: i));
            }

            return party;
        }

        /// <summary>
        /// Six distinct creatures for the computer to play, at the player's own level.
        ///
        /// Drawn from the species that HAVE PORTRAITS rather than from the whole dex, for the
        /// reason the gacha pool is built the same way: a creature the client cannot draw is a
        /// blank rectangle on the field. Levelled to the player's average so a freshly drawn
        /// team gets a winnable fight and a levelled one does not get a walkover.
        ///
        /// Chosen on the client, and that is fine here and would not be in PvP: nobody is
        /// cheated by the computer's team, and the server does not need to agree about it.
        /// </summary>
        private static List<CreatureInstance> BuildOpponent(RosterEntry[] roster)
        {
            var level = AverageLevel(roster);
            var pool = DrawablePool();
            var party = new List<CreatureInstance>();

            if (pool.Count == 0) return party;

            // Fisher-Yates over a copy, so the six are distinct without a contains-check loop
            // that can spin when the pool is small.
            for (var i = pool.Count - 1; i > 0; i--)
            {
                var j = UnityEngine.Random.Range(0, i + 1);
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }

            var count = Mathf.Min(PlayerProfile.MaxPartySize, pool.Count);
            for (var i = 0; i < count; i++)
            {
                party.Add(CreatureFactory.Create(pool[i], level,
                    UnityEngine.Random.Range(int.MinValue, int.MaxValue), ordinal: i));
            }

            return party;
        }

        private static List<int> DrawablePool()
        {
            var ids = new List<int>();
            if (!ServiceHub.TryGet<ISpeciesRegistry>(out var species)) return ids;
            ServiceHub.TryGet<ICreatureArtRegistry>(out var art);

            foreach (var entry in species.All)
            {
                if (entry == null) continue;
                if (art != null && art.GetPortrait(entry.Id) == null) continue;
                ids.Add(entry.Id);
            }

            return ids;
        }

        private static int AverageLevel(RosterEntry[] roster)
        {
            if (roster == null || roster.Length == 0) return 5;
            var total = 0;
            foreach (var entry in roster) if (entry != null) total += entry.level;
            return Mathf.Max(1, total / roster.Length);
        }

        private T EnsureComponent<T>() where T : Component
        {
            var existing = FindAnyObjectByType<T>();
            if (existing != null) return existing;
            return gameObject.AddComponent<T>();
        }

        // --- Coming back --------------------------------------------------------------------

        private void Finish()
        {
            // The services go back before the scene does, so nothing waking during the unload
            // can read the throwaway profile.
            if (_hadProfile && _previousProfile != null) ServiceHub.Register(_previousProfile);
            if (_hadTrainers && _previousTrainers != null) ServiceHub.Register(_previousTrainers);
            _previousProfile = null;
            _previousTrainers = null;

            // The arena covered the menu rather than replacing it, so leaving is an unload.
            if (SceneManager.GetSceneByName(BattleSceneName).isLoaded)
                SceneManager.UnloadSceneAsync(BattleSceneName);

            RestoreTheMenu();

            if (_canvas != null) Destroy(_canvas.gameObject);
            _canvas = null;
            _status = null;
            // Parented under the canvas, so it has just been destroyed with it; the field is
            // cleared so a second battle in the same session builds a fresh one rather than
            // running its sequence against a dead object.
            _summary = null;
            _running = false;
        }

        /// <summary>Canvases this session switched off on the way into a battle.</summary>
        private readonly List<Canvas> _dimmed = new List<Canvas>();

        /// <summary>
        /// Switches off every canvas that is not ours, for the length of the fight.
        ///
        /// The arena is laid over the title screen, so the menu is still loaded and still
        /// drawing itself underneath a view that covers it completely. On the web that is not
        /// merely untidy: the measured cost of a canvas being enabled here is 798.5 MB, and the
        /// heap never gives it back, so the battle then asks for its own on top.
        ///
        /// The camera is deliberately left alone -- Battle.unity has none and borrows this one.
        /// </summary>
        private void DimTheMenu()
        {
            _dimmed.Clear();
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (canvas == null || !canvas.enabled) continue;
                // Not our own status overlay: it is the thing telling the player what is
                // happening while the arena loads.
                if (_canvas != null && canvas.transform.IsChildOf(_canvas.transform)) continue;
                if (canvas == _canvas) continue;

                canvas.enabled = false;
                _dimmed.Add(canvas);
            }

            if (_dimmed.Count > 0)
                Debug.Log($"[BattleMode] {_dimmed.Count} canvas(es) dimmed for the battle.");
        }

        /// <summary>Turns back on whatever <see cref="DimTheMenu"/> switched off.</summary>
        private void RestoreTheMenu()
        {
            for (var i = 0; i < _dimmed.Count; i++)
                if (_dimmed[i] != null) _dimmed[i].enabled = true;
            _dimmed.Clear();
        }

        // --- The little overlay ----------------------------------------------------------------

        private void BuildOverlay()
        {
            if (_canvas != null) { ShowOverlay(); return; }

            UiBuilder.EnsureEventSystem();

            var host = new GameObject("BattleModeOverlay", typeof(Canvas),
                typeof(UnityEngine.UI.CanvasScaler), typeof(UnityEngine.UI.GraphicRaycaster));
            host.transform.SetParent(transform, false);
            _canvas = UiBuilder.ConfigureCanvas(host.GetComponent<Canvas>(), 600);

            var root = UiBuilder.Rect("Root", host.transform);
            var scrim = UiBuilder.Backdrop("Scrim", root, null,
                new Color(0.02f, 0.03f, 0.05f, 0.9f), true);
            UiBuilder.Stretch(scrim.rectTransform);

            _status = UiBuilder.Text("Status", root, "", UiTextRole.Title,
                UiPalette.TextPrimary, TextAlignmentOptions.Center);
            UiBuilder.Stretch(_status.rectTransform, 120f);
        }

        private void ShowOverlay()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(true);
            else BuildOverlay();
        }

        private void HideOverlay()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void Say(string message)
        {
            if (_status != null) _status.text = message ?? "";
        }

        private static IEnumerator Wait(float seconds)
        {
            var elapsed = 0f;
            while (elapsed < seconds) { elapsed += Time.unscaledDeltaTime; yield return null; }
        }

        // The dismiss-on-any-key wait used to live here, for the text summary that has been
        // replaced. BattleExpSummary owns it now, because the same press has to serve two
        // purposes — skip the remaining rolls, then dismiss — and only the sequence knows
        // which of the two it is at any moment.
    }

    /// <summary>
    /// A trainer registry holding exactly one opponent, for the length of one battle.
    ///
    /// <see cref="BattleStage"/> builds a trainer's party through this interface, and the
    /// alternative to implementing it was a way to hand the stage a party directly — a change
    /// to the staging path that every story battle also runs through. This is the smaller
    /// surface: the stage is unchanged, and battle mode is simply a trainer it happens to know
    /// about.
    /// </summary>
    public sealed class BattleModeTrainers : ITrainerRegistry
    {
        public const string OpponentId = "battlemode_opponent";

        private readonly string _id;
        private readonly TrainerProfile _profile;
        private readonly List<CreatureInstance> _party;

        public BattleModeTrainers(string id, string displayName, List<CreatureInstance> party)
        {
            _id = id;
            _party = party ?? new List<CreatureInstance>();
            _profile = new TrainerProfile
            {
                TrainerId = id,
                DisplayName = displayName,
                Reward = 0,
            };
        }

        public bool TryGetProfile(string trainerId, out TrainerProfile profile)
        {
            if (trainerId == _id) { profile = _profile; return true; }
            profile = null;
            return false;
        }

        /// <summary>
        /// A fresh list every call, as the interface requires — the engine mutates what it is
        /// handed, and a second battle against a shared list would open with a party that is
        /// already fainted.
        /// </summary>
        public IReadOnlyList<CreatureInstance> BuildParty(string trainerId, int levelOffset = 0)
        {
            if (trainerId != _id) return Array.Empty<CreatureInstance>();

            var copy = new List<CreatureInstance>(_party.Count);
            for (var i = 0; i < _party.Count; i++)
            {
                var source = _party[i];
                if (source == null) continue;
                var rebuilt = CreatureFactory.Create(source.SpeciesId,
                    Mathf.Max(1, source.Level + levelOffset), source.InstanceId.GetHashCode(), ordinal: i);
                copy.Add(rebuilt);
            }

            return copy;
        }
    }
}
