using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using SmartBase.Controls.DrawnGrid;
using SmartBase.Core.Models;
using SmartBase.Database;
using SmartBase.Views.ConstructorDb;

/// <summary>Живая страница записи: вычисленные свойства показываются и остаются только для чтения.</summary>
internal static class RelationComputedChecks
{
    public static int Run(string outDir)
    {
        var path = Path.Combine(outDir, $"relation-computed-{Guid.NewGuid():N}.db");
        DatabaseManager? manager = null;
        Window? window = null;
        var failed = 0;
        var checks = 0;
        try
        {
            manager = DatabaseManager.Open(9417, "relation-computed", path);
            var repo = manager.Constructor;
            var target = repo.AddCollection(new DbCollection { Name = "Счета" }).Id!.Value;
            var source = repo.AddCollection(new DbCollection { Name = "Клиенты" }).Id!.Value;
            var amount = repo.AddField(new DbField { CollectionId = target, Name = "Сумма", FieldType = "number" });
            var relation = repo.AddField(new DbField
            {
                CollectionId = source, Name = "Счета", FieldType = "relation",
                ConfigJson = new RelationConfig(target, false).ToConfigJson(),
            });
            var lookup = repo.AddField(new DbField
            {
                CollectionId = source, Name = "Названия счетов", FieldType = "lookup",
                ConfigJson = new RelationComputedConfig(relation.Id!.Value).ToConfigJson(), Position = 2000,
            });
            var sum = repo.AddField(new DbField
            {
                CollectionId = source, Name = "Итого", FieldType = "rollup",
                ConfigJson = new RelationComputedConfig(relation.Id!.Value, amount.Id!.Value.ToString(), "sum").ToConfigJson(), Position = 3000,
            });
            repo.AddField(new DbField { CollectionId = source, Name = "Создано", FieldType = "created_at", Position = 4000 });
            var a = repo.AddRecord(new DbRecord { CollectionId = target, Title = "Счет А" }).Id!.Value;
            var b = repo.AddRecord(new DbRecord { CollectionId = target, Title = "Счет Б" }).Id!.Value;
            repo.SetFieldValue(a, amount.Id!.Value, "1,25");
            repo.SetFieldValue(b, amount.Id.Value, "10,5");
            var id = repo.AddRecord(new DbRecord { CollectionId = source, Title = "Клиент" }).Id!.Value;
            repo.SetLinks(relation.Id!.Value, id, [b, a]);
            var page = new RecordPageView();
            var host = new Window { Width = 620, Height = 800, Content = page };
            window = host;
            host.Show();
            page.Show(repo, id, source);
            Pump();
            var list = page.FindControl<PropertyList>("PropertiesHost")!;
            Expect(Display(lookup.Name) == "Счет Б, Счет А", "lookup показал обе цели в порядке связи");
            Expect(Display(sum.Name) == "11,75", "rollup показал числовую сумму с десятичной запятой");
            Expect(Display("Создано").Length > 0 && Display("Создано") != Display(lookup.Name),
                   "прежнее вычисляемое поле даты осталось независимым от lookup");

            ClickValue(lookup.Name);
            Expect(list.CurrentEditor is TextBox { IsReadOnly: true, Text: "Счет Б, Счет А" },
                   "клик по lookup открыл только чтение показанных значений");
            host.KeyTextInput("не должно записаться");
            Pump();
            Expect(Display(lookup.Name) == "Счет Б, Счет А", "набор в lookup не меняет вычисленное значение");
            ClickValue(sum.Name);
            Expect(list.CurrentEditor is TextBox { IsReadOnly: true, Text: "11,75" },
                   "редактор итога также доступен только для чтения");
            host.KeyTextInput("999");
            Pump();
            Expect(Display(sum.Name) == "11,75", "набор в итог не подменяет вычисление");
            Expect(!manager.Context.DbFieldValues.Any(v => v.FieldId == lookup.Id || v.FieldId == sum.Id),
                   "показ и попытка ввода не создали материальные значения вычисляемых полей");

            repo.SetRecordTitle(b, "Счет Б новый");
            repo.SetFieldValue(b, amount.Id.Value, "20");
            page.ReloadFromDb();
            Pump();
            Expect(Display(lookup.Name) == "Счет Б новый, Счет А", "перечитывание увидело переименование цели");
            Expect(Display(sum.Name) == "21,25", "перечитывание обновило итог после изменения целевого числа");
            repo.SetLinks(relation.Id.Value, id, [a]);
            page.ReloadFromDb();
            Pump();
            Expect(Display(lookup.Name) == "Счет А" && Display(sum.Name) == "1,25",
                   "изменение набора связей одновременно обновило lookup и rollup");
            Console.WriteLine($"Вычисления по связи UI: проверок {checks}, провалов {failed}.");
            return failed == 0 ? 0 : 2;

            string Display(string name)
            {
                var card = list.Cards.Single(c => c.Name == name);
                return card.Display?.Invoke() ?? card.Value();
            }

            void Expect(bool ok, string message)
            {
                checks++;
                if (!ok) failed++;
                Console.WriteLine($"  {(ok ? "ок    " : "ПРОВАЛ")} {message}");
            }

            void Pump()
            {
                Dispatcher.UIThread.RunJobs();
                host.UpdateLayout();
                using var frame = host.CaptureRenderedFrame();
                Dispatcher.UIThread.RunJobs();
            }

            void ClickValue(string name)
            {
                var index = list.Cards.ToList().FindIndex(c => c.Name == name);
                list.ScrollIntoView(index);
                Pump();
                var rect = list.CardRect(index);
                var point = list.Surface.TranslatePoint(new Point(rect.X + list.NameColumnWidth + 70,
                    rect.Y + rect.Height / 2), host) ?? throw new InvalidOperationException("Карточка не прикреплена к окну");
                host.MouseMove(point);
                Pump();
                host.MouseDown(point, MouseButton.Left);
                host.MouseUp(point, MouseButton.Left);
                Pump();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ПРОВАЛ проверки вычислений по связи: {ex}");
            return 2;
        }
        finally
        {
            window?.Close();
            Dispatcher.UIThread.RunJobs();
            manager?.Dispose();
            AppDbContext.ResetPool(path);
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }
}
