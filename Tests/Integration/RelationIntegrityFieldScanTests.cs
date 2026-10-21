using SmartBase.Core.Models;
using SmartBase.Database.Backup;

namespace SmartBase.Tests.Integration;

/// <summary>Проверка связей одного свойства из его окна: в отчет идут только ребра этого
/// свойства и его столбцы данных, исправление идет тем же путем с резервной копией.</summary>
public sealed class RelationIntegrityFieldScanTests
{
    [Fact]
    public void A_field_scan_reports_only_that_relation_and_its_data_columns()
    {
        using var f = new RelationIntegrityFixture();
        var other = f.RelationField(f.SourceCollection, f.LocalCollection, "Другая связь");
        var own = f.InsertEdge(f.Relation, f.SourceRecord, 0, 960001);
        f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0]);
        var foreign = f.InsertEdge(other, f.SourceRecord, 0, 960002);
        var column = f.InsertField(f.SourceCollection, "Данные", "lookup",
            new RelationComputedConfig(f.Relation, "999999").ToConfigJson());
        var otherColumn = f.InsertField(f.SourceCollection, "Чужие данные", "lookup",
            new RelationComputedConfig(other, "999999").ToConfigJson());

        var report = f.Scan(new(FieldId: f.Relation));

        Assert.Equal(RelationIntegrityStatus.Completed, report.Status);
        Assert.Equal(2, report.ScannedEdges);
        Assert.Equal(2, report.ScannedFields);
        Assert.Contains(report.Issues, i => i.EdgeId == own && i.Kind == RelationIntegrityIssueKind.MissingTargetRecord);
        Assert.Contains(report.Issues, i => i.FieldId == column && i.Kind == RelationIntegrityIssueKind.MissingDependency);
        Assert.DoesNotContain(report.Issues, i => i.EdgeId == foreign || i.FieldId == other || i.FieldId == otherColumn);
        // Вся база по-прежнему видит оба свойства.
        Assert.Contains(f.Scan().Issues, i => i.EdgeId == foreign);
    }

    [Fact]
    public void A_field_scan_repairs_through_the_same_backup_path()
    {
        using var f = new RelationIntegrityFixture();
        var other = f.RelationField(f.SourceCollection, f.LocalCollection, "Другая связь");
        var broken = f.InsertEdge(f.Relation, f.SourceRecord, 0, 961001);
        var valid = f.InsertEdge(f.Relation, f.SourceRecord, 0, f.LocalRecords[0]);
        var foreign = f.InsertEdge(other, f.SourceRecord, 0, 961002);
        var report = f.Scan(new(FieldId: f.Relation));
        var plan = f.Preview(report, [report.Issues.Single(i => i.EdgeId == broken && i.CanRepair).Key]);
        Assert.True(plan.CanApply, plan.Message);

        var result = f.Source.Constructor.ApplyRelationIntegrityRepairAsync(plan, (request, ct) =>
            RelationIntegrityBackupService.CreateVerifiedAsync(f.Source, request, f.Backups.Path, f.PgInfo, ct: ct))
            .GetAwaiter().GetResult();

        Assert.True(result.Applied, result.Message);
        Assert.Equal(1, result.RemovedEdges);
        Assert.Equal([valid, foreign], f.EdgeIds());
    }
}
