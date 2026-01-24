using System.ComponentModel.DataAnnotations;
using ScholarFlow.Models.Entities.Base;

namespace ScholarFlow.Models.Entities
{
    public class User : Entity
    {
        public string Username { get; set; } = string.Empty;
        public string StudentEmail { get; set; } = string.Empty;
        public string StudentPassword { get; set; } = string.Empty;
    }
}
