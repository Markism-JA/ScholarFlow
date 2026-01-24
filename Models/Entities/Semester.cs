using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ScholarFlow.Models.Entities.Base;
using ScholarFlow.Models.Enums;
using ScholarFlow.Models.Interface;

namespace ScholarFlow.Models.Entities
{
    public class Semester : Entity, IArchivable
    {
        public string Name { get; set; } = string.Empty;

        public SemesterStatus Status { get; set; } = SemesterStatus.Ongoing;

        public string Directory { get; set; } = string.Empty;

        public Guid UserId { get; set; }

        public User? User { get; set; }

        public ICollection<Course> Courses { get; set; } = [];
        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedAt { get; set; }
    }
}
