using System.Collections.Generic;

namespace ScholarFlow.Models.Entities
{
    public class AppConfig
    {
        public string? LastOpenedBasinPath { get; set; }
        public List<Bucket> KnownBasin { get; set; } = [];
    }
}
