using Kilo.Persistence;
using Kilo.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kilo.Features.Me;

public sealed class CurrentUser(KiloDbContext db, IHttpContextAccessor httpContextAccessor)
{
    private int? _userId;

    public async Task<int> GetIdAsync(CancellationToken cancellationToken = default)
    {
        if (_userId is { } cachedId)
        {
            return cachedId;
        }

        var principal = httpContextAccessor.HttpContext?.User;

        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException(
                "CurrentUser requires an authenticated request.");
        }

        var subject = principal.FindFirst("sub")?.Value;

        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new InvalidOperationException(
                "The authenticated principal has no subject.");
        }

        var userIds = db.Users
            .AsNoTracking()
            .Where(user => user.ClerkUserId == subject)
            .Select(user => user.Id);

        var id = await userIds.SingleOrDefaultAsync(cancellationToken);

        if (id == 0)
        {
            var user = new User { ClerkUserId = subject };
            db.Users.Add(user);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                id = user.Id;
            }
            catch (DbUpdateException exception) when (
                exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                    ConstraintName: "users_clerk_user_id_key"
                })
            {
                db.Entry(user).State = EntityState.Detached;
                id = await userIds.SingleAsync(cancellationToken);
            }
        }

        _userId = id;
        return id;
    }
}
