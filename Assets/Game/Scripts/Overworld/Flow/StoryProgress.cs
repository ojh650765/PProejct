using PokeLab.Core;

namespace PokeLab.Overworld
{
    public static class StoryProgress
    {
        public const string HomeReady = "story.home_ready";
        public const string CaptureLearned = "story.capture_learned";
        public static bool Has(string flag) => string.IsNullOrEmpty(flag) ||
            (ServiceHub.TryGet<IPlayerProfile>(out var profile) && profile is PlayerProfile p && p.GetFlagBool(flag));

        public static string ItemName(string id) => id switch
        {
            "journal" => Loc.Pick("Journal", "모험노트"),
            "parcel" => Loc.Pick("Parcel for Barry", "용식에게 전해줄 물건"),
            "poke-ball" => Loc.Pick("Poké Ball", "몬스터볼"),
            _ => id
        };

        public static string Journal()
        {
            var text = Loc.Pick("Journal\nReceived a Pokédex from Professor Rowan.", "모험노트\n마박사에게 포켓몬도감을 받았다.");
            if (Has("story.home_journal")) text += Loc.Pick("\nTold Mom about the journey.", "\n엄마에게 여행 이야기를 드렸다.");
            if (Has(HomeReady)) text += Loc.Pick("\nCarrying a parcel for Barry in Jubilife City.", "\n축복시티로 간 용식에게 물건을 전해주기로 했다.");
            if (Has(CaptureLearned)) text += Loc.Pick("\nWatched the catching lesson on Route 202.", "\n202번도로에서 포켓몬 잡는 방법을 배웠다.");
            return text;
        }
    }
}
