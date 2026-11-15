using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AGUIWebChat.Server.Migrations
{
    /// <inheritdoc />
    /// <summary>
/// Seeds the small Ollama chat model used by the Aspire AppHost so a freshly created
/// database still renders a populated model catalogue. This is a data-only migration:
/// the EF model is intentionally left unchanged so existing test fixtures that assume
/// an empty model table keep working.
/// </summary>
    public partial class SeedOllamaChatModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "AIModels",
                columns: new[] { "Id", "ContextWindow", "DisplayName", "IsEnabled", "MaxOutputTokens", "ModelId", "ProviderId", "SupportsStreaming", "SupportsTools", "SupportsVision", "ThinkingMode" },
                values: new object[] { 1, 32768, "Qwen3 1.7B", true, 4096, "qwen3:1.7b", 1, true, true, false, "Effort" });

            migrationBuilder.InsertData(
                table: "AIModelDefaults",
                columns: new[] { "Id", "AIModelId", "NumCtx", "Temperature", "TopK", "TopP" },
                values: new object[] { 1, 1, 16384, 0.69999999999999996, 40, 0.90000000000000002 });

            migrationBuilder.InsertData(
                table: "AIModelReasoningEfforts",
                columns: new[] { "Id", "AIModelId", "DisplayName", "IsDefault", "SortOrder", "Value" },
                values: new object[,]
                {
                    { 1, 1, "Low", false, 1, "low" },
                    { 2, 1, "Medium", true, 2, "medium" },
                    { 3, 1, "High", false, 3, "high" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "AIModelDefaults",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "AIModelReasoningEfforts",
                keyColumn: "Id",
                keyValue: 1);

            migrationBuilder.DeleteData(
                table: "AIModelReasoningEfforts",
                keyColumn: "Id",
                keyValue: 2);

            migrationBuilder.DeleteData(
                table: "AIModelReasoningEfforts",
                keyColumn: "Id",
                keyValue: 3);

            migrationBuilder.DeleteData(
                table: "AIModels",
                keyColumn: "Id",
                keyValue: 1);
        }
    }
}
