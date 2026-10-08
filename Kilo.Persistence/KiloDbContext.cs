using Kilo.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace Kilo.Persistence;

public sealed class KiloDbContext(DbContextOptions<KiloDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

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
    }
}
