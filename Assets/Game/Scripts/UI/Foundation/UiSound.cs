using PokeLab.Core;

namespace PokeLab.UI
{
    /// <summary>
    /// The UI's one route to interaction sounds.
    ///
    /// Every menu in this assembly wants the same six clicks — cursor moved, choice taken,
    /// backed out, refused, menu opened, menu closed — and the bank that owns the actual
    /// clips lives behind <see cref="IUiSoundBank"/> in Core, registered by the audio worker.
    /// If each view resolved the bank itself, every one of them would repeat the same
    /// TryGet-and-null-check dance, and the day the bank's registration moves in the boot
    /// order every view would need the same fix. This wrapper is that dance written once.
    ///
    /// Absence is silence by design, exactly as <see cref="UiServices"/> treats a missing
    /// engine: the UI is built in parallel with the audio layer, so for most of integration
    /// there is no bank at all, and a menu that throws — or even logs — because navigation
    /// cannot click would make the UI untestable on its own. The cached reference re-resolves
    /// on demand because boot order registers services after the first UI Awake in some
    /// scenes; once found it is held, because a dictionary lookup per cursor tick during a
    /// held arrow key is a silly price for a sound.
    /// </summary>
    public static class UiSound
    {
        private static IUiSoundBank _bank;

        private static IUiSoundBank Bank
        {
            get
            {
                if (_bank == null) ServiceHub.TryGet(out _bank);
                return _bank;
            }
        }

        /// <summary>The cursor moved to another entry.</summary>
        public static void Navigate() => Bank?.Navigate();

        /// <summary>A choice was taken.</summary>
        public static void Confirm() => Bank?.Confirm();

        /// <summary>The player backed out one level.</summary>
        public static void Cancel() => Bank?.Cancel();

        /// <summary>The press was refused — a disabled control, an illegal pick.</summary>
        public static void Error() => Bank?.Error();

        /// <summary>A menu surface appeared.</summary>
        public static void MenuOpen() => Bank?.MenuOpen();

        /// <summary>A menu surface went away.</summary>
        public static void MenuClose() => Bank?.MenuClose();

        /// <summary>Drops the cached bank. Call alongside <see cref="ServiceHub.Reset"/>.</summary>
        public static void Reset() => _bank = null;
    }
}
