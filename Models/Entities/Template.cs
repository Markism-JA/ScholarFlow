using System;
using System.ComponentModel.DataAnnotations;
using ScholarFlow.Models.Entities.Base;
using ScholarFlow.Models.Interface;

namespace ScholarFlow.Models.Entities
{
    public class Template : Entity, IArchivable
    {
        public string Name { get; set; } = string.Empty;

        public string Category { get; set; } = "General";

        public string FilePath { get; set; } = string.Empty;

        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedAt { get; set; }
    }
}
