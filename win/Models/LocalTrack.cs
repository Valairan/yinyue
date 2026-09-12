using System;

namespace Yinyue.Models
{
    public class LocalTrack
    {
        public int Id { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Artist { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;
        public double DurationSeconds { get; set; }
        public int Year { get; set; }
        public string Genre { get; set; } = string.Empty;
        public DateTime IndexedAt { get; set; }
    }
}