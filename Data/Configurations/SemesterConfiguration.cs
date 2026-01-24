using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Data.Configurations
{
    public class SemesterConfiguration : IEntityTypeConfiguration<Semester>
    {
        public void Configure(EntityTypeBuilder<Semester> builder)
        {
            builder.ToTable("Semesters");

            builder.Property(s => s.Name).IsRequired().HasMaxLength(100);

            builder.Property(s => s.Directory).IsRequired().HasMaxLength(50);

            builder
                .HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(s => s.Courses)
                .WithOne(c => c.Semester)
                .HasForeignKey(c => c.SemesterId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
