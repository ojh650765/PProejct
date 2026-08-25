using System;
using PokeLab.Core;
using PokeLab.Online;
using UnityEngine;

namespace PokeLab.Boot
{
    /// <summary>
    /// Turns a finished story battle into coins in the account's purse.
    ///
    /// <b>What this is for.</b> 스토리에서 얻는 코인이랑 아웃게임에서도 사용가능해야함. Until now the
    /// overworld kept its own wallet -- <c>PlayerProfile.Money</c>, starting at 3000, earned from
    /// trainers, halved on a whiteout -- and nothing anywhere spent it. It was a number that could
    /// only be looked at. Meanwhile every pull, every 강화 and every 돌파 spent a different number
    /// that only battle mode could earn. Two currencies, one of them dead.
    ///
    /// <b>Why the amount is not on the wire.</b> The overworld says what happened -- won or lost,
    /// trainer or wild -- and the Worker prices it. A client that could name its own reward would
    /// be minting the currency that buys gacha pulls, and those creatures walk into PvP against
    /// somebody else's afternoon. The tier is the only thing the client gets to assert, and it is
    /// two values wide.
    ///
    /// <b>Why it listens instead of being called.</b> PokeLab.Overworld references Core and nothing
    /// else, on purpose: the world has to be playable and testable with no account, no network and
    /// no login. So the world raises a Core event and this -- which lives on the side of the fence
    /// that knows about the network -- decides what it costs. Signed out, nobody is listening and
    /// the story simply plays.
    ///
    /// <b>Failure is silent by design.</b> A dropped report costs the player some coins; a story
    /// that stops to complain about the network costs them the scene they were in. The next battle
    /// pays normally.
    /// </summary>
    public static class StoryCoinReporter
    {
        /// <summary>Modes the Worker prices in economy.ts. Wild is worth less; grass is endless.</summary>
        private const string WildMode = "story_wild";
        private const string TrainerMode = "story_trainer";

        private static bool _installed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (_installed) return;
            _installed = true;
            GameEvents.StoryBattleFinished += OnStoryBattleFinished;
        }

        private static void OnStoryBattleFinished(bool won, bool trainer)
        {
            var session = OnlineSession.Instance;
            if (session == null || !session.IsSignedIn) return;

            // No participants: the story party is the SAVE's creatures, not the collection's, so
            // there is nobody here the roster could pay experience to. The Worker knows that from
            // the mode and answers with coins alone.
            session.StartCoroutine(session.ReportBattle(
                trainer ? TrainerMode : WildMode, won, string.Empty,
                Array.Empty<BattleParticipant>(),
                reply =>
                {
                    if (reply == null)
                        Debug.Log("[StoryCoins] The battle result did not reach the server (" +
                                  (session.LastError ?? "unknown") + "). No coins for this one.");
                }));
        }
    }
}
