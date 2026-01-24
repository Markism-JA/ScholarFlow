using System;
using ScholarFlow.Models.Entities.Base;
using ScholarFlow.Models.Enums;

namespace ScholarFlow.Models.Entities
{
    public class CourseMaterial : Entity
    {
        public string FileName { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public MaterialType Category { get; set; } = MaterialType.Unknown;

        // Is this file new? (For that "New Scrape" notification)
        public bool IsNew { get; set; } = true;

        public Guid CourseId { get; set; }
        public Course? Course { get; set; }
    }
}
