namespace PokeLab.Core
{
    /// <summary>
    /// The UI's sound vocabulary, expressed as a Core contract so the UI assembly can make
    /// noise without referencing the audio assembly.
    ///
    /// The audio worker implements this over <c>UiAudio</c> and registers it on
    /// <see cref="ServiceHub"/>; UI code resolves it with <c>TryGet</c> and treats absence
    /// as silence. Every method is fire-and-forget and must be safe to call every frame —
    /// throttling (the typewriter's every-nth-character rule, the navigate debounce) is the
    /// implementation's job, never the caller's.
    /// </summary>
    public interface IUiSoundBank
    {
        /// <summary>Cursor moved between options. Debounced by the implementation.</summary>
        void Navigate();

        /// <summary>A choice was committed.</summary>
        void Confirm();

        /// <summary>Backed out of a menu or dismissed a prompt.</summary>
        void Cancel();

        /// <summary>An action was refused — no PP, illegal target, empty bag.</summary>
        void Error();

        /// <summary>A panel or menu opened.</summary>
        void MenuOpen();

        /// <summary>A panel or menu closed.</summary>
        void MenuClose();

        /// <summary>
        /// One character was revealed by a typewriter. Call once per character; the
        /// implementation decides which characters actually sound.
        /// </summary>
        void TypewriterTick(char revealed);

        /// <summary>A new typewriter line began, so the tick cadence restarts cleanly.</summary>
        void BeginTypewriterLine();
    }
}
