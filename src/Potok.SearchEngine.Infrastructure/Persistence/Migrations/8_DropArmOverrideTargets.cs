using FluentMigrator;

namespace Potok.SearchEngine.Infrastructure.Persistence.Migrations;

// ARM graph v2 replaced the (workId, orderingId, groupId, episodeId) override target with
// (workId, entryId, episodeId). Pre-v2 file_map bindings point at the retired graph whose
// episode ids are dead, so they are dropped wholesale: every file_map entry carrying an
// armTarget is removed, numeric (season/episode) entries are untouched. Irreversible —
// the old identities cannot be reconstructed.
[Migration(8)]
public class DropArmOverrideTargets : Migration
{
    public override void Up()
    {
        var schema = DbSchema.SearchEngineRaw;
        Execute.Sql($$"""
            UPDATE "{{schema}}".torrent_overrides
            SET file_map = COALESCE((
                SELECT jsonb_object_agg(entry.key, entry.value)
                FROM jsonb_each(file_map) AS entry
                WHERE NOT (entry.value ? 'armTarget')
            ), '{}'::jsonb),
                updated_at = now()
            WHERE EXISTS (
                SELECT 1
                FROM jsonb_each(file_map) AS entry
                WHERE entry.value ? 'armTarget'
            )
            """);
    }

    public override void Down()
    {
        // Irreversible: the dropped bindings referenced the retired pre-v2 ARM graph.
    }
}
