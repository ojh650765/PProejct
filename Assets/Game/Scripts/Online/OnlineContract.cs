using System;
using UnityEngine;

namespace PokeLab.Online
{
    /// <summary>
    /// The wire shapes the game and the Worker agree on.
    ///
    /// Every type here is <c>[Serializable]</c> with public fields and no properties, because
    /// they go through <see cref="JsonUtility"/> — which is the only JSON this project has on
    /// the client, and which has three rules worth stating once rather than rediscovering per
    /// type: it ignores fields it has no member for <b>silently</b>, it cannot serialise a
    /// top-level array (hence the wrappers below), and it cannot serialise a dictionary at all.
    ///
    /// The names are therefore load-bearing in both directions. The Worker in
    /// <c>Server/pokelab-online/src</c> writes these exact keys; a rename on one side and not
    /// the other produces a request that parses into a struct of default values rather than an
    /// error, which is the single most expensive failure mode this file has.
    /// </summary>
    public static class OnlineContract
    {
        /// <summary>Bumped when a shape here changes incompatibly. The Worker refuses a mismatch.</summary>
        public const int Version = 1;
    }

    // --- Accounts ---------------------------------------------------------------------------

    /// <summary>
    /// One of the questions an account can be recovered by.
    ///
    /// The list is fixed and shipped in both binaries rather than fetched, because the id is
    /// what is stored against the account and a question that changed its id would lock every
    /// account created under the old one out of its own recovery.
    /// </summary>
    [Serializable]
    public struct SecurityQuestion
    {
        public string Id;
        public string PromptKo;
        public string PromptEn;

        public SecurityQuestion(string id, string ko, string en)
        {
            Id = id;
            PromptKo = ko;
            PromptEn = en;
        }
    }

    /// <summary>The questions offered at account creation.</summary>
    public static class SecurityQuestions
    {
        public static readonly SecurityQuestion[] All =
        {
            new SecurityQuestion("birthplace", "태어난 곳은 어디인가요?", "Where were you born?"),
            new SecurityQuestion("memory", "가장 기억에 남는 순간은?", "Your most memorable moment?"),
            new SecurityQuestion("nickname", "어릴 적 별명은?", "Your childhood nickname?"),
            new SecurityQuestion("pet", "처음 키운 동물의 이름은?", "Your first pet's name?"),
            new SecurityQuestion("food", "가장 좋아하는 음식은?", "Your favourite food?"),
            new SecurityQuestion("school", "처음 다닌 학교 이름은?", "Your first school's name?"),
        };

        public static bool IsKnown(string id)
        {
            for (var i = 0; i < All.Length; i++) if (All[i].Id == id) return true;
            return false;
        }

        public static string PromptFor(string id, bool korean)
        {
            for (var i = 0; i < All.Length; i++)
                if (All[i].Id == id) return korean ? All[i].PromptKo : All[i].PromptEn;
            return id ?? "";
        }
    }

    [Serializable]
    public sealed class AccountRequest
    {
        public int version = OnlineContract.Version;
        public string trainerName;
        public string questionId;
        public string answer;
    }

    [Serializable]
    public sealed class AccountResponse
    {
        public bool ok;
        public string error;
        public string accountId;
        public string token;
        public string trainerName;
        /// <summary>True when this account has not yet drawn anything.</summary>
        public bool needsGacha;

        /// <summary>
        /// What the account can afford, as the SERVER counts it.
        ///
        /// Sent on every sign-in because the client cannot remember it across one: the gacha
        /// panel used to count its own draws, so signing back in handed a spent account a full
        /// allowance. These replaced rollsUsed/rollsMax when the lifetime cap gave way to a
        /// price -- a collection you may only draw five times is not a collection.
        /// </summary>
        public int coins;
        public int freePulls;
        public int pullCost;
    }

    // --- Roster -----------------------------------------------------------------------------

    /// <summary>
    /// One creature the account owns. The species id is the GAME id (SliceRoster), the same
    /// space <c>StageCreature</c> beats and <c>CreatureFactory</c> use — not the national dex
    /// number, which is the one mistake in this project that produces a plausible wrong
    /// creature rather than an error.
    /// </summary>
    [Serializable]
    public sealed class RosterEntry
    {
        public int speciesId;
        public int level;
        public int experience;
        public string rarity;

