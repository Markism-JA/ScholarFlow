using System;
using ScholarFlow.Models.Entities.Base;
using ScholarFlow.Models.Enums;

namespace ScholarFlow.Models.Entities
{
    public class TaskArtifact : Entity
    {
        public string FileName { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public bool IsDirectory { get; set; } = false;

        public ArtifactType Type { get; set; } = ArtifactType.Submission;

        public string? VersionLabel { get; set; } //Draft, Review Copy, Final Submission

        public int VersionNumber { get; set; } = 1;

        public DateTime CreatedAt { get; set; } = DateTime.Now;

        public Guid? GroupId { get; set; }

        public Guid TodoTaskId { get; set; }

        public TodoTask? TodoTask { get; set; }
    }
}
