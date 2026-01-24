using System;
using System.ComponentModel.DataAnnotations;

namespace ScholarFlow.Models.Entities.Base
{
    public class Entity
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
