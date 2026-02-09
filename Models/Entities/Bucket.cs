using System;

namespace ScholarFlow.Models.Entities
{
    public class Bucket
    {
        public string Name { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public DateTime LastAccessed { get; set; }
    }
}
