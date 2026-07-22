using Avalonia.Controls;
using SmartBase.Core.Models;

namespace SmartBase.Controls.Dialogs;

/// <summary>
/// Окно записи, открываемое по клику на уведомление: полная информация о событии
/// (наименование, дата/время, коллекция, описание) и кнопка «Принято» (подтвердить
/// прочтение — просто закрывает). Стиль — как у <see cref="ConfirmDialog"/>.
/// </summary>
public partial class NotificationRecordDialog : Window
{
    public NotificationRecordDialog()
    {
        InitializeComponent();
        AcceptBtn.Click += (_, _) => Close();
    }

    public NotificationRecordDialog(CalendarRecord record) : this()
    {
        TitleLabel.Text = record.DisplayTitle;
        WhenLabel.Text  = new DateValue(record.StartDt, record.EndDt, record.AllDay).ToDisplay();

        if (!string.IsNullOrWhiteSpace(record.CollectionName))
        {
            CollectionLabel.Text      = record.CollectionName;
            CollectionLabel.IsVisible = true;
        }
        if (!string.IsNullOrWhiteSpace(record.Description))
        {
            DescriptionLabel.Text      = record.Description;
            DescriptionLabel.IsVisible = true;
        }
    }
}
