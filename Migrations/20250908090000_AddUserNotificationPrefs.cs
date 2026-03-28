using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CEObot.Migrations
{
    /// <inheritdoc />
    public partial class AddUserNotificationPrefs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NotifyEmail",
                table: "UserAccounts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "NotifyVoiceEnabled",
                table: "UserAccounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotifyEmail",
                table: "UserAccounts");

            migrationBuilder.DropColumn(
                name: "NotifyVoiceEnabled",
                table: "UserAccounts");
        }
    }
}
