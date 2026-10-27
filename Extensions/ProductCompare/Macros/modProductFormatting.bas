Attribute VB_Name = "modProductFormatting"
Option Explicit

Private Const PF_MENU_TAG As String = "SmartBase.Formatting.V1"
Private Const PF_HEADER_ROWS As Long = 5
Private Const PF_FIRST_DATA_ROW As Long = 6

' Отдельное форматирование всех блоков отчета, без изменения их значений.
Public Sub FormatProductHistory()
    Dim ws As Worksheet
    Dim found As Range, searchArea As Range
    Dim fontArea As Range, borderArea As Range
    Dim headers As Variant, indexCols() As Long
    Dim borderType As Variant
    Dim blockCount As Long, lastCol As Long, lastRow As Long
    Dim firstCol As Long, endCol As Long
    Dim c As Long, h As Long, k As Long
    Dim errorText As String
    Dim oldScreen As Boolean, oldEvents As Boolean
    Dim oldCancel As XlEnableCancelKey, stateSaved As Boolean

    On Error GoTo Failed
    If ActiveWorkbook Is Nothing Then Exit Sub
    If Not (ActiveWorkbook Is ThisWorkbook) Then
        PF_Stop "Выберите лист в книге с этим макросом."
    End If
    If Not TypeOf ActiveSheet Is Worksheet Then PF_Stop "Выберите лист с отчетом."
    Set ws = ActiveSheet
    If ws.ProtectContents Then PF_Stop "Сначала снимите защиту листа."

    Set found = ws.Cells.Find(What:="*", After:=ws.Cells(1, 1), _
        LookIn:=xlFormulas, LookAt:=xlPart, SearchOrder:=xlByColumns, _
        SearchDirection:=xlPrevious, MatchCase:=False, SearchFormat:=False)
    If found Is Nothing Then PF_Stop "На листе нет данных."
    lastCol = found.Column
    headers = ws.Range(ws.Cells(1, 1), ws.Cells(PF_HEADER_ROWS, lastCol)).Value2
    ReDim indexCols(1 To lastCol)

    ' Блоки определяем только по шапке. Индексы строк для форматирования не нужны.
    For c = 1 To lastCol
        For h = 1 To PF_HEADER_ROWS
            If Not IsError(headers(h, c)) Then
                If PF_IsIndexHeader(headers(h, c)) Then
                    If c < 2 Then PF_Stop "Перед индексом должен быть столбец уровня."
                    If c + 7 > ws.Columns.Count Then PF_Stop "Блок выходит за границы листа."
                    If blockCount > 0 Then
                        If c - 1 <= indexCols(blockCount) + 7 Then
                            PF_Stop "Обнаружено пересечение блоков. Проверьте заголовки индекса."
                        End If
                    End If
                    blockCount = blockCount + 1
                    indexCols(blockCount) = c
                    Exit For
                End If
            End If
        Next h
    Next c
    If blockCount = 0 Then
        PF_Stop "В первых пяти строках нет заголовков «Индекс» или «Уровень идентификатор»."
    End If

    ' Общий конец отчета: одинаковая высота сетки у всех дат, включая пустые позиции.
    lastRow = PF_HEADER_ROWS
    For k = 1 To blockCount
        firstCol = indexCols(k) - 1
        endCol = indexCols(k) + 7
        Set searchArea = ws.Range(ws.Cells(PF_FIRST_DATA_ROW, firstCol), _
                                  ws.Cells(ws.Rows.Count, endCol))
        Set found = searchArea.Find(What:="*", After:=searchArea.Cells(1, 1), _
            LookIn:=xlFormulas, LookAt:=xlPart, SearchOrder:=xlByRows, _
            SearchDirection:=xlPrevious, MatchCase:=False, SearchFormat:=False)
        If Not found Is Nothing Then
            If found.Row > lastRow Then lastRow = found.Row
        End If
    Next k

    oldScreen = Application.ScreenUpdating
    oldEvents = Application.EnableEvents
    oldCancel = Application.EnableCancelKey
    stateSaved = True
    Application.EnableCancelKey = xlErrorHandler
    Application.ScreenUpdating = False
    Application.EnableEvents = False

    For k = 1 To blockCount
        firstCol = indexCols(k) - 1
        endCol = indexCols(k) + 7
        Set fontArea = ws.Range(ws.Cells(1, firstCol), ws.Cells(lastRow, endCol))
        With fontArea.Font
            .Name = "Arial"
            .Size = 12
        End With

        ' Верхние объединенные названия без новой сетки; границы с пятой строки.
        Set borderArea = ws.Range(ws.Cells(PF_HEADER_ROWS, firstCol), _
                                  ws.Cells(lastRow, endCol))
        For Each borderType In Array(xlEdgeLeft, xlEdgeTop, xlEdgeBottom, _
                                     xlEdgeRight, xlInsideVertical, xlInsideHorizontal)
            With borderArea.Borders(CLng(borderType))
                .LineStyle = xlContinuous
                .Weight = xlThin
                .Color = RGB(0, 0, 0)
            End With
        Next borderType
    Next k
    GoTo Finish

