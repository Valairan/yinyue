using System;
using System.Collections.Generic;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Finds the queue entry a typed term most plausibly means. A queue search does not list
    /// hits; it brings the queue itself up with one entry highlighted, so there has to be a
    /// single answer, and "first entry that contains the letters" is often the wrong one when
    /// an artist's name appears on every row. Title matches outrank artist and album, and an
    /// exact or leading title match outranks a title that merely contains the term. Ties go
    /// to queue order, which is also play order — the earlier one plays sooner.
    /// </summary>
    public static class QueueSearch
    {
        /// <summary>Index into <paramref name="order"/> of the best match, or -1 for none.</summary>
        public static int BestMatch(IReadOnlyList<Track> order, string term)
        {
            if (string.IsNullOrWhiteSpace(term)) return -1;

            int best = -1, bestScore = 0;
            for (int i = 0; i < order.Count; i++)
            {
                int score = Score(order[i], term);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }
            return best;
        }

        private static int Score(Track track, string term)
        {
            const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
            if (track.Title.Equals(term, ignoreCase)) return 5;
            if (track.Title.StartsWith(term, ignoreCase)) return 4;
            if (track.Title.Contains(term, ignoreCase)) return 3;
            if (track.Artist.Contains(term, ignoreCase)) return 2;
            if (track.Album.Contains(term, ignoreCase)) return 1;
            return 0;
        }
    }
}
