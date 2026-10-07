using Kilo.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Kilo.Migrations;

// One-time adoption of the exact retained users baseline; never a general schema importer.
internal static class LegacyBaselineAdoption
{
    public static async Task AdoptAsync(KiloDbContext db, CancellationToken ct)
    {
        var history = db.GetService<IHistoryRepository>();
        if ((await history.GetAppliedMigrationsAsync(ct)).Count != 0)
            throw new InvalidOperationException("EF migrations are already recorded.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(Audit, ct);
        var baseline = db.Database.GetMigrations().Single(id => id.EndsWith("_CreateUsers", StringComparison.Ordinal));
        await db.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
        await db.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())), ct);
        await transaction.CommitAsync(ct);
    }

    private const string Audit = """
        LOCK TABLE public.users IN ACCESS EXCLUSIVE MODE;
        DO $audit$
        DECLARE columns jsonb;
        BEGIN
          IF NOT EXISTS (SELECT 1 FROM public.schemaversions
                         WHERE scriptname = 'Kilo.Migrations.Migrations.001_users.sql')
             OR (SELECT count(*) FROM public.schemaversions) <> 1 THEN
            RAISE EXCEPTION 'Unrecognized legacy migration history';
          END IF;
          SELECT jsonb_agg(jsonb_build_array(column_name, data_type, is_nullable,
                          is_identity, identity_generation, column_default)
                          ORDER BY ordinal_position)
            INTO columns FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'users';
          IF columns IS DISTINCT FROM $columns$[
            ["id","integer","NO","YES","ALWAYS",null],
            ["clerk_user_id","text","NO","NO",null,null],
            ["time_zone","text","NO","NO",null,"'UTC'::text"],
            ["measurement_system","text","NO","NO",null,"'imperial'::text"],
            ["created_at","timestamp with time zone","NO","NO",null,"now()"]
          ]$columns$::jsonb THEN
            RAISE EXCEPTION 'Legacy users columns differ';
          END IF;
          IF (SELECT array_agg(conname::text ORDER BY conname)
              FROM pg_constraint WHERE conrelid = 'public.users'::regclass
              AND contype <> 'n') IS DISTINCT FROM ARRAY[
                'users_clerk_user_id_check', 'users_clerk_user_id_key',
                'users_measurement_system_check', 'users_pkey'] THEN
            RAISE EXCEPTION 'Legacy users constraint names differ';
          END IF;
          IF (SELECT array_agg(pg_get_constraintdef(oid) ORDER BY conname)
              FROM pg_constraint WHERE conrelid = 'public.users'::regclass
              AND contype IN ('p', 'u', 'c')) IS DISTINCT FROM ARRAY[
                'CHECK ((btrim(clerk_user_id) <> ''''::text))',
                'UNIQUE (clerk_user_id)',
                'CHECK ((measurement_system = ANY (ARRAY[''imperial''::text, ''metric''::text])))',
                'PRIMARY KEY (id)'] THEN
            RAISE EXCEPTION 'Legacy users constraints differ';
          END IF;
          IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public'
                     AND tablename NOT IN ('users', 'schemaversions', '__EFMigrationsHistory'))
             OR EXISTS (SELECT 1 FROM pg_trigger WHERE tgrelid = 'public.users'::regclass
                        AND NOT tgisinternal) THEN
            RAISE EXCEPTION 'Legacy schema has unsupported additions';
          END IF;
        END $audit$;
        """;
}
