using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Services.Persistence.Configurations
{
    public class CourseMaterialConfiguration : IEntityTypeConfiguration<CourseMaterial>
    {
        public void Configure(EntityTypeBuilder<CourseMaterial> builder)
        {
            builder.ToTable("CourseMaterials");

            builder.Property(m => m.FileName).IsRequired().HasMaxLength(255);

            builder.Property(m => m.FilePath).IsRequired().HasMaxLength(500);

            builder.Property(m => m.Category).IsRequired();

            builder.Property(m => m.IsNew).HasDefaultValue(true);

            builder
                .HasOne(m => m.Course)
                .WithMany(c => c.Materials)
                .HasForeignKey(m => m.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(m => m.CourseId);

            builder.HasIndex(m => new { m.CourseId, m.FilePath }).IsUnique();
        }
    }
}
