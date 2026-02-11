using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Services.Persistence.Configurations
{
    public class UserConfiguration : IEntityTypeConfiguration<User>
    {
        public void Configure(EntityTypeBuilder<User> builder)
        {
            builder.ToTable("Users");

            builder.HasKey(u => u.Id);

            builder.Property(u => u.Username).IsRequired().HasMaxLength(50);

            builder.Property(u => u.StudentEmail).IsRequired().HasMaxLength(255);

            builder.Property(u => u.StudentPassword).IsRequired();

            builder.HasIndex(u => u.StudentEmail).IsUnique();
        }
    }
}
