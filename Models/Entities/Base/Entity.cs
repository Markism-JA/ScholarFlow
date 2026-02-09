using System;
using System.ComponentModel.DataAnnotations;

namespace ScholarFlow.Models.Entities.Base
{
    public class Entity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }
}