        /// <summary>
        /// Position in the COLLECTION, and the creature's identity for the whole of its life.
        ///
        /// It used to be 0-5 and mean "position in the team"; it is now an ever-growing index,
        /// and <see cref="partySlot"/> carries the meaning it gave up. Everything that changes
        /// a creature -- 강화, 돌파, a disc, a candy -- names it by this.
        /// </summary>
        public int slot;

        /// <summary>0-5 for a party member, -1 for one on the bench.</summary>
        public int partySlot = -1;

        /// <summary>돌파 level, 0-5. Raises the level cap and the stat bonus together.</summary>
        public int stars;

        /// <summary>Duplicate pulls of this species, spent on 돌파.</summary>
        public int shards;

        /// <summary>
        /// The four moves, comma separated, or empty for "whatever the level-up learnset gives
        /// at this level".
        ///
        /// Empty is the normal state and stays that way until a disc is taught. Both runtimes
        /// derive the same four from the same learnset until then, so writing them down any
        /// earlier would only be a second place for them to disagree.
        /// </summary>
        public string moves = "";

        public bool InParty => partySlot >= 0;
    }

    /// <summary>One kind of thing in the bag. <c>itemId</c> is "candy" or "disc:&lt;moveId&gt;".</summary>
    [Serializable]
    public sealed class OwnedItem
    {
        public string itemId;
        public int count;
    }

    /// <summary>
    /// Everything a screen needs to draw the account.
    ///
    /// The collection, the purse and the bag arrive together because every screen that wants
    /// one wants all three: 내 포켓몬 prices 강화 against the coins, 돌파 against the shards on
    /// the row, and 기술 against the discs in the bag. Every growth route answers with this
    /// same shape for the same reason -- three round trips to draw one list is three chances to
    /// show a number that has already changed.
    /// </summary>
    [Serializable]
    public sealed class RosterResponse
    {
        public bool ok;
        public string error;
        public RosterEntry[] roster;
        public OwnedItem[] items;
        public int coins;
        public int freePulls;
        public int pullCost;
        public int maxPulls;

        /// <summary>Which collection slot a growth call acted on. Unset by /roster.</summary>
        public int slot = -1;
        public int spent;
        public int levelsGained;
        public int stars;
    }

    // --- Gacha ------------------------------------------------------------------------------

    [Serializable]
    public sealed class GachaRequest
    {
        public int version = OnlineContract.Version;

        /// <summary>
        /// How many to draw. One to ten; the server clamps.
        ///
        /// It used to be six and only six, because the gacha drew a team. It draws into a
        /// collection now -- 무조건 6개가 아니라, 1개도 뽑을 수도 있고 ~n개를 뽑기 가능한거지 --
        /// so the count is the player's, and it is the only field left in this request.
        /// </summary>
        public int pulls = 1;
    }

    /// <summary>
    /// One pull, in the order the server drew it — which is the order the presentation reveals
    /// them in. The rarity is the server's word, not something the client derives from the
    /// stats, so the reveal and the odds can never disagree.
    /// </summary>
    [Serializable]
    public sealed class GachaPull
    {
        public int speciesId;
        public int level;
        public string rarity;
        /// <summary>0-4. Higher is rarer; drives how loud the reveal is.</summary>
        public int rarityRank;

        /// <summary>
        /// True when the account already owned this species and the pull became a shard.
        ///
        /// Not a disappointment to be hidden: shards are the only way to 돌파, so the reveal
        /// says so plainly and shows the piece it turned into.
        /// </summary>
        public bool duplicate;

        /// <summary>The collection slot this landed on, new or existing.</summary>
        public int slot;
    }

    [Serializable]
    public sealed class GachaResponse
    {
        public bool ok;
        public string error;
        public GachaPull[] pulls;
        public RosterEntry[] roster;
        public OwnedItem[] items;

        /// <summary>What the purse holds after this roll, and what the next one costs.</summary>
        public int coins;
        public int freePulls;
        public int pullCost;
        public int maxPulls;
        public int spent;
    }

    // --- Battle results ---------------------------------------------------------------------

    /// <summary>What the client claims happened, per creature that took part.</summary>
    [Serializable]
    public sealed class BattleParticipant
    {
        public int slot;
        public bool fainted;
    }

    [Serializable]
    public sealed class BattleResultRequest
    {
        public int version = OnlineContract.Version;
        /// <summary>"ai" or "pvp". The server pays differently for each.</summary>
        public string mode = "ai";
        public bool won;
        /// <summary>The match this result belongs to, for pvp. Empty for ai.</summary>
        public string matchId = "";
        public BattleParticipant[] participants;
    }

