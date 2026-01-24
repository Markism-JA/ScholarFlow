using System.Collections.Generic;
using ScholarFlow.Models.Entities.Base;

namespace ScholarFlow.Models.Entities
{
    public class TaskCategory : Entity
    {
        public string Title { get; set; } = string.Empty;

        public string? Description { get; set; }

        public ICollection<TodoTask> Tasks { get; set; } = [];
    }
}
