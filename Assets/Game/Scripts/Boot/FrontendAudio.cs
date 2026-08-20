using PokeLab.Audio;
using PokeLab.Core;

namespace PokeLab.Boot
{
    /// <summary>
    /// What the audio layer should be doing while a menu owns the screen.
    ///
    /// <b>The bug this exists for.</b> The AV directors are persistent and self-bootstrapping:
    /// <see cref="AvPresenterHost"/> stands one of each up in every scene, including the two
    /// that are not places. Both of them decided what to play from a biome that defaulted to
    /// the route, and the world clock goes on ticking under a menu — so the first
    /// <c>TimeOfDayChanged</c> after boot had the music director ask for the route's daytime
    /// theme and the ambience director bring up birdsong and wind in grass, over the login
    /// form. The user reported it twice, as 메인 메뉴에서 새소리 and as 로그인 화면과
    /// 메인메뉴에서 스토리모드의 배경음악이 들림.
    ///
    /// The directors now start nowhere rather than on the route, which fixes the first screen
    /// of a session by itself. This call is what fixes the SECOND visit: coming back to the
    /// title from a save, the directors remember the town and would keep playing it under the
    /// menu. Telling them the world is gone is the caller's job because only the caller knows
    /// it is a menu.
    ///
    /// Ordering matters and is the reason this is one function rather than two calls at each
    /// site: the world is dropped first, then the menu's own track is asked for, so the
    /// crossfade goes straight from the town theme to the title piece instead of through a
    /// hole. Resolved through <see cref="ServiceHub"/> and silently skipped when a director is
    /// absent — a scene run on its own from the editor has no AV layer and must still open.
    /// </summary>
    internal static class FrontendAudio
    {
        /// <summary>Seconds. Long, because the menu's rows fly in over about that long and a
        /// track that arrives at full volume on frame one steps on them.</summary>
        private const float Fade = 1.4f;

        public static void TakeOver(string track)
        {
            if (ServiceHub.TryGet<AmbienceDirector>(out var ambience)) ambience.LeaveWorld();

            if (!ServiceHub.TryGet<MusicDirector>(out var music)) return;
            music.LeaveWorld();

            if (!string.IsNullOrEmpty(track)) music.PlayTrack(track, Fade);
        }
    }
}
