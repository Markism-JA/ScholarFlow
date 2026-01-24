using System;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using ScholarFlow.Models.Entities;
using ScholarFlow.Models.Interface;

namespace ScholarFlow.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options)
            : base(options) { }

        public DbSet<User> Users { get; set; }
        public DbSet<Semester> Semesters { get; set; }
        public DbSet<Course> Courses { get; set; }
        public DbSet<CourseMaterial> CourseMaterials { get; set; }
        public DbSet<Template> Templates { get; set; }
        public DbSet<TodoTask> TodoTasks { get; set; }
        public DbSet<TaskCategory> TaskCategories { get; set; }
        public DbSet<TaskArtifact> TaskArtifacts { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());

            var method = typeof(AppDbContext).GetMethod(
                nameof(ConfigureArchivableFilter),
                BindingFlags.NonPublic | BindingFlags.Instance
            );

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (typeof(IArchivable).IsAssignableFrom(entityType.ClrType))
                {
                    var genericMethod = method?.MakeGenericMethod(entityType.ClrType);

                    genericMethod?.Invoke(this, [modelBuilder]);
                }
            }
        }

        private void ConfigureArchivableFilter<T>(ModelBuilder builder)
            where T : class, IArchivable
        {
            builder.Entity<T>().HasQueryFilter(e => !e.IsArchived);
        }
    }
}
