using FluentMigrator;

namespace Potok.SearchEngine.Infrastructure.Persistence.Migrations;

[Migration(6)]
public sealed class AddTorrentObservations : Migration
{
    public override void Up()
    {
        var schema = DbSchema.SearchEngineRaw;

        Alter.Table("torrents").InSchema(schema)
            .AddColumn("name").AsString().Nullable()
            .AddColumn("original_name").AsString().Nullable()
            .AddColumn("release_year").AsInt32().Nullable()
            .AddColumn("types").AsCustom("text[]").Nullable()
            .AddColumn("quality").AsInt32().Nullable()
            .AddColumn("video_type").AsString().Nullable()
            .AddColumn("languages").AsCustom("text[]").Nullable()
            .AddColumn("voices").AsCustom("text[]").Nullable()
            .AddColumn("seasons").AsCustom("integer[]").Nullable();

        Create.Table("torrent_observations").InSchema(schema)
            .WithColumn("id").AsGuid().PrimaryKey().WithDefaultValue(SystemMethods.NewGuid)
            .WithColumn("torrent_id").AsGuid().NotNullable()
            .WithColumn("tracker").AsString().NotNullable()
            .WithColumn("source_key").AsString().NotNullable()
            .WithColumn("source_url").AsString().NotNullable()
            .WithColumn("title").AsString().Nullable()
            .WithColumn("name").AsString().Nullable()
            .WithColumn("original_name").AsString().Nullable()
            .WithColumn("magnet_uri").AsString().NotNullable()
            .WithColumn("size_bytes").AsInt64().Nullable()
            .WithColumn("size_label").AsString().Nullable()
            .WithColumn("seeders").AsInt32().Nullable()
            .WithColumn("leechers").AsInt32().Nullable()
            .WithColumn("publish_date").AsDateTimeOffset().Nullable()
            .WithColumn("release_year").AsInt32().Nullable()
            .WithColumn("types").AsCustom("text[]").Nullable()
            .WithColumn("quality").AsInt32().Nullable()
            .WithColumn("video_type").AsString().Nullable()
            .WithColumn("languages").AsCustom("text[]").Nullable()
            .WithColumn("voices").AsCustom("text[]").Nullable()
            .WithColumn("seasons").AsCustom("integer[]").Nullable()
            .WithColumn("observed_fields").AsInt64().NotNullable()
            .WithColumn("details_state").AsString().NotNullable()
            .WithColumn("source_updated_at").AsDateTimeOffset().Nullable()
            .WithColumn("fetched_at").AsDateTimeOffset().NotNullable()
            .WithColumn("details_fetched_at").AsDateTimeOffset().Nullable()
            .WithColumn("payload_schema_version").AsInt32().NotNullable()
            .WithColumn("parser_version").AsString().NotNullable()
            .WithColumn("source_payload").AsCustom("jsonb").NotNullable()
            .WithColumn("created_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime)
            .WithColumn("updated_at").AsDateTimeOffset().NotNullable().WithDefaultValue(SystemMethods.CurrentUTCDateTime);

        Create.ForeignKey("fk_torrent_observations_torrent")
            .FromTable("torrent_observations").InSchema(schema).ForeignColumn("torrent_id")
            .ToTable("torrents").InSchema(schema).PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);
        Create.Index("ux_torrent_observations_tracker_source_key")
            .OnTable("torrent_observations").InSchema(schema)
            .OnColumn("tracker").Ascending()
            .OnColumn("source_key").Ascending()
            .WithOptions().Unique();
        Create.Index("ix_torrent_observations_torrent_id")
            .OnTable("torrent_observations").InSchema(schema)
            .OnColumn("torrent_id");
        Create.Index("ix_torrent_observations_tracker_fetched_at")
            .OnTable("torrent_observations").InSchema(schema)
            .OnColumn("tracker").Ascending()
            .OnColumn("fetched_at").Descending();
        Create.Index("ix_torrent_observations_source_updated_at")
            .OnTable("torrent_observations").InSchema(schema)
            .OnColumn("source_updated_at");
        Execute.Sql($"""
            ALTER TABLE "{schema}".torrent_observations
              ADD CONSTRAINT ck_torrent_observations_seeders_nonnegative CHECK (seeders IS NULL OR seeders >= 0),
              ADD CONSTRAINT ck_torrent_observations_leechers_nonnegative CHECK (leechers IS NULL OR leechers >= 0),
              ADD CONSTRAINT ck_torrent_observations_details_state CHECK (
                details_state IN ('not_required', 'fetched', 'partial', 'failed', 'budget_exhausted'));
            CREATE INDEX ix_torrent_observations_source_payload
              ON "{schema}".torrent_observations USING gin (source_payload);
            CREATE INDEX ix_torrent_observations_title_trgm
              ON "{schema}".torrent_observations USING gin (title gin_trgm_ops);
            """);

        Create.Table("torrent_media_links").InSchema(schema)
            .WithColumn("torrent_id").AsGuid().NotNullable()
            .WithColumn("tmdb_id").AsInt64().NotNullable()
            .WithColumn("query").AsString().NotNullable()
            .WithColumn("reason").AsString().NotNullable()
            .WithColumn("first_seen_at").AsDateTimeOffset().NotNullable()
            .WithColumn("last_seen_at").AsDateTimeOffset().NotNullable();
        Create.PrimaryKey("pk_torrent_media_links")
            .OnTable("torrent_media_links").WithSchema(schema)
            .Columns("torrent_id", "tmdb_id");
        Create.ForeignKey("fk_torrent_media_links_torrent")
            .FromTable("torrent_media_links").InSchema(schema).ForeignColumn("torrent_id")
            .ToTable("torrents").InSchema(schema).PrimaryColumn("id")
            .OnDelete(System.Data.Rule.Cascade);
        Create.Index("ix_torrent_media_links_tmdb_last_seen")
            .OnTable("torrent_media_links").InSchema(schema)
            .OnColumn("tmdb_id").Ascending()
            .OnColumn("last_seen_at").Descending();

        Alter.Table("queries").InSchema(schema)
            .AddColumn("last_refresh_attempt_at").AsDateTimeOffset().Nullable()
            .AddColumn("last_refresh_error").AsString().Nullable();

        Execute.Sql($$"""
            INSERT INTO "{{schema}}".torrent_observations (
              torrent_id, tracker, source_key, source_url, title, magnet_uri,
              size_bytes, seeders, leechers, publish_date, observed_fields,
              details_state, fetched_at, details_fetched_at, payload_schema_version,
              parser_version, source_payload, created_at, updated_at)
            SELECT
              id,
              lower(tracker_name),
              url,
              url,
              title,
              magnet_uri,
              size,
              seeders,
              leechers,
              publish_date,
              127,
              'partial',
              updated_at,
              NULL,
              1,
              'legacy-backfill',
              '{"schemaVersion":1,"parserVersion":"legacy-backfill","list":{},"details":{},"unmapped":{},"warnings":[]}'::jsonb,
              updated_at,
              updated_at
            FROM "{{schema}}".torrents
            ON CONFLICT (tracker, source_key) DO NOTHING;

            INSERT INTO "{{schema}}".torrent_media_links (
              torrent_id, tmdb_id, query, reason, first_seen_at, last_seen_at)
            SELECT id, tmdb_id, title, 'legacy', updated_at, updated_at
            FROM "{{schema}}".torrents
            WHERE tmdb_id IS NOT NULL
            ON CONFLICT (torrent_id, tmdb_id) DO NOTHING;
            """);
    }

    public override void Down()
    {
        var schema = DbSchema.SearchEngineRaw;
        Delete.Column("last_refresh_error").FromTable("queries").InSchema(schema);
        Delete.Column("last_refresh_attempt_at").FromTable("queries").InSchema(schema);
        Delete.Table("torrent_media_links").IfExists().InSchema(schema);
        Delete.Table("torrent_observations").IfExists().InSchema(schema);
        Delete.Column("seasons").FromTable("torrents").InSchema(schema);
        Delete.Column("voices").FromTable("torrents").InSchema(schema);
        Delete.Column("languages").FromTable("torrents").InSchema(schema);
        Delete.Column("video_type").FromTable("torrents").InSchema(schema);
        Delete.Column("quality").FromTable("torrents").InSchema(schema);
        Delete.Column("types").FromTable("torrents").InSchema(schema);
        Delete.Column("release_year").FromTable("torrents").InSchema(schema);
        Delete.Column("original_name").FromTable("torrents").InSchema(schema);
        Delete.Column("name").FromTable("torrents").InSchema(schema);
    }
}
