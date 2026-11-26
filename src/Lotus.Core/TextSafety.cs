using System.Globalization;
using System.Text;

namespace Lotus.Core
{
    /// <summary>
    /// Cleans text that comes from a USB device. Whoever built the device chooses
    /// its name and serial number, so LOTUS treats them as hostile input: nothing
    /// in them may break a log line, hide part of a name, or run on for ever.
    /// </summary>
    public static class TextSafety
    {
        public const int MaxNameLength = 96;

        /// <summary>
        /// Removes control characters and the invisible characters that reorder or
        /// hide text, folds every kind of white space into single spaces, and cuts
        /// the result to a sensible length.
        /// </summary>
        public static string Clean(string text, int maxLength = MaxNameLength)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var result = new StringBuilder(text.Length);
            bool space = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    // Keep well-formed pairs (emoji and rare scripts) together.
                    if (result.Length + 2 > maxLength) break;
                    if (space && result.Length > 0) result.Append(' ');
                    space = false;
                    result.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }
                if (char.IsSurrogate(c)) continue; // half a pair is not a character

                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (char.IsWhiteSpace(c) || category == UnicodeCategory.LineSeparator ||
                    category == UnicodeCategory.ParagraphSeparator)
                {
                    space = true;
                    continue;
                }
                if (category == UnicodeCategory.Control || category == UnicodeCategory.Format ||
                    category == UnicodeCategory.PrivateUse || category == UnicodeCategory.OtherNotAssigned)
                {
                    continue;
                }
                if (result.Length >= maxLength) break;
                if (space && result.Length > 0) result.Append(' ');
                space = false;
                result.Append(c);
            }
            return result.ToString();
        }
    }
}
