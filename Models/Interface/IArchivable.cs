using System;

namespace ScholarFlow.Models.Interface
{
    public interface IArchivable
    {
        public bool IsArchived { get; set; }
        public DateTime? ArchivedAt { get; set; }
    }
}
