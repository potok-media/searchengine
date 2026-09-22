using FluentMigrator;

namespace Potok.SearchEngine.Infrastructure.Persistence.Migrations;

[Migration(5)]
public class AddContinueWatchingStill : Migration
{
    public override void Up()
    {
        Alter.Table("continue_watching").InSchema(DbSchema.SearchEngineRaw)
            .AddColumn("still_src").AsString().Nullable();
    }

    public override void Down()
    {
        Delete.Column("still_src").FromTable("continue_watching").InSchema(DbSchema.SearchEngineRaw);
    }
}
