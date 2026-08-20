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
        /// <summary>True when this account has not yet drawn its team.</summary>
        public bool needsGacha;
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
        public int slot;
    }

    [Serializable]
    public sealed class RosterResponse
    {
        public bool ok;
        public string error;
        public RosterEntry[] roster;
    }

    // --- Gacha ------------------------------------------------------------------------------

    [Serializable]
    public sealed class GachaRequest
    {
        public int version = OnlineContract.Version;
        /// <summary>How many to draw. The server clamps; a full team is six.</summary>
        public int pulls = 6;
        /// <summary>True to discard the current team and draw a fresh one.</summary>
        public bool reroll;
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
    }

    [Serializable]
    public sealed class GachaResponse
    {
        public bool ok;
        public string error;
        public GachaPull[] pulls;
        public RosterEntry[] roster;
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
    }

    [Serializable]
    public sealed class BattleResultResponse
    {
        public bool ok;
        public string error;
        public ExperienceGain[] gains;
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
