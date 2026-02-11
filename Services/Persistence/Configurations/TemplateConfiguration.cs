using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ScholarFlow.Models.Entities;

namespace ScholarFlow.Services.Persistence.Configurations
{
    public class TemplateConfiguration : IEntityTypeConfiguration<Template>
    {
        public void Configure(EntityTypeBuilder<Template> builder)
        {
            builder.ToTable("Templates");

            builder.Property(t => t.Name).IsRequired().HasMaxLength(100);

            builder.Property(t => t.Category).IsRequired().HasMaxLength(50);

            builder.Property(t => t.FilePath).IsRequired().HasMaxLength(500);

            builder.HasIndex(t => t.Category);

            builder.HasIndex(t => t.Name).IsUnique();
        }
    }
}
