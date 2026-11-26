using System;
using System.Collections.Generic;
using System.IO;

namespace Lotus.Setup
{
    /// <summary>
    /// Works on the list Windows keeps under "PendingFileRenameOperations": pairs of
    /// (source, target), where an empty target means "delete the source at the next restart".
    /// </summary>
    internal static class PendingDeletes
    {
        /// <summary>
        /// Returns the list without the pairs whose source is the given folder or
        /// something inside it. A list that is not made of pairs is returned unchanged.
        /// </summary>
        public static string[] Without(string[] entries, string folder, out int removed)
        {
            removed = 0;
            if (entries == null || entries.Length == 0 || entries.Length % 2 != 0) return entries ?? new string[0];

            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var kept = new List<string>(entries.Length);
            for (int i = 0; i < entries.Length; i += 2)
            {
                if (IsUnder(entries[i], root))
                {
                    removed++;
                    continue;
                }
                kept.Add(entries[i]);
                kept.Add(entries[i + 1]);
            }
            return removed == 0 ? entries : kept.ToArray();
        }

        /// <summary>
        /// Sources look like \??\C:\Folder\file.exe, sometimes with a short marker
        /// such as *1 in front.
        /// </summary>
        private static bool IsUnder(string source, string root)
        {
            if (string.IsNullOrEmpty(source)) return false;
            int at = source.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            // Only a short prefix may come before the path. Anything longer means the
            // folder name merely appears somewhere inside another path.
            if (at < 0 || at > 8) return false;
            int end = at + root.Length;
            return end == source.Length || source[end] == Path.DirectorySeparatorChar;
        }
    }
}
