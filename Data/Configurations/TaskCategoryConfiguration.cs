using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Data.Configurations
{
    public class TaskCategoryConfiguration : IEntityTypeConfiguration<TaskCategory>
    {
        public void Configure(EntityTypeBuilder<TaskCategory> builder)
        {
            builder.ToTable("TaskCategories");

            builder.Property(c => c.Title).IsRequired().HasMaxLength(50);

            builder.Property(c => c.Description).HasMaxLength(255);

            builder.HasIndex(c => c.Title).IsUnique();

            builder
                .HasMany(c => c.Tasks)
                .WithOne(t => t.Category)
                .HasForeignKey(t => t.CategoryId)
                .OnDelete(DeleteBehavior.SetNull);
        }
    }
}
