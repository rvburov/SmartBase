namespace DataBase.Database;

/// <summary>
/// Составной идентификатор сущности при мульти-маунте: какая БД (DbId) + локальный Id.
/// Локальные id уникальны только внутри своего файла, поэтому в UI коллекции/записи
/// адресуются парой (DbId, Id), чтобы id из разных БД не конфликтовали.
/// </summary>
public readonly record struct EntityRef(int DbId, int Id);
