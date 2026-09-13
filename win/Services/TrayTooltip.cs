using System;
using System.Collections.Generic;
using Yinyue.Models;

namespace Yinyue.Services
{
    /// <summary>
    /// Composes the tray icon's tooltip: the one surface that is always there while the
    /// overlay is hidden. Same shape as the Mac menu-bar tooltip, on purpose — the track,
    /// then playing or paused, then the volume, then the sleep timer, joined with dots.
    ///
    /// Pure, so the suite can check it without a tray. The Windows tray caps the text at
    /// <see cref="Limit"/> characters and NotifyIcon throws rather than truncating past it,
    /// so a long title is shortened with an ellipsis before anything else is dropped: the
    /// title is the part that varies, and the state and volume are what the rest is for.
    /// </summary>
    public static class TrayTooltip
    {
        /// <summary>
        /// NotifyIcon.Text's maximum on .NET 8. The shell buffer is 128 characters; the
        /// classic .NET Framework limit was 63, and the suite verifies which one applies.
        /// </summary>
        public const int Limit = 127;

        private const string Separator = "  ·  ";

        public static string Compose(Track? track, bool playing, bool muted, double volume,
            TimeSpan? sleepRemaining, int limit = Limit)
        {
            string? trackText = track is null ? null : $"{track.Title} — {track.DisplayArtist}";

            string Build(string? trackPart)
            {
                var parts = new List<string> { "Yinyue" };
                if (trackPart is not null) parts.Add(trackPart);
                if (playing) parts.Add("playing");
                else if (track is not null) parts.Add("paused");
                parts.Add(muted ? "Muted" : $"Volume {Math.Round(volume * 100)}%");
                if (sleepRemaining is { } left && left > TimeSpan.Zero)
                    parts.Add($"Sleep in {SleepTimerService.Describe(left)}");
                return string.Join(Separator, parts);
            }

            string text = Build(trackText);
            if (text.Length <= limit || trackText is null) return text.Length <= limit ? text : text[..limit];

            // Too long: give the track whatever room the fixed parts leave, with an ellipsis.
            int fixedLength = Build(null).Length + Separator.Length;
            int room = limit - fixedLength;
            if (room < 4) return Build(null)[..Math.Min(limit, Build(null).Length)];

            return Build(trackText[..(room - 1)].TrimEnd() + "…");
        }
    }
}
