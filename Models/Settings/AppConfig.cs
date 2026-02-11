using System.Collections.Generic;
using ScholarFlow.Models.DTOs;

namespace ScholarFlow.Models.Settings
{
    public class AppConfig
    {
        public string? LastOpenedBasinPath { get; set; }
        public List<KnowBasinEntry> KnownBasin { get; set; } = [];
    }
}
