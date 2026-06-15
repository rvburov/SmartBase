namespace DataBase.Core.Models;

public class DbFieldValue
{
    public int?   Id       { get; set; }
    public int    RecordId { get; set; }
    public int    FieldId  { get; set; }
    public string Value    { get; set; } = "";
}
