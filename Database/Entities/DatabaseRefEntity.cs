using System.ComponentModel.DataAnnotations;

namespace SmartBase.Database.Entities;

/// <summary>
/// Запись реестра баз данных (системная БД app.db, таблица «databases»).
/// Provider пока всегда "sqlite"; в будущем сюда добавится "postgres".
/// </summary>
public class DatabaseRefEntity
{
    [Key] public int       Id        { get; set; }
    [Required] public string Name     { get; set; } = "";
    [Required] public string Path     { get; set; } = "";
    [Required] public string Provider { get; set; } = "sqlite";
    public bool            IsDefault { get; set; }
}
