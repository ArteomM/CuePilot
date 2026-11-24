using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DesktopCapture
{
    /// <summary>
    /// Automatic language detection («--lang auto»). Each source (system sound, microphone, a media file) is recognised
    /// with detection on until the language is clear: two chunks agreeing, or the most frequent one after five chunks.
    /// From then on that source uses the locked language, so short chunks no longer drift between close languages
    /// (Russian and Ukrainian), and Russian gets its priming phrase again. Another speaker on the other source can
    /// lock onto another language: the user speaks Russian while the client speaks English.
    /// The language comes from the engine when it reports one (whisper-server verbose_json, whisper-cli log, an API
    /// that returns "language"), otherwise from the script of the recognised text.
    /// </summary>
    internal static class AutoLanguage
    {
        public const string Auto = "auto";

        private const int VotesToLock = 2;
        private const int MaxVotesBeforeLock = 5;

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Locked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<string>> Votes = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Language the engine reported for the chunk just recognised on this thread.</summary>
        [ThreadStatic]
        public static string LastDetected;

        private static readonly Dictionary<string, string> FullNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "en", ["russian"] = "ru", ["ukrainian"] = "uk", ["belarusian"] = "be", ["kazakh"] = "kk",
            ["uzbek"] = "uz", ["german"] = "de", ["french"] = "fr", ["spanish"] = "es", ["italian"] = "it",
            ["portuguese"] = "pt", ["polish"] = "pl", ["turkish"] = "tr", ["chinese"] = "zh", ["japanese"] = "ja",
            ["korean"] = "ko", ["arabic"] = "ar", ["hebrew"] = "he", ["hindi"] = "hi", ["armenian"] = "hy",
            ["georgian"] = "ka", ["azerbaijani"] = "az", ["dutch"] = "nl", ["czech"] = "cs", ["serbian"] = "sr",
            ["bulgarian"] = "bg", ["romanian"] = "ro", ["finnish"] = "fi", ["swedish"] = "sv", ["latvian"] = "lv",
            ["lithuanian"] = "lt", ["estonian"] = "et", ["tajik"] = "tg", ["kyrgyz"] = "ky", ["mongolian"] = "mn"
        };

        private static readonly Regex WhisperLogLanguage = new Regex(@"auto-detected language:\s*([a-z]{2,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static bool IsAuto(string language) =>
            string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), Auto, StringComparison.OrdinalIgnoreCase);

        public static string LockedFor(string source)
        {
            lock (Gate)
            {
                return Locked.TryGetValue(source ?? string.Empty, out string language) ? language : null;
            }
        }

        /// <summary>Counts one detection; returns the language when this vote locks the source, otherwise null.</summary>
        public static string Vote(string source, string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            source = source ?? string.Empty;
            lock (Gate)
            {
                if (Locked.ContainsKey(source))
                {
                    return null;
                }

                if (!Votes.TryGetValue(source, out List<string> votes))
                {
                    votes = new List<string>();
                    Votes[source] = votes;
                }

                votes.Add(language);
                var leader = votes.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
                if (leader.Count() >= VotesToLock || votes.Count >= MaxVotesBeforeLock)
                {
                    Locked[source] = leader.Key;
                    Votes.Remove(source);
                    return leader.Key;
                }

                return null;
            }
        }

        /// <summary>«russian» / «ru» / «RU» → «ru»; anything else → null.</summary>
        public static string Normalize(string language)
        {
            string value = (language ?? string.Empty).Trim().ToLowerInvariant();
            if (value.Length == 0 || value == Auto)
            {
                return null;
            }

            if (FullNames.TryGetValue(value, out string code))
            {
                return code;
            }

            return value.Length <= 3 && value.All(char.IsLetter) ? value : null;
        }

        public static string FromWhisperLog(string log)
        {
            Match match = WhisperLogLanguage.Match(log ?? string.Empty);
            return match.Success ? Normalize(match.Groups[1].Value) : null;
        }

        /// <summary>
        /// When the engine does not say: Cyrillic text with Ukrainian-only letters is Ukrainian, other Cyrillic is
        /// Russian, plain Latin letters are English. Short or mixed text decides nothing.
        /// </summary>
        public static string FromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            int cyrillic = 0, latin = 0, ukrainian = 0, other = 0;
            foreach (char c in text)
            {
                if (!char.IsLetter(c))
                {
                    continue;
                }

                if (c >= 'Ѐ' && c <= 'ӿ')
                {
                    cyrillic++;
                    if ("іїєґІЇЄҐ".IndexOf(c) >= 0)
                    {
                        ukrainian++;
                    }
                }
                else if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    latin++;
                }
                else
                {
                    other++;
                }
            }

            int letters = cyrillic + latin + other;
            if (letters < 12)
            {
                return null;
            }

            if (cyrillic >= letters * 0.8)
            {
                return ukrainian >= 2 ? "uk" : "ru";
            }

            return latin >= letters * 0.9 ? "en" : null;
        }
    }
}
