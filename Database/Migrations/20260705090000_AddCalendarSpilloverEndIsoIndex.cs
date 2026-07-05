using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataBase.Database.Migrations
{
    /// <summary>
    /// Индекс под спилловеры окна сетки календаря: timed-события, начавшиеся ДО окна и
    /// пересекающие его, ищутся сиком по EndIso ∈ [начало окна, начало+буфер). Без него
    /// проверка EndIso шла чтением строк по всему 62-дневному StartIso-буферу — день-гигант
    /// в буфере стоил секунд на каждое открытие «Дня»/«Недели» в следующие 62 дня после него
    /// (замер на 10 млн: 3.15 с → 7 мс). Хвост (StartIso, RecordId, FieldId) делает запрос
    /// покрывающим. Построение на 10 млн строк — ~8 с, разово при миграции.
    /// </summary>
    public partial class AddCalendarSpilloverEndIsoIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_calendar_index_CollectionId_AllDay_EndIso_StartIso_RecordId_FieldId",
                table: "calendar_index",
                columns: new[] { "CollectionId", "AllDay", "EndIso", "StartIso", "RecordId", "FieldId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_calendar_index_CollectionId_AllDay_EndIso_StartIso_RecordId_FieldId",
                table: "calendar_index");
        }
    }
}
