namespace SmartBase.Core.Models;

/// <summary>Координаты ячеек — смещения в скопированном прямоугольнике, а не подписи.</summary>
public sealed record RelationClipboardCell(int Row, int Column, IReadOnlyList<RelationTarget> Targets);
public sealed record RelationClipboard(int Version, string Text, int SourceDbId, string SourceIdentity,
    Dictionary<int, string> DatabaseIdentities, List<RelationClipboardCell> Cells);
