using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SmartBase.Controls.Dialogs;

namespace SmartBase.Extensions.DocRegistry;

/// <summary>Доступ к боту выбирают по карточкам пользователей; в настройке остаются ID.</summary>
public sealed class BotAccessEditor : UserControl
{
    public BotAccessEditor(ExtensionSettingEditorContext context)
    {
        var picker = new ParticipantPicker(() => BotUsers.Choices(
            BotUsers.Directory(context.Read(BotUsers.DirectoryKey)), context.Read(DocRegistryExtension.SendersKey)))
            { Name = "BotAccessUsers", HorizontalAlignment = HorizontalAlignment.Stretch };
        picker.SetSelected(MessageRegistration.Senders(context.Read(DocRegistryExtension.SendersKey)));
        picker.SelectionChanged += (_, _) =>
        {
            var value = string.Join("; ", picker.SelectedIds);
            if (context.Read(DocRegistryExtension.SendersKey) != value) context.Save(DocRegistryExtension.SendersKey, value);
        };
        var instruction = new Button
        {
            Name = "BotSendInstruction", Classes = { "dialog", "auto" }, Content = SenderGreeting.ButtonText,
        };
        instruction.Click += async (_, _) =>
        {
            if (context.Owner() is not { } owner) return;
            instruction.IsEnabled = false;
            try { await SenderGreeting.RunAsync(new(owner, context.Read(DocRegistryExtension.SendersKey), context.Replies())); }
            catch (Exception error) { await ConfirmDialog.Info("Не удалось отправить инструкцию: " + error.Message).ShowDialog(owner); }
            finally { instruction.IsEnabled = true; }
        };
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(picker);
        panel.Children.Add(new TextBlock { Classes = { "desc" }, TextWrapping = TextWrapping.Wrap,
            Text = "Выберите по ФИО, кому разрешена работа с ботом. Пустой список закрывает доступ всем." });
        panel.Children.Add(instruction);
        Content = panel;
    }
}
