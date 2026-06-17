using System.ComponentModel.DataAnnotations;

namespace DataBase.Database.Entities;

/// <summary>
/// Пара «ключ-значение» настроек приложения (системная БД app.db, таблица «app_settings»).
/// Хранит масштаб, состояние/ширину панели записи и т.п.
/// Значение хранится строкой; формат (число/bool) — на стороне <c>AppSettings</c>
/// (InvariantCulture), чтобы хранилище оставалось простым ключ-значением.
/// </summary>
public class AppSettingEntity
{
    [Key]      public string Key   { get; set; } = "";
    [Required] public string Value { get; set; } = "";
}