    /// <summary>What one creature gained. The server owns the curve; the client only shows it.</summary>
    [Serializable]
    public sealed class ExperienceGain
    {
        public int slot;
        public int speciesId;
        public int experienceGained;
        public int experience;
        public int level;
        public int levelsGained;

        /// <summary>
        /// True when this creature is sitting on the ceiling its 돌파 level allows.
        ///
        /// Said out loud on the summary, because otherwise a run of battles that visibly change
        /// nothing reads as the result never having been reported. The experience is banked,
        /// not lost -- it arrives as levels the moment the ceiling moves.
        /// </summary>
        public bool capped;
    }

    [Serializable]
    public sealed class BattleResultResponse
    {
        public bool ok;
        public string error;
        public ExperienceGain[] gains;

        /// <summary>
        /// What the battle paid. Both outcomes pay -- 대전에서 승리하거나 패배할때 코인 지급 --
        /// and a loss pays less rather than nothing.
        /// </summary>
        public int coinsGained;
        public int coins;

        /// <summary>
        /// What the battle dropped, as item ids: "candy" or "disc:&lt;moveId&gt;". Usually empty;
        /// "가끔식 획득가능하게" is the whole specification for how often.
        /// </summary>
        public string[] drops;
        public OwnedItem[] items;
    }

    // --- Growth -------------------------------------------------------------------------------

    /// <summary>
    /// Names one creature in the collection, by its collection slot.
    ///
    /// 강화, 돌파 and 사탕 need nothing else: what they cost and what they grant is the server's
    /// to decide, and a request that carried a price would be a request proposing one.
    /// </summary>
    [Serializable]
    public sealed class CreatureRequest
    {
        public int version = OnlineContract.Version;
        public int slot;
    }

    /// <summary>
    /// Teaches a disc into one of the four move slots.
    ///
    /// The server checks the species' learnset before it spends the disc -- 막 모든 포켓몬이 막
    /// 모든 디스크를 배울 수 있다 X -- and answers "cannot_learn" with the disc still in the bag.
    /// The client checks the same thing first, so the refusal is normally a greyed-out row
    /// rather than a round trip; the server's check is the one that counts, because a taught
    /// move walks into a PvP match.
    /// </summary>
    [Serializable]
    public sealed class TeachRequest
    {
        public int version = OnlineContract.Version;
        public int slot;
        public int moveSlot;
        public string moveId;
    }

    /// <summary>Which six of the collection fight, in party order, by collection slot.</summary>
    [Serializable]
    public sealed class PartyRequest
    {
        public int version = OnlineContract.Version;
        public int[] slots;
    }

    // --- Matchmaking ------------------------------------------------------------------------

    [Serializable]
    public sealed class MatchTicketResponse
    {
        public bool ok;
        public string error;
        /// <summary>The room to open a socket against once one has been assigned.</summary>
        public string matchId;
        public string socketUrl;
        /// <summary>"queued" while waiting, "matched" once an opponent is in the room.</summary>
        public string state;
        public string opponentName;
        public RosterEntry[] opponentRoster;
    }

    // --- Cloud save -------------------------------------------------------------------------

    /// <summary>
    /// A story save on its way up.
    ///
    /// <c>payload</c> is the save file VERBATIM — the same JSON <c>SaveSystem</c> writes — and
    /// the Worker stores it without parsing it. The fields beside it are pulled out of that
    /// same JSON by the client purely so the server can index them; they are a description of
    /// the payload, never a second source of truth for it.
    /// </summary>
    [Serializable]
    public sealed class SavePutRequest
    {
        public int version = OnlineContract.Version;
        public string payload;
        public int saveVersion;
        public string trainerName;
        public float playTimeSeconds;
        public string savedAtUtc;
    }

    [Serializable]
    public sealed class SavePutResponse
    {
        public bool ok;
        public string error;
        public long savedAt;
        public long uploadedAt;
    }

    /// <summary>
    /// What the cloud holds. <c>hasSave</c> false is a NORMAL answer, not a failure — a player
    /// who has never pressed 리포트 anywhere is in a perfectly ordinary state.
    ///
    /// <c>payload</c> is empty on the /save/info route, which exists so a menu can ask whether
    /// there is a save without paying to download one.
    /// </summary>
    [Serializable]
    public sealed class SaveGetResponse
    {
        public bool ok;
        public string error;
        public bool hasSave;
        public string payload;
        public int saveVersion;
        public string trainerName;
        public float playTimeSeconds;
        public long savedAt;
        public long uploadedAt;
    }
}
