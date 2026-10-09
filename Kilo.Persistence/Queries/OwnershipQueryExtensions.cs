using Microsoft.EntityFrameworkCore;

namespace Kilo.Persistence.Queries;

public static class OwnershipQueryExtensions
{
    // For EF entities with a mapped int/int? UserId. Supply the verified local user ID.
    // Null owners (global exercises) never match; visibility and admin policies stay separate.
    public static IQueryable<TEntity> OwnedBy<TEntity>(this IQueryable<TEntity> query, int userId)
        where TEntity : class
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);
        return query.Where(entity => EF.Property<int?>(entity, "UserId") == userId);
    }
}