Failed:
    errorText = Err.Description
Finish:
    If stateSaved Then
        On Error Resume Next
        Application.EnableEvents = oldEvents
        Application.ScreenUpdating = oldScreen
        Application.EnableCancelKey = oldCancel
        On Error GoTo 0
    End If
    If Len(errorText) > 0 Then
        MsgBox "Форматирование остановлено: " & errorText, vbExclamation
    Else
        MsgBox "Форматирование выполнено." & vbCrLf & _
               "Блоков: " & blockCount & vbCrLf & "Шрифт: Arial, 12.", vbInformation
    End If
End Sub

Public Sub InstallProductFormattingMenu()
    Dim menuName As Variant, menu As Object, button As Object
    Dim macroName As String, errorText As String
    Dim cellMenuAdded As Boolean

    On Error GoTo Failed
    If ActiveWorkbook Is Nothing Then Exit Sub
    If Not (ActiveWorkbook Is ThisWorkbook) Then Exit Sub
    RemoveProductFormattingMenu
    macroName = "'" & Replace$(ThisWorkbook.Name, "'", "''") & "'!FormatProductHistory"

    For Each menuName In Array("Cell", "Row", "Column", "List Range Popup")
        Set menu = PF_GetMenu(CStr(menuName))
        If Not menu Is Nothing Then
            Set button = menu.Controls.Add(Type:=1, Before:=1, Temporary:=True)
            With button
                .Caption = "Форматирование"
                .Tag = PF_MENU_TAG
                .Style = 2
                .OnAction = macroName
                .BeginGroup = False
            End With
            If CStr(menuName) = "Cell" Then cellMenuAdded = True
        End If
    Next menuName
    If Not cellMenuAdded Then PF_Stop "Не удалось получить контекстное меню ячеек."
    Exit Sub

Failed:
    errorText = Err.Description
    RemoveProductFormattingMenu
    MsgBox "Не удалось добавить кнопку форматирования: " & errorText, vbExclamation
End Sub

Public Sub RemoveProductFormattingMenu()
    Dim menuName As Variant, menu As Object, i As Long
    On Error Resume Next
    For Each menuName In Array("Cell", "Row", "Column", "List Range Popup")
        Set menu = PF_GetMenu(CStr(menuName))
        If Not menu Is Nothing Then
            For i = menu.Controls.Count To 1 Step -1
                If menu.Controls(i).Tag = PF_MENU_TAG Then menu.Controls(i).Delete
            Next i
        End If
    Next menuName
    On Error GoTo 0
End Sub

Private Function PF_IsIndexHeader(ByVal value As Variant) As Boolean
    Dim text As String
    If IsEmpty(value) Or IsNull(value) Then Exit Function
    text = Trim$(Replace$(Replace$(Replace$(Replace$( _
        CStr(value), ChrW(160), " "), vbTab, " "), vbCr, " "), vbLf, " "))
    PF_IsIndexHeader = StrComp(text, "Индекс", vbTextCompare) = 0 Or _
                       StrComp(text, "Уровень идентификатор", vbTextCompare) = 0
End Function

Private Function PF_GetMenu(ByVal menuName As String) As Object
    On Error Resume Next
    Set PF_GetMenu = Application.CommandBars(menuName)
    On Error GoTo 0
End Function

Private Sub PF_Stop(ByVal message As String)
    Err.Raise vbObjectError + 5100, "Форматирование", message
End Sub
