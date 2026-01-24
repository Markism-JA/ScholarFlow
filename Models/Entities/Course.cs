using System;
using System.Collections.Generic;
using ScholarFlow.Models.Entities.Base;
using ScholarFlow.Models.Interface;

namespace ScholarFlow.Models.Entities
{
    public class Course : Entity, IArchivable
    {
        // UI Display: "Introduction to AI" (Editable)
        public string Title { get; set; } = string.Empty;

        // Logic ID: "CS101" (Required for uniqueness)
        public string ClassCode { get; set; } = string.Empty;

        // File System: "CS101 - Introduction to AI" (Sanitized & Stable)
        public string DirectoryName { get; set; } = string.Empty;

        // Scraper ID: "123456" (From NEO LMS)
        public string? WebsiteCode { get; set; }

        public string? MasterPdfPath { get; set; }

        public Guid SemesterId { get; set; }
        public Semester? Semester { get; set; }

        public ICollection<TodoTask> Tasks { get; set; } = [];

        public ICollection<CourseMaterial> Materials { get; set; } = [];

        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedAt { get; set; }
    }
}
