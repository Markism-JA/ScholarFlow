using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Data.Configurations
{
    public class TaskArtifactConfiguration : IEntityTypeConfiguration<TaskArtifact>
    {
        public void Configure(EntityTypeBuilder<TaskArtifact> builder)
        {
            builder.ToTable("TaskArtifacts");

            builder.Property(a => a.FileName).IsRequired().HasMaxLength(255);

            builder.Property(a => a.FilePath).IsRequired().HasMaxLength(500);

            builder.Property(a => a.VersionLabel).HasMaxLength(50);

            builder.Property(a => a.VersionNumber).HasDefaultValue(1);

            builder.Property(a => a.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");

            builder
                .HasOne(a => a.TodoTask)
                .WithMany(t => t.Artifacts)
                .HasForeignKey(a => a.TodoTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(a => a.TodoTaskId);

            builder.HasIndex(a => a.GroupId);
        }
    }
}
