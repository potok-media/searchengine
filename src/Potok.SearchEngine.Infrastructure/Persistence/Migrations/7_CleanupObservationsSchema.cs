using FluentMigrator;

namespace Potok.SearchEngine.Infrastructure.Persistence.Migrations;

// Cleanup after the observations cutover: torrents.tmdb_id is superseded by torrent_media_links,
// ix_torrents_info_hash duplicates the UNIQUE constraint on info_hash, and the observation
// payload/trgm/fetch-order indexes have no readers in the standalone service. Adds the missing
// index backing GetStaleSearchQueriesAsync.
[Migration(7)]
public sealed class CleanupObservationsSchema : Migration
{
    public override void Up()
    {
        var schema = DbSchema.SearchEngineRaw;
        Execute.Sql($"""
            DROP INDEX IF EXISTS "{schema}".ix_torrents_tmdb_id;
            DROP INDEX IF EXISTS "{schema}".ix_torrents_info_hash;
            DROP INDEX IF EXISTS "{schema}".ix_torrent_observations_source_payload;
            DROP INDEX IF EXISTS "{schema}".ix_torrent_observations_title_trgm;
            DROP INDEX IF EXISTS "{schema}".ix_torrent_observations_tracker_fetched_at;
            DROP INDEX IF EXISTS "{schema}".ix_torrent_observations_source_updated_at;
            CREATE INDEX IF NOT EXISTS ix_queries_last_refresh_time
              ON "{schema}".queries (last_refresh_time);
            """);
        Delete.Column("tmdb_id").FromTable("torrents").InSchema(schema);
    }

    public override void Down()
    {
        var schema = DbSchema.SearchEngineRaw;
        Execute.Sql($"""DROP INDEX IF EXISTS "{schema}".ix_queries_last_refresh_time;""");
        Alter.Table("torrents").InSchema(schema)
            .AddColumn("tmdb_id").AsInt64().Nullable().Indexed();
        Execute.Sql($"""
            CREATE INDEX IF NOT EXISTS ix_torrents_tmdb_id
              ON "{schema}".torrents (tmdb_id);
            CREATE INDEX IF NOT EXISTS ix_torrents_info_hash
              ON "{schema}".torrents (info_hash);
            CREATE INDEX IF NOT EXISTS ix_torrent_observations_source_payload
              ON "{schema}".torrent_observations USING gin (source_payload);
            CREATE INDEX IF NOT EXISTS ix_torrent_observations_title_trgm
              ON "{schema}".torrent_observations USING gin (title gin_trgm_ops);
            CREATE INDEX IF NOT EXISTS ix_torrent_observations_tracker_fetched_at
              ON "{schema}".torrent_observations (tracker, fetched_at DESC);
            CREATE INDEX IF NOT EXISTS ix_torrent_observations_source_updated_at
              ON "{schema}".torrent_observations (source_updated_at);
            """);
    }
}
