using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Data.Configurations
{
    public class CourseConfiguration : IEntityTypeConfiguration<Course>
    {
        public void Configure(EntityTypeBuilder<Course> builder)
        {
            builder.ToTable("Courses");

            builder.Property(c => c.Title).IsRequired().HasMaxLength(100);

            builder.Property(c => c.ClassCode).IsRequired().HasMaxLength(20);

            builder.Property(c => c.DirectoryName).IsRequired().HasMaxLength(150);

            builder.Property(c => c.WebsiteCode).HasMaxLength(50);

            builder.Property(c => c.MasterPdfPath).HasMaxLength(500);

            // "Retake Logic": You can have CS101 in 2025 and CS101 in 2026.
            // BUT you cannot have two CS101s in the SAME semester.
            builder.HasIndex(c => new { c.ClassCode, c.SemesterId }).IsUnique();

            // If a WebsiteCode exists, it must be unique per semester too.
            builder
                .HasIndex(c => new { c.WebsiteCode, c.SemesterId })
                .IsUnique()
                .HasFilter("[WebsiteCode] IS NOT NULL");

            builder
                .HasOne(c => c.Semester)
                .WithMany(s => s.Courses)
                .HasForeignKey(c => c.SemesterId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(c => c.Tasks)
                .WithOne(t => t.Course)
                .HasForeignKey(t => t.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasMany(c => c.Materials)
                .WithOne(m => m.Course)
                .HasForeignKey(m => m.CourseId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }
}
