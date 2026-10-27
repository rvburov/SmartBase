Attribute VB_Name = "modProductGrouping"
Option Explicit

Private Const GROUP_MENU_TAG As String = "SmartBase.ProductGrouping.V2"

Public Sub GroupProductHistory()
    Const HEADER_ROWS As Long = 5
    Const AFTER_INDEX As Long = 7
    Dim ws As Worksheet, found As Range, area As Range
    Dim headers As Variant, data As Variant, title As String
    Dim indexCol As Long, lastCol As Long, lastRow As Long
    Dim levels() As Long, n As Long, c As Long, h As Long
    Dim i As Long, j As Long, z As Long, rowNumber As Long
    Dim index As String, rawLevel As Long, previousLevel As Long
    Dim hasDetails As Boolean, indexedRows As Long
    Dim oldScreen As Boolean, oldEvents As Boolean
    Dim oldCalc As XlCalculation, oldCancel As XlEnableCancelKey
    Dim savedState As Boolean, errorText As String

    On Error GoTo Failed
    If ActiveWorkbook Is Nothing Then LB_Stop "Откройте книгу с отчетом."
    If Not (ActiveWorkbook Is ThisWorkbook) Then
        LB_Stop "Выберите лист в книге, в которую добавлен макрос."
    End If
    If Not TypeOf ActiveSheet Is Worksheet Then LB_Stop "Выберите лист с отчетом."
    Set ws = ActiveSheet
    If ws.ProtectContents Then LB_Stop "Сначала снимите защиту листа."
    If ws.FilterMode Then LB_Stop "Сначала снимите активный фильтр таблицы."

    Set found = ws.Cells.Find(What:="*", After:=ws.Cells(1, 1), _
        LookIn:=xlFormulas, LookAt:=xlPart, SearchOrder:=xlByColumns, _
        SearchDirection:=xlPrevious, MatchCase:=False, SearchFormat:=False)
    If found Is Nothing Then LB_Stop "На листе нет данных."
    lastCol = found.Column
    headers = ws.Range(ws.Cells(1, 1), ws.Cells(HEADER_ROWS, lastCol)).Value2

    ' Справа налево: ищем индекс последнего блока, включая название из шаблона.
    For c = lastCol To 2 Step -1
        For h = 1 To HEADER_ROWS
            If Not IsError(headers(h, c)) Then
                title = LB_Text(headers(h, c))
                If StrComp(title, "Индекс", vbTextCompare) = 0 Or _
                   StrComp(title, "Уровень идентификатор", vbTextCompare) = 0 Then
                    indexCol = c
                    Exit For
                End If
            End If
        Next h
        If indexCol > 0 Then Exit For
    Next c
    If indexCol = 0 Then
        LB_Stop "В первых пяти строках не найден заголовок «Индекс» или «Уровень идентификатор»."
    End If
    If indexCol + AFTER_INDEX > ws.Columns.Count Then
        LB_Stop "Последний блок выходит за границы листа."
    End If

    ' Конец всего отчета нужен и для строк, оставшихся только в прежних датах.
    ' Их индексы и уровни не считываем и не используем.
    Set area = ws.Range(ws.Cells(HEADER_ROWS + 1, 1), _
                        ws.Cells(ws.Rows.Count, indexCol + AFTER_INDEX))
    Set found = area.Find(What:="*", After:=area.Cells(1, 1), _
        LookIn:=xlFormulas, LookAt:=xlPart, SearchOrder:=xlByRows, _
        SearchDirection:=xlPrevious, MatchCase:=False, SearchFormat:=False)
    If found Is Nothing Then LB_Stop "Под заголовками нет данных."
    lastRow = found.Row
    n = lastRow - HEADER_ROWS
    ReDim levels(1 To n)

    ' Считываем только индекс и данные последнего блока. Столбец уровня не нужен.
    data = ws.Range(ws.Cells(HEADER_ROWS + 1, indexCol), _
                    ws.Cells(lastRow, indexCol + AFTER_INDEX)).Value2

    ' Полностью проверяем и рассчитываем уровни до изменения группировки.
    For i = 1 To n
        rowNumber = HEADER_ROWS + i
        If IsError(data(i, 1)) Then
            LB_Stop "Ошибка значения в индексе последнего блока, строка " & rowNumber & "."
        End If
        index = LB_Text(data(i, 1))

        If Len(index) > 0 Then
            indexedRows = indexedRows + 1
            rawLevel = LB_IndexLevel(index, rowNumber)

            previousLevel = rawLevel
            If rawLevel = 0 Then
                levels(i) = 1
            ElseIf rawLevel > 8 Then
                levels(i) = 8
            Else
                levels(i) = rawLevel
            End If
        Else
            ' Без индекса: операция только при наличии данных в C-I последнего блока.
            hasDetails = False
            For z = 2 To UBound(data, 2)
                If IsError(data(i, z)) Then
                    hasDetails = True
                    Exit For
                End If
                If Len(LB_Text(data(i, z))) > 0 Then
                    hasDetails = True
                    Exit For
                End If
            Next z

            If hasDetails And previousLevel > 0 Then
                If previousLevel >= 8 Then
                    levels(i) = 8
                Else
                    levels(i) = previousLevel + 1
                End If
            Else
                levels(i) = 1
                If Not hasDetails Then previousLevel = 0
            End If
        End If
    Next i
    If indexedRows = 0 Then LB_Stop "В последнем блоке нет строк с индексом."

    oldScreen = Application.ScreenUpdating
    oldEvents = Application.EnableEvents
    oldCalc = Application.Calculation
    oldCancel = Application.EnableCancelKey
    savedState = True
    Application.EnableCancelKey = xlErrorHandler
    Application.ScreenUpdating = False
    Application.EnableEvents = False
    Application.Calculation = xlCalculationManual

    ' Меняем только структуру строк. Значения и оформление ячеек не изменяются.
    With ws.Rows(CStr(HEADER_ROWS + 1) & ":" & CStr(lastRow))
        .ClearOutline
        .Hidden = False
    End With
    ws.Outline.SummaryRow = xlAbove
    i = 1
    Do While i <= n
        j = i
        Do While j < n
            If levels(j + 1) <> levels(i) Then Exit Do
            j = j + 1
        Loop
        ws.Rows(CStr(HEADER_ROWS + i) & ":" & CStr(HEADER_ROWS + j)).OutlineLevel = levels(i)
        i = j + 1
    Loop
    ws.Outline.ShowLevels RowLevels:=8
    If Not (ActiveWindow Is Nothing) Then ActiveWindow.DisplayOutline = True
    GoTo Finish

