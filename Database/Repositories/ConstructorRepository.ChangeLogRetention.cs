using Microsoft.EntityFrameworkCore;
using SmartBase.Database.Entities;

namespace SmartBase.Database.Repositories;

public partial class ConstructorRepository
{
    private const string RetentionEnabledKey = "changelog.retention.enabled";
    private const string RetentionMonthsKey = "changelog.retention.months";
    public const int DefaultRetentionMonths = 12;

    public (bool Enabled, int Months) GetChangeLogRetention()
    {
        var values = db.DbMeta.AsNoTracking()
            .Where(m => m.Key == RetentionEnabledKey || m.Key == RetentionMonthsKey)
            .ToDictionary(m => m.Key, m => m.Value);
        var months = values.TryGetValue(RetentionMonthsKey, out var text)
            && int.TryParse(text, out var stored) && stored > 0 ? stored : DefaultRetentionMonths;
        return (values.GetValueOrDefault(RetentionEnabledKey) == "1", months);
    }

    public void SetChangeLogRetention(bool enabled, int months)
    {
        Set(RetentionEnabledKey, enabled ? "1" : "0");
        Set(RetentionMonthsKey, months.ToString(System.Globalization.CultureInfo.InvariantCulture));
        db.SaveChanges();

        void Set(string key, string value)
        {
            var row = db.DbMeta.Find(key);
            if (row is not null)
            {
                db.Entry(row).Reload();
                if (db.Entry(row).State == EntityState.Detached) row = null;
            }
            if (row is null) db.DbMeta.Add(new DbMetaEntity { Key = key, Value = value });
            else row.Value = value;
        }
    }
}
