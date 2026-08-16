using System.Text;

namespace PokeLab.Core
{
    /// <summary>
    /// Korean particles that agree with the word in front of them.
    /// </summary>
    /// <remarks>
    /// Korean picks 은/는, 이/가, 을/를 by whether the preceding syllable ends in a consonant.
    /// Writing "이(가)" everywhere is what a machine translation looks like, and writing one
    /// of the pair regardless is worse — "피카츄이 쓰러졌다" is simply wrong. Since every one
    /// of these lines is assembled from a creature's name at runtime, the choice cannot be
    /// authored into the string and has to be made here.
    ///
    /// Hangul syllables are laid out so the final consonant is recoverable by arithmetic:
    /// the block starts at U+AC00 and each initial-medial pair is followed by its 28 finals,
    /// the first of which is "no final". Anything outside that block — a Latin name, a digit —
    /// falls back to the vowel form, which is what Korean does when reading a foreign word
    /// aloud, and is right for "Pikachu가".
    /// </remarks>
    public static class Josa
    {
        private const char HangulStart = '가';
        private const char HangulEnd = '힣';
        private const int FinalCount = 28;

        /// <summary>True when the last syllable of <paramref name="word"/> ends in a consonant.</summary>
        public static bool HasFinalConsonant(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;

            // Trailing punctuation and spaces are not what the particle agrees with — a name
            // in brackets or followed by a full stop still ends in its own last letter.
            var index = word.Length - 1;
            while (index >= 0 && !char.IsLetterOrDigit(word[index])) index--;
            if (index < 0) return false;

            var c = word[index];
            if (c >= HangulStart && c <= HangulEnd) return (c - HangulStart) % FinalCount != 0;

            // Latin and digits: judged by how the syllable is read in Korean. These are the
            // endings that are pronounced with a final consonant — "Pikachu" is not one of
            // them, "Sandshrew"'s 'w' is not either, but "Charmander"'s 'r' is.
            if (char.IsDigit(c))
                return c == '0' || c == '1' || c == '3' || c == '6' || c == '7' || c == '8';

            switch (char.ToLowerInvariant(c))
            {
                case 'l': case 'm': case 'n': case 'r': case 'g': case 'k':
                case 'b': case 'p': case 't': case 'd': case 'c': case 'x': case 's':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>은/는.</summary>
        public static string Topic(string word) => HasFinalConsonant(word) ? "은" : "는";

        /// <summary>이/가.</summary>
        public static string Subject(string word) => HasFinalConsonant(word) ? "이" : "가";

        /// <summary>을/를.</summary>
        public static string Object(string word) => HasFinalConsonant(word) ? "을" : "를";

        /// <summary>으로/로.</summary>
        public static string Direction(string word)
        {
            if (string.IsNullOrEmpty(word)) return "로";

            // ㄹ is the exception: it takes 로 like a vowel does, which is why this cannot
            // simply reuse HasFinalConsonant.
            var index = word.Length - 1;
            while (index >= 0 && !char.IsLetterOrDigit(word[index])) index--;
            if (index < 0) return "로";

            var c = word[index];
            if (c >= HangulStart && c <= HangulEnd)
            {
                var final = (c - HangulStart) % FinalCount;
                return final == 0 || final == 8 ? "로" : "으로";
            }
            return HasFinalConsonant(word) ? "으로" : "로";
        }

        /// <summary>Word plus its 은/는.</summary>
        public static string WithTopic(string word) => word + Topic(word);

        /// <summary>Word plus its 이/가.</summary>
        public static string WithSubject(string word) => word + Subject(word);

        /// <summary>Word plus its 을/를.</summary>
        public static string WithObject(string word) => word + Object(word);
    }
}
