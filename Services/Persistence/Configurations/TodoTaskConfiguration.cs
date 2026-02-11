using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Services.Persistence.Configurations
{
    public class TodoTaskConfiguration : IEntityTypeConfiguration<TodoTask>
    {
        public void Configure(EntityTypeBuilder<TodoTask> builder)
        {
            builder.ToTable("TodoTasks");

            builder.Property(t => t.Title).IsRequired().HasMaxLength(200);

            builder.Property(t => t.Description).HasMaxLength(2000);

            builder.Property(t => t.IsCompleted).HasDefaultValue(false);

            builder.Property(t => t.UrgencyScore).HasDefaultValue(0);

            builder
                .HasOne(t => t.Course)
                .WithMany(c => c.Tasks)
                .HasForeignKey(t => t.CourseId)
                .OnDelete(DeleteBehavior.Cascade);

            builder
                .HasOne(t => t.Category)
                .WithMany(c => c.Tasks)
                .HasForeignKey(t => t.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);

            builder
                .HasOne(t => t.ParentTask)
                .WithMany(p => p.SubTasks)
                .HasForeignKey(t => t.ParentTaskId)
                .OnDelete(DeleteBehavior.Cascade);

            builder.HasIndex(t => t.IsCompleted);
            builder.HasIndex(t => t.UrgencyScore);

            builder.HasIndex(t => t.DueDate);
        }
    }
}
