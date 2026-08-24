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

            // The PARTY, not the collection. Once the gacha started drawing into a collection
            // rather than a team, "the first six rows" stopped being an answer to "who fights" --
            // it would field whichever six happened to be drawn earliest and silently ignore
            // every choice made on the 내 포켓몬 screen. `Party` is the same rule the Worker's own
            // partyOf applies, so the team here and the team a PvP opponent is handed agree.
            var roster = session.Party;

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

            // Who the player is about to face, and what they look like.
            //
            // A PvP opponent is a real person with a real name already fetched from the room, so
            // only the AI's title is ours to give -- overwriting a human's name with "등산가"
            // would be worse than the generic "도전자" it replaces.
            var look = BattleModeOpponents.Pick();
            var opponentArt = pvp == null ? look.ArtKey : BattleModeOpponents.PvpArtKey;
            if (pvp == null) opponentName = look.Title;

            var trainers = new BattleModeTrainers(BattleModeTrainers.OpponentId,
                opponentName, opponent, opponentArt);
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

            // Before the load, not after: BattleCameraRig caches its brain in Awake, and Awake
            // runs while the arena scene is loading. The host object is DontDestroyOnLoad, so
            // it survives the single-mode load that is about to replace everything else.
            PokeLab.Cinematics.BattleCameraHost.Ensure();

            // Tell the world the mode changed, because nothing else here will.
            //
            // MusicDirector switches to the battle theme on GameMode.Battle, and the only
            // caller of GameEvents.RaiseModeChanged in the project is GameFlowController --
            // an OVERWORLD component that a battle launched from the title screen never
            // touches. So the director was never told, and the menu's title track played
            // straight through the fight. The kind (wild vs trainer) still comes from
            // BattleStartedEvent, which BattleAudioPresenter relays; this is the transition
            // that comment refers to when it says "the music director owns the transition".
            GameEvents.RaiseModeChanged(GameMode.Menu, GameMode.Battle);

            if (!SceneManager.GetSceneByName(BattleSceneName).isLoaded)
            {
                // SINGLE, because there is nothing under the title screen worth keeping.
                //
                // Battle.unity carries no Camera -- it is built to be laid over a scene that
                // has one, which is how the overworld uses it for wild encounters. That is why
                // an earlier single-mode load here broke the battle: it took the menu's camera
                // down with the menu. But MainMenu is an empty level whose UI is built in code,
                // and its camera could not have drawn the arena anyway (no CinemachineBrain, and
                // a culling mask of zero). Keeping it loaded underneath bought nothing at all.
                //
                // So the arena replaces it, and BattleCameraHost above supplies the camera the
                // scene does not have. The menu costing nothing while a battle runs is the
                // point; it is rebuilt from code on the way back.
                var load = SceneManager.LoadSceneAsync(BattleSceneName, LoadSceneMode.Single);
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

            // 4b. Stand somebody at each mark.
            //
            // Every battle reached from the overworld gets this from TransitionDirector, and
            // battle mode does not go through TransitionDirector -- it loads the arena itself,
            // which is why the encounter already said BattleKind.Trainer and still played like
            // a wild one. With nothing bound, the far TrainerView has no art, BattlePresenter
            // skips the throw for that side, and the AI's creature simply appears while the
            // player's is thrown by a person standing there. The asymmetry is the series'
            // signal for "this one is wild", so a trainer battle wearing it reads as a bug.
            BattleModeTrainerMarks.Bind(opponentArt);

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

            // 6. Report it and show what it earned -- but not until the arena has finished
            //    saying it.
            //
            // `result` is filled the moment the ENGINE ends the battle, which is several
            // seconds before the presenter has played the last creature fainting. Going
            // straight on from here put the whole team's experience summary on screen while
            // the final knockout was still animating: the player was told what they had won
            // before being shown the win. The presenter already knows when its queue is
            // empty, so ask it.
            var presenter = UnityEngine.Object.FindAnyObjectByType<PokeLab.Cinematics.BattlePresenter>();
            if (presenter != null) yield return presenter.WaitUntilIdle(20f);

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
            var entries = BuildSummary(report, profile, roster);

            if (reported && report != null && entries.Count > 0)
            {
                yield return Summary().Play(won, entries, null, RewardLine(report));
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
        /// <summary>
        /// What the battle paid beyond experience, as one line.
        ///
        /// Coins always — both outcomes pay, and the loss half of that rule only does its job if
        /// the player can see it happen. Drops only when there were any, because "가끔식
        /// 획득가능하게" means most battles have nothing to add and a line reading
        /// "dropped: nothing" every time would drown the times it did.
        /// </summary>
        private static string RewardLine(BattleResultResponse report)
        {
            if (report == null) return null;

            var line = Loc.Pick($"+{report.coinsGained:N0} coins", $"코인 +{report.coinsGained:N0}");

            var drops = report.drops;
            if (drops == null || drops.Length == 0) return line;

            var names = new List<string>(drops.Length);
            foreach (var itemId in drops)
            {
                if (string.IsNullOrEmpty(itemId)) continue;
                names.Add(itemId == "candy"
                    ? Loc.Pick("Rare Candy", "이상한 사탕")
                    : itemId.StartsWith("disc:")
                        ? Loc.Pick($"{UiServices.MoveName(itemId.Substring(5))} disc",
                                   $"{UiServices.MoveName(itemId.Substring(5))} 디스크")
                        : itemId);
            }

            if (names.Count == 0) return line;
            return line + Loc.Pick("   ·   found " + string.Join(", ", names),
                                   "   ·   " + string.Join(", ", names) + " 획득!");
        }

        private static List<ExperienceSummaryEntry> BuildSummary(BattleResultResponse report,
                                                                 PlayerProfile profile,
                                                                 RosterEntry[] party)
        {
            var entries = new List<ExperienceSummaryEntry>();
            var gains = report?.gains;
            if (gains == null) return entries;

            for (var i = 0; i < gains.Length; i++)
            {
                var gain = gains[i];
                if (gain == null) continue;

                // The slot is the server's COLLECTION index, which stopped being the party index
                // the moment the gacha started drawing into a collection: a party built from
                // slots 3, 9 and 40 would have read those straight off profile.Party and shown
                // the wrong creature's name, or none. So it is looked up in the same party array
                // the profile was built from, in the same order.
                var partyIndex = -1;
                if (party != null)
                {
                    for (var p = 0; p < party.Length; p++)
                    {
                        if (party[p] != null && party[p].slot == gain.slot) { partyIndex = p; break; }
                    }
                }

                var member = profile != null && partyIndex >= 0 && partyIndex < profile.Party.Count
                    ? profile.Party[partyIndex]
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
                // owns the numbers that matter and everything else about a creature -- its
                // IVs, its ability -- is derived from the seed, so there is nothing else to
                // persist. What the server DOES own beyond the level is the growth the player
                // paid for, and those two lines below are it.
                var creature = CreatureFactory.Create(entry.speciesId, entry.level,
                    entry.speciesId * 7919 + entry.slot + seedSalt, ordinal: i);

                ApplyTaughtMoves(creature, entry.moves);
                ApplyStars(creature, entry.stars);

                party.Add(creature);
            }

            return party;
        }

        /// <summary>
        /// Replaces the derived moveset with the one the player taught, when there is one.
        ///
        /// Empty is the normal state and means "whatever the level-up learnset gives at this
        /// level", which both runtimes derive identically -- so nothing is written down until a
        /// disc is actually taught. Once it is, the server's list is the only one that counts,
        /// because it is the list a PvP opponent's copy of this creature will be built from too.
        ///
        /// A move id the local registry does not know is dropped rather than faked. That can
        /// only happen if the Worker's learnsets and moves.json have drifted apart, and a slot
        /// carrying a move the engine cannot look up would throw mid-turn.
        /// </summary>
        private static void ApplyTaughtMoves(CreatureInstance creature, string taught)
        {
            if (creature == null || string.IsNullOrWhiteSpace(taught)) return;
            if (!ServiceHub.TryGet<IMoveRegistry>(out var moves) || moves == null) return;

            var ids = taught.Split(',');
            var resolved = new List<MoveData>(4);
            foreach (var raw in ids)
            {
                var id = raw.Trim();
                if (id.Length == 0) continue;
                if (moves.TryGet(id, out var move) && move != null) resolved.Add(move);
            }

            if (resolved.Count == 0)
            {
                Debug.LogWarning($"[BattleMode] {creature.SpeciesId} was taught \"{taught}\" but the " +
                                 "move registry knows none of it; keeping the learnset moveset. " +
                                 "The Worker's learnsets.ts and moves.json have drifted.");
                return;
            }

            CreatureFactory.FillMoves(creature, resolved);
        }

        /// <summary>
        /// 돌파: +4% to every stat per star.
        ///
        /// Applied here rather than inside CreatureFactory because stars belong to an ONLINE
        /// collection and the factory is what the story mode builds wild encounters with -- a
        /// stat bonus reaching into that would strengthen creatures nobody had broken through.
        ///
        /// HP is scaled with the rest and CurrentHp is set from the new maximum, because a
        /// creature is built at full health here and a bonus that raised the ceiling without
        /// raising the fill would send it into the arena already hurt.
        /// </summary>
        private static void ApplyStars(CreatureInstance creature, int stars)
        {
            if (creature?.Stats == null || stars <= 0) return;

            var multiplier = 1f + 0.04f * Mathf.Clamp(stars, 0, 5);
            for (var i = 0; i < creature.Stats.Length; i++)
                creature.Stats[i] = Mathf.Max(1, Mathf.RoundToInt(creature.Stats[i] * multiplier));

            creature.MaxHp = creature.Stats[(int)StatKind.Hp];
            creature.CurrentHp = creature.MaxHp;
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

        /// <summary>
        /// The species an AI opponent may be drawn from: the ones that can actually be SEEN.
        ///
        /// <b>The filter used to be a no-op.</b> It asked <c>ICreatureArtRegistry</c> whether a
        /// species had a portrait and skipped it if not — but nothing in this project registers
        /// that interface with real art. <c>BattleArena</c> registers a stub that answers heights
        /// and returns null for every sprite, and the catalogue behind the real one has never
        /// been built; the console says so on every battle: "No creature art registry was
        /// registered". So `art` came back null, the guard short-circuited on `art != null`, and
        /// the pool became all 721 species.
        ///
        /// Only 53 of those have battle sprites. The other 668 reached the arena, failed to
        /// resolve a texture, and fell through to CreatureView's ellipsoid placeholder — the one
        /// its own comment calls "never meant to reach a build". That is the row of grey blobs.
        ///
        /// So ask the thing that actually draws them. <see cref="CreatureSpriteLibrary"/> is the
        /// path every battle billboard resolves through, and a species it has an entry for is a
        /// species that will appear. The art registry is still consulted when one exists, since
        /// a real catalogue would be the better answer.
        /// </summary>
        private static List<int> DrawablePool()
        {
            var ids = new List<int>();
            if (!ServiceHub.TryGet<ISpeciesRegistry>(out var species)) return ids;
            ServiceHub.TryGet<ICreatureArtRegistry>(out var art);

            var sprites = PokeLab.Cinematics.CreatureSpriteLibrary.Shared;
            var haveManifest = sprites != null && sprites.HasManifest;

            foreach (var entry in species.All)
            {
                if (entry == null) continue;
                if (art != null && art.GetPortrait(entry.Id) != null) { ids.Add(entry.Id); continue; }
                if (haveManifest && !sprites.Has(entry.Id)) continue;
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

            // The arena replaced the menu rather than covering it, so leaving is a load.
            // Released first: the battle camera must not outlive the scene it was made for, and
            // the title screen brings its own back.
            // Out of battle mode before anything else, so the outro fade starts while the
            // arena is still up rather than after the menu has replaced it.
            GameEvents.RaiseModeChanged(GameMode.Battle, GameMode.BattleOutro);
            GameEvents.RaiseModeChanged(GameMode.BattleOutro, GameMode.Menu);

            PokeLab.Cinematics.BattleCameraHost.Release();

            if (SceneManager.GetSceneByName(BattleSceneName).isLoaded)
                SceneManager.LoadScene(MenuSceneName, LoadSceneMode.Single);

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
    /// <summary>
    /// Stands both trainers at their marks for a battle-mode fight.
    ///
    /// The player's own sprite on the near mark and <paramref name="opponentArtKey"/> on the
    /// far one, which is exactly what <c>TransitionDirector</c> does for a story trainer battle.
    /// Reached through the presenter because the cinematic stage is the arena scene's, and this
    /// runs after that scene has loaded and its presenter has claimed it.
    /// </summary>
    internal static class BattleModeTrainerMarks
    {
        internal static void Bind(string opponentArtKey)
        {
            var presenter = UnityEngine.Object.FindAnyObjectByType<PokeLab.Cinematics.BattlePresenter>();
            var stage = presenter != null ? presenter.Stage : null;
            if (stage == null)
            {
                // Not fatal: the battle still plays, the send-out just has nobody throwing.
                // Said out loud because a silent miss here is indistinguishable from the bug
                // this exists to fix.
                Debug.LogWarning("[BattleMode] No cinematic stage to stand the trainers on; " +
                                 "the send-out will play without them.");
                return;
            }

            stage.SetTrainers(PlayerBody.SpriteKey, opponentArtKey);
            Debug.Log("[BattleMode] Trainers bound: player='" + PlayerBody.SpriteKey +
                      "' opponent='" + opponentArtKey + "'.");
        }
    }

    /// <summary>
    /// The face battle mode's opponent wears, drawn at random from the portraits the game
    /// already ships.
    ///
    /// <b>Why random.</b> The arena is a queue of challengers rather than one opponent fought
    /// over and over, and meeting the same silhouette on every entry reads as the mode not
    /// having restarted. Seven faces is enough that a run of battles feels like a run of
    /// different people.
    ///
    /// <b>Why these seven.</b> They are every portrait under <c>Resources/Portraits</c> that
    /// depicts somebody who could plausibly challenge you. <c>professor</c> is a story
    /// character, <c>research_terminal</c> is not a person at all, and <c>player</c>/
    /// <c>player_f</c> are the player — standing any of them at the far mark would say
    /// something the battle does not mean. No new art was needed for this.
    ///
    /// Only front views are used, which is why the lack of <c>_back</c> sheets for these keys
    /// does not matter: the opponent is the one being looked at across the field, and
    /// <see cref="PokeLab.Cinematics.TrainerView"/> only reaches for <c>_back</c> on the side
    /// the camera stands behind.
    /// </summary>
    public static class BattleModeOpponents
    {
        /// <summary>
        /// What a human opponent is drawn as.
        ///
        /// A PvP opponent is a real person whose look we do not know — the protocol carries a
        /// name and a roster, not a body. <c>rival</c> is the honest choice among what exists:
        /// unmistakably another trainer, and pointedly not the player's own sprite, which at the
        /// far mark would read as fighting yourself.
        /// </summary>
        public const string PvpArtKey = "rival";

        private static readonly string[][] Roster =
        {
            new[] { "youngster",  "Youngster",  "소년" },
            new[] { "lass",       "Lass",       "소녀" },
            new[] { "hiker",      "Hiker",      "등산가" },
            new[] { "gardener",   "Gardener",   "정원사" },
            new[] { "townsman",   "Townsfolk",  "마을 주민" },
            new[] { "shopkeeper", "Shopkeeper", "상인" },
            new[] { "rival",      "Rival",      "라이벌" },
        };

        /// <summary>Which face the last battle used, so the next one does not repeat it.</summary>
        private static int _last = -1;

        /// <summary>
        /// A face and the title that goes with it. Never the same one twice running: with seven
        /// entries a chance repeat lands often enough to look like a bug rather than like luck.
        /// </summary>
        public static (string ArtKey, string Title) Pick()
        {
            var index = UnityEngine.Random.Range(0, Roster.Length);
            if (index == _last && Roster.Length > 1)
                index = (index + 1 + UnityEngine.Random.Range(0, Roster.Length - 1)) % Roster.Length;
            _last = index;

            var entry = Roster[index];
            return (entry[0], Loc.Pick(entry[1], entry[2]));
        }
    }

    public sealed class BattleModeTrainers : ITrainerRegistry
    {
        public const string OpponentId = "battlemode_opponent";

        private readonly string _id;
        private readonly TrainerProfile _profile;
        private readonly List<CreatureInstance> _party;

        public BattleModeTrainers(string id, string displayName, List<CreatureInstance> party,
                                  string artKey = null)
        {
            _id = id;
            _party = party ?? new List<CreatureInstance>();
            _profile = new TrainerProfile
            {
                TrainerId = id,
                DisplayName = displayName,
                // What the opponent is drawn as. Left empty this used to reach
                // TransitionDirector.OpponentPersonKey as "no art", and battle mode never asked
                // it anything anyway — see BattleModeLauncher.BindTrainers.
                ArtKey = artKey,
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
