namespace PokeLab.Core
{
    /// <summary>
    /// Every sentence the battle engine narrates, in one place.
    ///
    /// <b>Why they are not in the engine.</b> They were, and they were English — all thirty-five
    /// of them, written as interpolated literals next to the rules that emit them. In a Korean
    /// build the log came out half-translated: the lines the UI layer wrote were Korean, the ones
    /// the engine wrote were English, and because the creature's own name resolves through the
    /// species registry it landed in Korean inside an English sentence. "It doesn't affect 팬텀."
    /// A player reads that as the game being broken, and they are not wrong.
    ///
    /// This follows what PokeLabStrings already does for the scanner: the prose lives somewhere it
    /// can be read end to end and reviewed as writing, and the engine stays about rules.
    ///
    /// <b>Register.</b> These are the battle log, not dialogue — no speaker, no personality, the
    /// flat declarative past the games print. Modelled on the Korean DP script's own wording
    /// rather than translated from the English above them, which is why 풀이 죽어, 쿨쿨,
    /// 몸이 저려서 read the way they do.
    ///
    /// <b>Particles.</b> Every name spliced into a sentence goes through <see cref="Josa"/>. The
    /// player nicknames their creatures, so 는/은 and 를/을 cannot be authored into the string:
    /// 팬텀은 and 피카츄는 are both correct and no single literal is.
    /// </summary>
    public static class BattleLogStrings
    {
        // --- Attacks that did not land ----------------------------------------------------

        public static string Protected(string who) =>
            Loc.Pick($"{who} protected itself!", $"{Josa.WithTopic(who)} 몸을 지켰다!");

        public static string Missed(string who) =>
            Loc.Pick($"{who}'s attack missed!", $"{who}의 공격은 빗나갔다!");

        public static string NoEffectOn(string who) =>
            Loc.Pick($"It doesn't affect {who}.", $"{who}에게는 효과가 없는 것 같다…");

        /// <summary>No target, no legality, nothing to do. The games' 그러나 line.</summary>
        public static string NothingHappenedEmphatic =>
            Loc.Pick("But nothing happened!", "그러나 아무 일도 일어나지 않았다!");

        public static string Failed =>
            Loc.Pick("But it failed!", "그러나 실패했다!");

        public static string NothingHappened =>
            Loc.Pick("Nothing happened.", "아무 일도 일어나지 않았다.");

        public static string NoEffect =>
            Loc.Pick("It had no effect.", "효과가 없는 것 같다…");

        // --- Status, and being stopped by it ----------------------------------------------

        public static string Confused(string who) =>
            Loc.Pick($"{who} became confused!", $"{Josa.WithTopic(who)} 혼란에 빠졌다!");

        public static string IsConfused(string who) =>
            Loc.Pick($"{who} is confused!", $"{Josa.WithTopic(who)} 혼란에 빠져 있다!");

        public static string ConfusionEnded(string who) =>
            Loc.Pick($"{who} snapped out of its confusion!", $"{who}의 혼란이 풀렸다!");

        public static string HurtByConfusion =>
            Loc.Pick("It hurt itself in its confusion!", "혼란에 빠져 자신을 공격했다!");

        public static string Seeded(string who) =>
            Loc.Pick($"{who} was seeded!", $"{who}에게 씨가 뿌려졌다!");

        public static string Flinched(string who) =>
            Loc.Pick($"{who} flinched!", $"{Josa.WithTopic(who)} 풀이 죽어 움직일 수 없었다!");

        public static string Thawed(string who) =>
            Loc.Pick($"{who} thawed out!", $"{who}의 얼음 상태가 나았다!");

        public static string FrozenSolid(string who) =>
            Loc.Pick($"{who} is frozen solid!", $"{Josa.WithTopic(who)} 얼어붙어서 움직일 수 없다!");

        public static string Asleep(string who) =>
            Loc.Pick($"{who} is fast asleep.", $"{Josa.WithTopic(who)} 쿨쿨 잠들어 있다.");

        public static string WokeUp(string who) =>
            Loc.Pick($"{who} woke up!", $"{Josa.WithTopic(who)} 잠에서 깼다!");

        public static string Paralysed(string who) =>
            Loc.Pick($"{who} is paralysed and can't move!", $"{Josa.WithTopic(who)} 몸이 저려서 움직일 수 없다!");

        public static string StatusHealed(string who, StatusCondition status) =>
            Loc.Pick($"{who} shook off its {status}.", $"{who}의 {StatusName(status)}이(가) 나았다!");

        /// <summary>
        /// The Korean name of a status, as the games print it.
        ///
        /// Here rather than in the HUD so the pill on the health bar and the sentence in the log
        /// cannot come to disagree about what a status is called.
        /// </summary>
        public static string StatusName(StatusCondition status) => status switch
        {
            StatusCondition.Burn => Loc.Pick("burn", "화상"),
            StatusCondition.Freeze => Loc.Pick("freeze", "얼음"),
            StatusCondition.Paralysis => Loc.Pick("paralysis", "마비"),
            StatusCondition.Poison => Loc.Pick("poison", "독"),
            StatusCondition.BadPoison => Loc.Pick("bad poison", "맹독"),
            StatusCondition.Sleep => Loc.Pick("sleep", "잠듦"),
            StatusCondition.Fainted => Loc.Pick("fainting", "기절"),
            _ => Loc.Pick("nothing", "이상 없음"),
        };

        // --- Health, levels, the bench ----------------------------------------------------

        public static string Recovered(string who, int amount) =>
            Loc.Pick($"{who} recovered {amount} HP!", $"{Josa.WithTopic(who)} 체력을 {amount} 회복했다!");

        public static string GrewToLevel(string who, int level) =>
            Loc.Pick($"{who} grew to level {level}!", $"{Josa.WithTopic(who)} 레벨 {level}(으)로 올랐다!");

        public static string LevelChanged(string who) =>
            Loc.Pick($"{who} changed level.", $"{who}의 레벨이 바뀌었다.");

        public static string SentOutInstead(string who) =>
            Loc.Pick($"{who} was sent out instead!", $"대신 {Josa.WithObject(who)} 내보냈다!");

        // --- Running and catching ---------------------------------------------------------

        public static string NoRunningFromTrainer =>
            Loc.Pick("There's no running from a trainer battle!", "트레이너와의 승부에서는 도망칠 수 없다!");

        public static string GotAway =>
            Loc.Pick("Got away safely!", "무사히 도망쳤다!");

        public static string CouldNotGetAway =>
            Loc.Pick("Couldn't get away!", "도망칠 수 없었다!");

        public static string CannotCatchTrainers =>
            Loc.Pick("You can't catch another trainer's creature!", "다른 트레이너의 포켓몬은 잡을 수 없다!");

        public static string Caught(string who) =>
            Loc.Pick($"{who} was caught!", $"앗! {Josa.WithObject(who)} 잡았다!");

        public static string BrokeFree =>
            Loc.Pick("Oh no! It broke free!", "앗! 포켓몬이 튀어나왔다!");
    }
}
