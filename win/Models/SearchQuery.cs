using System;
using System.Collections.Generic;
using System.Linq;

namespace Yinyue.Models
{
    /// <summary>
    /// What kinds of thing a search should return.
    ///
    /// A flags enum rather than a single choice because a prefix narrows what is asked for,
    /// and the absence of one means everything.
    /// </summary>
    [Flags]
    public enum SearchScope
    {
        Tracks = 1,
        Playlists = 2,
        Albums = 4,
        Collections = Playlists | Albums,
        All = Tracks | Playlists | Albums,
    }

    /// <summary>Where a search looks.</summary>
    public enum SearchTarget
    {
        /// <summary>Every configured source.</summary>
        Library,

        /// <summary>Only what is already queued. Never touches a source.</summary>
        Queue,
    }

    /// <summary>
    /// A search term plus whatever a leading prefix narrowed it to — <c>track:hells</c>
    /// searches songs only, <c>album:black</c> albums only, <c>fav:love</c> favourites,
    /// <c>queue:bells</c> the queue in hand.
    ///
    /// Parsing happens once, at the library boundary, so every source is handed the same
    /// interpretation rather than each re-deriving it from a raw string. A source is free to
    /// ignore a scope it cannot serve; it must not return things outside it.
    ///
    /// The three dimensions are deliberately separate rather than one enum: a prefix narrows
    /// *what kind* of thing, *whether it must be a favourite*, and *where to look*, and
    /// collapsing those would make every future combination a new enum member.
    /// </summary>
    public sealed class SearchQuery
    {
        /// <summary>How a prefix reshapes the query.</summary>
        private sealed record PrefixRule(
            SearchScope Scope,
            bool FavoritesOnly = false,
            SearchTarget Target = SearchTarget.Library,
            string Noun = "results");

        /// <summary>The prefixes recognised, and what each narrows to.</summary>
        private static readonly Dictionary<string, PrefixRule> Prefixes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["track"] = new(SearchScope.Tracks, Noun: "songs"),
                ["tracks"] = new(SearchScope.Tracks, Noun: "songs"),
                ["song"] = new(SearchScope.Tracks, Noun: "songs"),
                ["songs"] = new(SearchScope.Tracks, Noun: "songs"),

                ["playlist"] = new(SearchScope.Playlists, Noun: "playlists"),
                ["playlists"] = new(SearchScope.Playlists, Noun: "playlists"),

                ["album"] = new(SearchScope.Albums, Noun: "albums"),
                ["albums"] = new(SearchScope.Albums, Noun: "albums"),

                // Favourites narrow by flag, not by kind: a favourite can be a track or a
                // collection, and the user asking for "fav:" has not said which.
                ["fav"] = new(SearchScope.All, FavoritesOnly: true, Noun: "favourites"),
                ["favs"] = new(SearchScope.All, FavoritesOnly: true, Noun: "favourites"),
                ["favourite"] = new(SearchScope.All, FavoritesOnly: true, Noun: "favourites"),
                ["favourites"] = new(SearchScope.All, FavoritesOnly: true, Noun: "favourites"),
                ["favorite"] = new(SearchScope.All, FavoritesOnly: true, Noun: "favourites"),
                ["favorites"] = new(SearchScope.All, FavoritesOnly: true, Noun: "favourites"),

                // The queue holds tracks only, and is searched in memory.
                ["queue"] = new(SearchScope.Tracks, Target: SearchTarget.Queue, Noun: "queued tracks"),
                ["q"] = new(SearchScope.Tracks, Target: SearchTarget.Queue, Noun: "queued tracks"),
            };

        private SearchQuery(string term, PrefixRule rule, string? prefix)
        {
            Term = term;
            Scope = rule.Scope;
            FavoritesOnly = rule.FavoritesOnly;
            Target = rule.Target;
            Noun = rule.Noun;
            Prefix = prefix;
        }

        /// <summary>The search text with any prefix stripped.</summary>
        public string Term { get; }

        public SearchScope Scope { get; }

        /// <summary>Restrict to items the user has favourited.</summary>
        public bool FavoritesOnly { get; }

        public SearchTarget Target { get; }

        /// <summary>What to call these results when there are none. "songs", "albums".</summary>
        public string Noun { get; }

        /// <summary>The prefix as typed, or null. Used to explain an empty result.</summary>
        public string? Prefix { get; }

        /// <summary>True when a prefix narrowed the search.</summary>
        public bool IsScoped => Prefix != null;

        public bool Wants(SearchScope kind) => (Scope & kind) != 0;

        /// <summary>
        /// Nothing to search for yet. A prefix on its own is this: the user has declared
        /// intent but not typed a term, and searching for "" would return the whole library.
        /// </summary>
        public bool IsEmpty => string.IsNullOrWhiteSpace(Term);

        private static readonly PrefixRule Unscoped = new(SearchScope.All);

        /// <summary>
        /// Splits a raw search box value into a scope and a term.
        ///
        /// Only a prefix at the very start counts, and only one: <c>track:</c> is a scope but
        /// a colon later in the line is just a colon, because song titles contain them
        /// ("Alive: Remastered") and a search that silently reinterpreted those would be
        /// worse than one that never tried.
        /// </summary>
        public static SearchQuery Parse(string? raw)
        {
            string text = (raw ?? string.Empty).TrimStart();

            int colon = text.IndexOf(':');
            if (colon > 0)
            {
                string candidate = text[..colon];

                // A prefix is one word. "my track:" is a search, not a scope.
                if (!candidate.Any(char.IsWhiteSpace) &&
                    Prefixes.TryGetValue(candidate, out var rule))
                {
                    return new SearchQuery(
                        text[(colon + 1)..].Trim(), rule, candidate.ToLowerInvariant());
                }
            }

            return new SearchQuery(text.Trim(), Unscoped, null);
        }

        public override string ToString() => IsScoped ? $"{Prefix}:{Term}" : Term;
    }
}
