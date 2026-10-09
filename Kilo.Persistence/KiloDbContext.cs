using Kilo.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Kilo.Persistence;

public sealed class KiloDbContext(DbContextOptions<KiloDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<Routine> Routines => Set<Routine>();
    public DbSet<RoutineExercise> RoutineExercises => Set<RoutineExercise>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema("public");
        var user = model.Entity<User>();
        user.ToTable("users", table =>
        {
            table.HasCheckConstraint("users_clerk_user_id_check", "btrim(clerk_user_id) <> ''");
            table.HasCheckConstraint("users_measurement_system_check", "measurement_system IN ('imperial', 'metric')");
        });
        user.HasKey(x => x.Id).HasName("users_pkey");
        user.HasAlternateKey(x => x.ClerkUserId).HasName("users_clerk_user_id_key");
        user.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        user.Property(x => x.ClerkUserId).HasColumnName("clerk_user_id").HasColumnType("text");
        user.Property(x => x.TimeZone).HasColumnName("time_zone").HasColumnType("text").HasDefaultValue("UTC");
        user.Property(x => x.MeasurementSystem).HasColumnName("measurement_system").HasColumnType("text").HasDefaultValue("imperial");
        user.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");

        var exercise = model.Entity<Exercise>();
        exercise.ToTable("exercises", table =>
        {
            table.HasCheckConstraint("exercises_user_id_check", "user_id IS NULL OR user_id > 0");
            table.HasCheckConstraint("exercises_name_check", "btrim(name) <> ''");
            table.HasCheckConstraint("exercises_brand_name_check",
                "brand_name IS NULL OR (btrim(brand_name) <> '' AND length(brand_name) <= 100)");
        });
        exercise.HasKey(x => x.Id).HasName("exercises_pkey");
        var accessScope = exercise.Property<int>("AccessScopeId")
            .HasColumnName("access_scope_id")
            .HasComputedColumnSql("coalesce(user_id, 0)", stored: true)
            .ValueGeneratedOnAdd();
        accessScope.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        exercise.HasAlternateKey("Id", "AccessScopeId").HasName("exercises_id_access_scope_key");
        exercise.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        exercise.Property(x => x.UserId).HasColumnName("user_id");
        exercise.Property(x => x.Name).HasColumnName("name").HasColumnType("text");
        exercise.Property(x => x.Description).HasColumnName("description").HasColumnType("text").HasDefaultValue("");
        exercise.Property(x => x.BrandName).HasColumnName("brand_name").HasColumnType("text");
        exercise.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnType("timestamp with time zone");
        exercise.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
        exercise.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("exercises_user_id_fkey");
        exercise.HasIndex(x => new { x.UserId, x.Name }).HasDatabaseName("exercises_active_list")
            .HasFilter("archived_at IS NULL");

        var routine = model.Entity<Routine>();
        routine.ToTable("routines", table =>
            table.HasCheckConstraint("routines_name_check", "btrim(name) <> ''"));
        routine.HasKey(x => x.Id).HasName("routines_pkey");
        routine.HasAlternateKey(x => new { x.Id, x.UserId }).HasName("routines_id_user_id_key");
        routine.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        routine.Property(x => x.UserId).HasColumnName("user_id");
        routine.Property(x => x.Name).HasColumnName("name").HasColumnType("text");
        routine.Property(x => x.Description).HasColumnName("description").HasColumnType("text").HasDefaultValue("");
        routine.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnType("timestamp with time zone");
        routine.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").HasDefaultValueSql("now()");
        routine.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("routines_user_id_fkey");
        routine.HasIndex(x => new { x.UserId, x.Name }).HasDatabaseName("routines_active_list")
            .HasFilter("archived_at IS NULL");

        var placement = model.Entity<RoutineExercise>();
        placement.ToTable("routine_exercises", table =>
        {
            table.HasCheckConstraint("routine_exercises_position_check", "position > 0");
            table.HasCheckConstraint("routine_exercises_default_rest_seconds_check", "default_rest_seconds >= 0");
            table.HasCheckConstraint("routine_exercises_exercise_scope_check", "exercise_scope_id = 0 OR exercise_scope_id = user_id");
        });
        placement.HasKey(x => x.Id).HasName("routine_exercises_pkey");
        placement.HasAlternateKey(x => new { x.Id, x.UserId }).HasName("routine_exercises_id_user_id_key");
        placement.Property(x => x.Id).HasColumnName("id").UseIdentityAlwaysColumn();
        placement.Property(x => x.UserId).HasColumnName("user_id");
        placement.Property(x => x.RoutineId).HasColumnName("routine_id");
        placement.Property(x => x.ExerciseId).HasColumnName("exercise_id");
        placement.Property(x => x.ExerciseScopeId).HasColumnName("exercise_scope_id");
        placement.Property(x => x.Position).HasColumnName("position");
        placement.Property(x => x.Description).HasColumnName("description").HasColumnType("text").HasDefaultValue("");
        // Always send the validated value: zero rest must not select the database's 120 default.
        placement.Property(x => x.DefaultRestSeconds).HasColumnName("default_rest_seconds")
            .HasDefaultValue(120).ValueGeneratedNever();
        placement.Property(x => x.ArchivedAt).HasColumnName("archived_at").HasColumnType("timestamp with time zone");
        placement.HasOne<User>().WithMany().HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("routine_exercises_user_id_fkey");
        placement.HasOne<Routine>().WithMany(x => x.Exercises)
            .HasForeignKey(x => new { x.RoutineId, x.UserId }).HasPrincipalKey(x => new { x.Id, x.UserId })
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("routine_exercises_routine_owner_fkey");
        placement.HasOne(x => x.Exercise).WithMany()
            .HasForeignKey(x => new { x.ExerciseId, x.ExerciseScopeId }).HasPrincipalKey("Id", "AccessScopeId")
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("routine_exercises_exercise_scope_fkey");
        placement.HasIndex(x => new { x.RoutineId, x.Position }).IsUnique()
            .HasDatabaseName("routine_exercises_active_position").HasFilter("archived_at IS NULL");
    }
}
