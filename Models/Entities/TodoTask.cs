using System;
using System.Collections.Generic;
using ScholarFlow.Models.Entities.Base;
using ScholarFlow.Models.Enums;
using ScholarFlow.Models.Interface;

namespace ScholarFlow.Models.Entities
{
    public class TodoTask : Entity, IArchivable
    {
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; } = string.Empty;

        public bool IsCompleted { get; set; } = false;

        public DateTime? DueDate { get; set; }

        public TaskDifficulty Difficulty { get; set; } = TaskDifficulty.Medium;

        public int UrgencyScore { get; set; }

        public ICollection<TaskArtifact> Artifacts { get; set; } = [];

        public Guid? CourseId { get; set; }

        public Course? Course { get; set; }

        public Guid? CategoryId { get; set; }

        public TaskCategory? Category { get; set; }

        public Term TermClassification { get; set; } = Term.None;

        public Guid? ParentTaskId { get; set; }

        public TodoTask? ParentTask { get; set; }

        public ICollection<TodoTask> SubTasks { get; set; } = [];

        public bool IsArchived { get; set; } = false;
        public DateTime? ArchivedAt { get; set; }
    }
}