Failed:
    errorText = Err.Description
Finish:
    If savedState Then
        On Error Resume Next
        Application.Calculation = oldCalc
        Application.EnableEvents = oldEvents
        Application.ScreenUpdating = oldScreen
        Application.EnableCancelKey = oldCancel
        On Error GoTo 0
    End If
    If Len(errorText) > 0 Then
        MsgBox "Макрос остановлен: " & errorText, vbExclamation
    Else
        MsgBox "Группировка по последнему блоку готова.", vbInformation
    End If
End Sub

Private Function LB_Text(ByVal value As Variant) As String
    If IsEmpty(value) Or IsNull(value) Then Exit Function
    If VarType(value) <> vbString And IsNumeric(value) Then
        LB_Text = Trim$(Str$(CDbl(value)))
    Else
        LB_Text = Trim$(Replace$(Replace$(Replace$(Replace$( _
            CStr(value), ChrW(160), " "), vbTab, " "), vbCr, " "), vbLf, " "))
    End If
End Function

Private Function LB_IndexLevel(ByVal index As String, ByVal rowNumber As Long) As Long
    Dim parts As Variant, part As Variant
    If StrComp(index, "Изделие", vbTextCompare) = 0 Then Exit Function
    index = Replace$(index, ",", ".")
    If Right$(index, 1) = "." Then index = Left$(index, Len(index) - 1)
    If Len(index) = 0 Then LB_Stop "Некорректный индекс в строке " & rowNumber & "."
    parts = Split(index, ".")
    For Each part In parts
        part = Trim$(CStr(part))
        If Len(part) = 0 Or part Like "*[!0-9]*" Then
            LB_Stop "Некорректный индекс в строке " & rowNumber & "."
        End If
    Next part
    If UBound(parts) = 0 Then
        If Len(Replace$(Trim$(CStr(parts(0))), "0", "")) = 0 Then Exit Function
    End If
    LB_IndexLevel = UBound(parts) + 1
End Function

Private Sub LB_Stop(ByVal message As String)
    Err.Raise vbObjectError + 5200, "Группировка последнего блока", message
End Sub

' Установка и удаление команды группировки в контекстном меню.
Public Sub InstallProductGroupingMenu()
    Dim menuName As Variant, menu As Object, button As Object
    Dim macroName As String, errorText As String
    Dim cellMenuAdded As Boolean

    On Error GoTo Failed
    If ActiveWorkbook Is Nothing Then Exit Sub
    If Not (ActiveWorkbook Is ThisWorkbook) Then Exit Sub
    RemoveProductGroupingMenu
    macroName = "'" & Replace$(ThisWorkbook.Name, "'", "''") & "'!GroupProductHistory"

    For Each menuName In Array("Cell", "Row", "Column", "List Range Popup")
        Set menu = LB_GetMenu(CStr(menuName))
        If Not menu Is Nothing Then
            Set button = menu.Controls.Add(Type:=1, Before:=1, Temporary:=True)
            With button
                .Caption = "Группировать по индексам"
                .Tag = GROUP_MENU_TAG
                .Style = 2
                .OnAction = macroName
                .BeginGroup = False
            End With
            If CStr(menuName) = "Cell" Then cellMenuAdded = True
        End If
    Next menuName
    If Not cellMenuAdded Then LB_Stop "Не удалось получить контекстное меню ячеек."
    Exit Sub

Failed:
    errorText = Err.Description
    RemoveProductGroupingMenu
    MsgBox "Не удалось добавить пункт группировки: " & errorText, vbExclamation
End Sub

Public Sub RemoveProductGroupingMenu()
    Dim menuName As Variant, menu As Object, i As Long
    On Error Resume Next
    For Each menuName In Array("Cell", "Row", "Column", "List Range Popup")
        Set menu = LB_GetMenu(CStr(menuName))
        If Not menu Is Nothing Then
            For i = menu.Controls.Count To 1 Step -1
                If menu.Controls(i).Tag = GROUP_MENU_TAG Then menu.Controls(i).Delete
            Next i
        End If
    Next menuName
    On Error GoTo 0
End Sub

Private Function LB_GetMenu(ByVal menuName As String) As Object
    On Error Resume Next
    Set LB_GetMenu = Application.CommandBars(menuName)
    On Error GoTo 0
End Function
