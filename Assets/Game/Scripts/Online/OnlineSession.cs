using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace PokeLab.Online
{
    /// <summary>
    /// Who is signed in, what they own, and the coroutines that change either.
    ///
    /// <b>One object, carried across scenes.</b> The account is picked up at the title screen
    /// and spent in a battle scene two loads later, so this survives with
    /// <c>DontDestroyOnLoad</c> — the same shape <c>GameBoot</c> uses, including its rule that
    /// a second copy arriving with a loaded scene stands down rather than re-initialising over
    /// the first.
    ///
    /// <b>The token is the credential, and the answer is not kept.</b> The security answer is
    /// typed once, sent once, and never stored on the device: what is kept is the opaque token
    /// the Worker returns. That matters more here than it would with a password, because a
    /// security answer is the kind of thing a player also uses on their bank — see the note on
    /// <see cref="CreateAccount"/> for what this scheme is and is not.
    ///
    /// <b>Nothing here decides anything about the game.</b> The roster is a cache of what the
    /// server says the account owns; rolls, experience and match results are all computed on
    /// the Worker. That is not defensiveness for its own sake — it is the only arrangement in
    /// which a PvP opponent's team means anything.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class OnlineSession : MonoBehaviour
    {
        private const string TokenKey = "pokelab.online.token";
        private const string NameKey = "pokelab.online.trainerName";
        private const string AccountKey = "pokelab.online.accountId";

        private static OnlineSession _owner;

        /// <summary>The live session, or null before anything has stood one up.</summary>
        public static OnlineSession Instance => _owner;

        /// <summary>Raised whenever the signed-in state or the roster changes.</summary>
        public event Action Changed;

        public string TrainerName { get; private set; } = "";
        public string AccountId { get; private set; } = "";
        public RosterEntry[] Roster { get; private set; } = Array.Empty<RosterEntry>();

        /// <summary>The last error code any call here produced, for a screen that wants to say why.</summary>
        public string LastError { get; private set; } = "";

        /// <summary>True while a call is in flight, so a screen can refuse to send a second.</summary>
        public bool Busy { get; private set; }

        private string _token = "";

        /// <summary>True when there is a token to send. Not proof the server still honours it.</summary>
        public bool IsSignedIn => !string.IsNullOrEmpty(_token);

        /// <summary>What is in the bag: rare candies and move discs, by item id.</summary>
        public OwnedItem[] Items { get; private set; } = Array.Empty<OwnedItem>();

        /// <summary>True when the account owns anything at all.</summary>
        public bool HasTeam => Roster != null && Roster.Length > 0;

        /// <summary>
        /// The six that fight, in party order.
        ///
        /// Falls back to the first six of the collection when nothing is assigned, exactly as
        /// the Worker's own <c>partyOf</c> does — an account created before the party existed
        /// has every row at partySlot -1, and a battle entrance that refused to open until a
        /// team screen had been visited would be a wall in front of the only mode that pays.
        /// The two implementations agree by construction, and this note is here so that stays
        /// true if either moves.
        /// </summary>
        public RosterEntry[] Party
        {
            get
            {
                if (Roster == null || Roster.Length == 0) return Array.Empty<RosterEntry>();

                var assigned = new List<RosterEntry>(PartySize);
                foreach (var entry in Roster)
                    if (entry != null && entry.InParty) assigned.Add(entry);

                if (assigned.Count == 0)
                {
                    var head = new List<RosterEntry>(PartySize);
                    foreach (var entry in Roster)
                    {
                        if (entry == null) continue;
                        head.Add(entry);
                        if (head.Count >= PartySize) break;
                    }
                    return head.ToArray();
                }

                assigned.Sort((a, b) => a.partySlot.CompareTo(b.partySlot));
                if (assigned.Count > PartySize) assigned.RemoveRange(PartySize, assigned.Count - PartySize);
                return assigned.ToArray();
            }
        }

        /// <summary>How many fight at once. The party, not the collection.</summary>
        public const int PartySize = 6;

        /// <summary>
        /// The purse, as the SERVER counts it.
        ///
        /// The gacha panel used to count its own draws against a lifetime cap, which is a number
        /// that cannot survive a reconnect — signing back in offered a spent account a full
        /// allowance. That cap is gone; a pull has a price now, because a collection you may
        /// only draw five times is not a collection. These come back on every sign-in, after
        /// every roll, after every battle and after every 강화, so what the screen shows is
        /// always the answer to the last thing that changed it.
        /// </summary>
        public int Coins { get; private set; }

        /// <summary>Pulls owed rather than paid for. Six at signup, spendable one at a time.</summary>
        public int FreePulls { get; private set; }

        /// <summary>Coins for one paid pull, and the most that may be drawn at once.</summary>
        public int PullCost { get; private set; } = 500;
        public int MaxPulls { get; private set; } = 10;

        /// <summary>Pulls affordable right now: the free ones plus what the coins buy.</summary>
        public int AffordablePulls =>
            FreePulls + (PullCost > 0 ? Coins / PullCost : 0);

        /// <summary>How many of <paramref name="itemId"/> the bag holds.</summary>
        public int ItemCount(string itemId)
        {
            if (Items == null || string.IsNullOrEmpty(itemId)) return 0;
            foreach (var item in Items)
                if (item != null && item.itemId == itemId) return item.count;
            return 0;
        }

        /// <summary>The collection entry at a slot, or null.</summary>
        public RosterEntry Owned(int slot)
        {
            if (Roster == null) return null;
            foreach (var entry in Roster)
                if (entry != null && entry.slot == slot) return entry;
            return null;
        }

        /// <summary>
        /// Stands the session up if nothing has yet, and returns it.
        ///
        /// Called by whichever screen needs it first rather than placed in a scene, because the
        /// title screen, the gacha screen and the battle flow all want it and only one of them
        /// is guaranteed to have been open.
        /// </summary>
        public static OnlineSession Ensure()
        {
            if (_owner != null) return _owner;

            var existing = FindAnyObjectByType<OnlineSession>();
            if (existing != null) { _owner = existing; return _owner; }

            var host = new GameObject("OnlineSession");
            DontDestroyOnLoad(host);
            return host.AddComponent<OnlineSession>();
        }

        private void Awake()
        {
            if (_owner != null && _owner != this)
            {
                // A second copy stands down rather than replacing the first: the first is the
                // one every screen already holds a reference to.
                Destroy(gameObject);
                return;
            }

            _owner = this;
            DontDestroyOnLoad(gameObject);

            _token = PlayerPrefs.GetString(TokenKey, "");
            TrainerName = PlayerPrefs.GetString(NameKey, "");
            AccountId = PlayerPrefs.GetString(AccountKey, "");

            // A restored token has to fetch the roster too, and this is the bug that cost a
            // player their team.
            //
            // FetchRoster used to run in exactly one place -- at the end of Authenticate --
            // which covers signing in and creating an account and nothing else. The common
            // path is neither: the token comes back out of PlayerPrefs here, in Awake, the
            // login screen sees IsSignedIn and skips itself, and the session goes to the title
            // with Roster still Array.Empty. Everything downstream reads that as the truth.
            // HasTeam is false, so the menu offers a first roll; the Worker knows better and
            // answers already_rolled; and the player is told both that they have no team and
            // that they may not have one -- "내가 선택한 팀 정보도 날라갔고, 이미 팀을 뽑은
            // 계정이래". Their six were on the server the whole time.
            //
            // Started here rather than left to a screen because every screen that cares would
            // otherwise have to remember, and the one that forgot is how this happened.
            if (IsSignedIn) StartCoroutine(RestoreRoster());
        }

        /// <summary>
        /// Pulls the roster for a token restored from disk, and tells the screens when it lands.
        ///
        /// Failure is deliberately quiet: an expired token or a dead network is not a reason to
        /// interrupt a player who has not asked for anything yet, and every screen that needs
        /// the roster re-reads it from <see cref="Changed"/> anyway.
        /// </summary>
        private IEnumerator RestoreRoster()
        {
            yield return FetchRoster(null);
            Changed?.Invoke();
        }

        private void OnDestroy()
        {
            if (_owner == this) _owner = null;
        }

        // --- Accounts ---------------------------------------------------------------------

        /// <summary>
        /// Creates an account from a name, one of the fixed questions, and an answer.
        ///
        /// <b>What this is.</b> The user's design, and their reasoning: a password is more
        /// than a game of this size should ask for, so the recovery question stands in for one
        /// — 비밀번호까지는 오바. The answer is normalised and hashed on the Worker with a
        /// per-account salt; neither this device nor the database ever holds it in the clear.
        ///
        /// <b>What this is not.</b> It is not as strong as a password and should not be
        /// described to a player as if it were. A birthplace has a few thousand plausible
        /// values, so anyone who knows the trainer name can work through them — which is why
        /// the Worker rate-limits attempts per name and per address, and why nothing that
        /// matters outside this game is ever protected by it.
        /// </summary>
        public IEnumerator CreateAccount(string trainerName, string questionId, string answer,
                                         Action<bool> done)
        {
            yield return Authenticate("/account/create", trainerName, questionId, answer, done);
        }

        /// <summary>Signs an existing account in on this device by answering its question.</summary>
        public IEnumerator SignIn(string trainerName, string questionId, string answer,
                                  Action<bool> done)
        {
            yield return Authenticate("/account/login", trainerName, questionId, answer, done);
        }

        private IEnumerator Authenticate(string path, string trainerName, string questionId,
                                         string answer, Action<bool> done)
        {
            if (Busy) { done?.Invoke(false); yield break; }

            var name = (trainerName ?? "").Trim();
            if (name.Length < 2 || name.Length > 16)
            {
                LastError = "bad_name";
                done?.Invoke(false);
                yield break;
            }

            if (!SecurityQuestions.IsKnown(questionId) || string.IsNullOrWhiteSpace(answer))
            {
                LastError = "bad_answer";
                done?.Invoke(false);
                yield break;
            }

            Busy = true;
            LastError = "";

            AccountResponse response = null;
            yield return OnlineClient.Post<AccountResponse>(path,
                new AccountRequest { trainerName = name, questionId = questionId, answer = answer },
                null, r => response = r);

            Busy = false;

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                done?.Invoke(false);
                yield break;
            }

            _token = response.token ?? "";
            TrainerName = response.trainerName ?? name;
            AccountId = response.accountId ?? "";
            Coins = response.coins;
            FreePulls = response.freePulls;
            if (response.pullCost > 0) PullCost = response.pullCost;
            Persist();

            // The roster is fetched rather than assumed empty: a returning player signing in on
            // a new device has a team already, and the menu must not offer them a first roll.
            yield return FetchRoster(null);

            Changed?.Invoke();
            done?.Invoke(true);
        }

        /// <summary>Forgets the token on this device. The account itself is untouched.</summary>
        public void SignOut()
        {
            _token = "";
            TrainerName = "";
            AccountId = "";
            Roster = Array.Empty<RosterEntry>();
            Items = Array.Empty<OwnedItem>();
            Coins = 0;
            FreePulls = 0;
            HasCloudSave = false;
            PlayerPrefs.DeleteKey(TokenKey);
            PlayerPrefs.DeleteKey(NameKey);
            PlayerPrefs.DeleteKey(AccountKey);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }

        private void Persist()
        {
            PlayerPrefs.SetString(TokenKey, _token);
            PlayerPrefs.SetString(NameKey, TrainerName);
            PlayerPrefs.SetString(AccountKey, AccountId);
            PlayerPrefs.Save();
        }

        // --- Roster and gacha -------------------------------------------------------------

        /// <summary>Refreshes the cached roster from the server.</summary>
        public IEnumerator FetchRoster(Action<bool> done)
        {
            if (!IsSignedIn) { LastError = "unauthorised"; done?.Invoke(false); yield break; }

            RosterResponse response = null;
            yield return OnlineClient.Get<RosterResponse>("/roster", _token, r => response = r);

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();
                done?.Invoke(false);
                yield break;
            }

            Absorb(response);
            done?.Invoke(true);
        }

        /// <summary>
        /// Takes everything an account-shaped reply carries.
        ///
        /// Every growth route and the roster fetch answer with the same shape, on purpose: they
        /// all move at least two of collection, purse and bag, and a screen that had to work out
        /// which had changed would eventually get it wrong. One method reads all of them, so a
        /// field added to the wire reaches every caller at once.
        ///
        /// pullCost and maxPulls are only taken when positive. A Worker that predates them
        /// deserialises as zero, and a zero price would read as "pulls are free" — the same
        /// class of bug as the old rollsMax-zero one, which is why the guard is here rather than
        /// trusted to the caller.
        /// </summary>
        private void Absorb(RosterResponse response)
        {
            if (response == null) return;
            Roster = response.roster ?? Array.Empty<RosterEntry>();
            Items = response.items ?? Array.Empty<OwnedItem>();
            Coins = response.coins;
            FreePulls = response.freePulls;
            if (response.pullCost > 0) PullCost = response.pullCost;
            if (response.maxPulls > 0) MaxPulls = response.maxPulls;
            Changed?.Invoke();
        }

        /// <summary>
        /// Draws <paramref name="pulls"/> creatures into the collection.
        ///
        /// One to ten at a time — 무조건 6개가 아니라, 1개도 뽑을 수도 있고 ~n개를 뽑기 가능한거지
        /// — weighted so the better creatures are rarer, and every word of that is enforced on
        /// the Worker. What comes back is the drawn creatures in the order the presentation
        /// should reveal them, marked where one was a duplicate that became a shard, and the
        /// account they left behind.
        /// </summary>
        public IEnumerator RollGacha(int pulls, Action<GachaResponse> done)
        {
            if (!IsSignedIn) { LastError = "unauthorised"; done?.Invoke(null); yield break; }
            if (Busy) { done?.Invoke(null); yield break; }

            Busy = true;
            LastError = "";

            GachaResponse response = null;
            yield return OnlineClient.Post<GachaResponse>("/gacha/roll",
                new GachaRequest { pulls = pulls }, _token, r => response = r);

            Busy = false;

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();

                // A refusal still carries the purse. "not_enough_coins" is only useful beside
                // the number that was not enough, so it is taken even though the roll failed.
                if (response != null && response.pullCost > 0)
                {
                    Coins = response.coins;
                    FreePulls = response.freePulls;
                    PullCost = response.pullCost;
                    Changed?.Invoke();
                }
                done?.Invoke(null);
                yield break;
            }

            Roster = response.roster ?? Roster;
            Items = response.items ?? Items;
            Coins = response.coins;
            FreePulls = response.freePulls;
            if (response.pullCost > 0) PullCost = response.pullCost;
            if (response.maxPulls > 0) MaxPulls = response.maxPulls;
            Changed?.Invoke();
            done?.Invoke(response);
        }

        // --- 내 포켓몬 ------------------------------------------------------------

        /// <summary>
        /// The one shape every growth route shares.
        ///
        /// 강화, 돌파, 사탕, 기술 and the party all name a creature and change the account, and
        /// they all answer with the whole account. Routing them through one method keeps that
        /// promise in one place: whatever comes back, the session ends up describing the same
        /// thing the server does, and every screen watching <see cref="Changed"/> redraws from
        /// it. <paramref name="done"/> gets the reply so a caller can say what it cost.
        /// </summary>
        private IEnumerator Growth(string path, object body, Action<RosterResponse> done)
        {
            if (!IsSignedIn) { LastError = "unauthorised"; done?.Invoke(null); yield break; }
            if (Busy) { done?.Invoke(null); yield break; }

            Busy = true;
            LastError = "";

            RosterResponse response = null;
            yield return OnlineClient.Post<RosterResponse>(path, body, _token, r => response = r);

            Busy = false;

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();
                done?.Invoke(null);
                yield break;
            }

            Absorb(response);
            done?.Invoke(response);
        }

        /// <summary>강화: buys one level's worth of experience with coins.</summary>
        public IEnumerator Enhance(int slot, Action<RosterResponse> done) =>
            Growth("/creature/enhance", new CreatureRequest { slot = slot }, done);

        /// <summary>돌파: spends duplicate shards and coins to raise the level ceiling.</summary>
        public IEnumerator Breakthrough(int slot, Action<RosterResponse> done) =>
            Growth("/creature/breakthrough", new CreatureRequest { slot = slot }, done);

        /// <summary>이상한 사탕: one level, straight out of the bag.</summary>
        public IEnumerator FeedCandy(int slot, Action<RosterResponse> done) =>
            Growth("/creature/candy", new CreatureRequest { slot = slot }, done);

        /// <summary>
        /// Teaches a move disc into one of the four slots.
        ///
        /// The Worker checks the species' learnset before it spends anything and answers
        /// "cannot_learn" with the disc still in the bag. The screen checks the same thing first
        /// so the usual outcome is a greyed row rather than a round trip — but the Worker's
        /// check is the one that counts, because a taught move walks into a PvP match.
        /// </summary>
        public IEnumerator Teach(int slot, int moveSlot, string moveId, Action<RosterResponse> done) =>
            Growth("/creature/teach",
                   new TeachRequest { slot = slot, moveSlot = moveSlot, moveId = moveId }, done);

        /// <summary>Sets which six of the collection fight, by collection slot, in party order.</summary>
        public IEnumerator SetParty(int[] slots, Action<RosterResponse> done) =>
            Growth("/party/set", new PartyRequest { slots = slots ?? Array.Empty<int>() }, done);

        // --- Progress ---------------------------------------------------------------------

        /// <summary>
        /// Reports a finished battle and collects the experience it earned.
        ///
        /// Both modes pay — "포켓몬 레벨업은 대전/ai대전 할때마다 경험치 증가" — and both are
        /// awarded by the Worker from the mode, the result and who took part, never from a
        /// number the client proposes. An AI battle is worth less than a PvP one, which is the
        /// only reason the mode is on the wire at all.
        /// </summary>
        public IEnumerator ReportBattle(string mode, bool won, string matchId,
                                        BattleParticipant[] participants,
                                        Action<BattleResultResponse> done)
        {
            if (!IsSignedIn) { LastError = "unauthorised"; done?.Invoke(null); yield break; }

            BattleResultResponse response = null;
            yield return OnlineClient.Post<BattleResultResponse>("/battle/result",
                new BattleResultRequest
                {
                    mode = mode,
                    won = won,
                    matchId = matchId ?? "",
                    participants = participants ?? Array.Empty<BattleParticipant>(),
                },
                _token, r => response = r);

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();
                done?.Invoke(null);
                yield break;
            }

            // The gains carry the new levels and the reply carries the purse and the bag, so the
            // cached account is brought up to date from what already arrived rather than costing
            // a second round trip.
            ApplyGains(response.gains);
            Coins = response.coins;
            if (response.items != null) Items = response.items;
            Changed?.Invoke();
            done?.Invoke(response);
        }

        private void ApplyGains(ExperienceGain[] gains)
        {
            if (gains == null || Roster == null) return;
            foreach (var gain in gains)
            {
                foreach (var entry in Roster)
                {
                    if (entry == null || entry.slot != gain.slot) continue;
                    entry.level = gain.level;
                    entry.experience = gain.experience;
                    break;
                }
            }
        }

        // --- Cloud save --------------------------------------------------------------------

        /// <summary>
        /// Sends a story save up, replacing whatever was there.
        ///
        /// <b>Last write wins, and that is safe here for one specific reason:</b> this is only
        /// ever called from 리포트, so every upload is a deliberate act by a person. There is no
        /// background sync that could overwrite a session somebody is still playing. If an
        /// autosave is ever added, this rule stops being defensible and has to be revisited
        /// rather than inherited.
        ///
        /// The payload is the save file verbatim; the fields beside it are a description of it
        /// so the server can index without parsing. They are never read back as truth — the
        /// payload is.
        /// </summary>
        public IEnumerator UploadSave(string payload, int saveVersion, string trainerName,
                                      float playTimeSeconds, string savedAtUtc,
                                      Action<bool> done)
        {
            if (!IsSignedIn) { LastError = "unauthorised"; done?.Invoke(false); yield break; }
            if (string.IsNullOrWhiteSpace(payload)) { LastError = "empty_save"; done?.Invoke(false); yield break; }

            SavePutResponse response = null;
            yield return OnlineClient.Post<SavePutResponse>("/save/put",
                new SavePutRequest
                {
                    payload = payload,
                    saveVersion = saveVersion,
                    trainerName = trainerName ?? "",
                    playTimeSeconds = playTimeSeconds,
                    savedAtUtc = savedAtUtc ?? "",
                }, _token, r => response = r);

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();
                done?.Invoke(false);
                yield break;
            }

            HasCloudSave = true;
            Changed?.Invoke();
            done?.Invoke(true);
        }

        /// <summary>
        /// Fetches the stored save. <paramref name="done"/> gets null when there is none — which
        /// is an ordinary answer for a new player, not a failure.
        /// </summary>
        public IEnumerator DownloadSave(Action<SaveGetResponse> done)
        {
            if (!IsSignedIn) { LastError = "unauthorised"; done?.Invoke(null); yield break; }

            SaveGetResponse response = null;
            yield return OnlineClient.Get<SaveGetResponse>("/save/get", _token, r => response = r);

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();
                done?.Invoke(null);
                yield break;
            }

            HasCloudSave = response.hasSave;
            Changed?.Invoke();
            done?.Invoke(response.hasSave ? response : null);
        }

        /// <summary>
        /// Asks whether a save exists, and for its description, WITHOUT downloading it.
        ///
        /// The title screen needs this to decide whether 이어하기 is offered, and a save is tens
        /// of kilobytes — opening a menu should not cost a save file.
        /// </summary>
        public IEnumerator FetchSaveInfo(Action<SaveGetResponse> done)
        {
            if (!IsSignedIn) { HasCloudSave = false; done?.Invoke(null); yield break; }

            SaveGetResponse response = null;
            yield return OnlineClient.Get<SaveGetResponse>("/save/info", _token, r => response = r);

            if (response == null || !response.ok)
            {
                LastError = response?.error ?? "bad_response";
                if (LastError == "unauthorised") SignOut();
                HasCloudSave = false;
                done?.Invoke(null);
                yield break;
            }

            HasCloudSave = response.hasSave;
            Changed?.Invoke();
            done?.Invoke(response.hasSave ? response : null);
        }

        /// <summary>
        /// Whether the account has a story save, as far as this device last heard.
        ///
        /// A cache, and deliberately not authoritative: it is what the menu draws from so that
        /// building a row does not start a network request. Refreshed by
        /// <see cref="FetchSaveInfo"/> and by every upload.
        /// </summary>
        public bool HasCloudSave { get; private set; }

        /// <summary>The token, for the socket handshake. Nothing else should need it.</summary>
        public string Token => _token;
    }
}
