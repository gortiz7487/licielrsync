﻿''----------------------------------------------------------------------------------------------
''
'' LicielRsync -  A multi-threaded interface for Rsync on Windows
'' By Arnaud Dovi - ad@heapoverflow.com
'' Rsync - http://rsync.samba.org
''
'' ModuleMain
''
'' Primary functions
''----------------------------------------------------------------------------------------------
Option Explicit On


Imports System.Globalization
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.IO
Imports System.ComponentModel
Imports System.Text

Module ModuleMain

    Private Delegate Sub DelegateInvoke(ByVal var() As Object)

    Public FirstLoad As Boolean = True, Progress As Boolean = False
    Public RsyncDirectory As String = My.Application.Info.DirectoryPath & "\", RsyncPath As String = "", LastLine As String = "", CurrentFile As String = ""
    Public RsyncPaths As New Hashtable, FileSizes As New Hashtable
    Public Processus As Process
    Public ProcessusSuspended As Boolean = False
    Public GlobalSize As Long = -1, GlobalSizeSent As Long = 0, CurrentSize As Long = -1, CurrentProgress As Integer = 0

    ReadOnly Fm As FrameMain = FrameMain

    Private Const NotEmptyPattern As String = "\S+"
    Private Const ProgressPattern As String = "(\d+)\%.*(\d{2}|\d{1}):(\d{2}|\d{1}):(\d{2}|\d{1})\s*(\(.*\))*$"
    Private Const WinPathPattern As String = "^(([a-zA-Z]):\\(.*)|(\\\\))"

    ''--------------------------------------------------------------------
    '' Main
    ''
    '' Entry point
    ''--------------------------------------------------------------------

    Public Sub Main()
        If Not FirstLoad Then Exit Sub
        FirstLoad = False
        'Dim config As Configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal)
        'MsgBox(config.FilePath)
        'End
        Fm.Icon = CType(My.Resources.ResourceManager.GetObject("LicielRsync", New CultureInfo("en")), Icon)
        Fm.StatusBarText.Text = String.Empty
        ''
        '' Old configs importation
        ''
        If My.Settings.ForceUpdate Then
            My.Settings.Upgrade()
            My.Settings.ForceUpdate = False
            My.Settings.Save()
        End If
        ''
        '' Loading config
        ''
        InitializeOptions()
        LoadConfig(True)
        ''
        '' Detect present and usable rsync
        ''
        InitializeRsyncs()
    End Sub

    ''--------------------------------------------------------------------
    '' GetFileOrDirectory
    ''
    '' Path and File selection
    ''--------------------------------------------------------------------

    Public Function GetDirectory(ByVal controlText As String)
        Dim dlgResult As DialogResult = Fm.FolderBrowserDialog.ShowDialog()
        If dlgResult = Windows.Forms.DialogResult.OK Then Return Fm.FolderBrowserDialog.SelectedPath & "\"
        Return controlText
    End Function

    ''--------------------------------------------------------------------
    '' FormatPath
    ''
    '' Convert Windows to Cygwin paths
    ''--------------------------------------------------------------------

    Private Function FormatPath(ByVal path As String)
        Dim matchObj As Match = Regex.Match(path, WinPathPattern)
        If Not matchObj.Success Then Return path
        Dim driveLetter As String = matchObj.Groups(2).Value
        If driveLetter = "" Then Return path.Replace("\", "/") ' -- unc path \\server\share
        Dim fullPath As String = matchObj.Groups(3).Value.Replace("\", "/")
        Return "/cygdrive/" & driveLetter & "/" & fullPath ' -- windows path c:\directory\
    End Function

    ''--------------------------------------------------------------------
    '' BuildArgument
    ''
    '' Command-line construction
    ''--------------------------------------------------------------------

    Private Function BuildArgument(ByVal dryrun As Boolean)
        Return BuildOptions(dryrun) & """" & FormatPath(Fm.TextBoxSrc.Text) & """ " & """" & FormatPath(Fm.TextBoxDst.Text) & """"
    End Function

    ''--------------------------------------------------------------------
    '' InvokeChangeControl
    ''
    '' Stub function used to update frames by other threads
    ''--------------------------------------------------------------------

    Private Sub InvokeChangeControl(ByVal obj() As Object)
        Try
            Dim control As Object = obj(0)
            Select Case control.Name
                Case Fm.TextBoxLogs.Name
                    control.Lines = obj(1).ToArray()
                    control.SelectionStart = control.TextLength
                    control.ScrollToCaret()
                Case Fm.TextBoxErrors.Name
                    control.AppendText(obj(1))
                    control.SelectionStart = control.TextLength
                    control.ScrollToCaret()
                Case Fm.ButtonExec.Name
                    Dim active As Boolean = obj(1)
                    control.Enabled = active
                    Fm.ButtonTest.Enabled = active
                    Fm.ButtonPause.Enabled = Not active
                    Fm.ButtonStop.Enabled = Not active
                Case Fm.ProgressBar.Name
                    Fm.ProgressBarText.Text = Math.Round(obj(1)) & "%"
                    control.Value = obj(1)
            End Select
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    ''--------------------------------------------------------------------
    '' ThreadReadStreams
    ''
    '' Stub function used to read the standard and error streams
    ''--------------------------------------------------------------------

    Private Sub ThreadReadStreams(ByVal arg As Object)
        Dim stdLog = arg(1)
        Dim line As String
        Try
            Select Case stdLog
                Case True
                    Dim textBoxLogsLines As New List(Of String), carriageReturn As Boolean = False, progressMatch As Object
                    Do While Not arg(0).EndOfStream
                        line = arg(0).ReadLine()
                        If Not Regex.Match(line, NotEmptyPattern).Success Then Continue Do
                        If Progress AndAlso CurrentFile = "" Then
                            Try
                                If FileSizes.ContainsKey(line) Then
                                    CurrentSize = FileSizes(line)
                                    CurrentFile = line
                                End If
                            Catch
                            End Try
                        End If
                        If Not carriageReturn Then
                            textBoxLogsLines.Add(line)
                        Else
                            textBoxLogsLines(textBoxLogsLines.Count - 1) = line
                        End If
                        progressMatch = Regex.Match(line, ProgressPattern)
                        carriageReturn = progressMatch.Success AndAlso progressMatch.Groups(5).Value = "" 'progressMatch.Success
                        Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.TextBoxLogs, textBoxLogsLines}})
                        If Progress AndAlso CurrentFile <> "" AndAlso progressMatch.Success Then
                            CurrentProgress = CInt(progressMatch.Groups(1).Value)
                            UpdateProgress(CurrentProgress >= 100)
                        End If
                    Loop
                Case False
                    Do While Not arg(0).EndOfStream
                        line = arg(0).ReadLine()
                        If Not Regex.Match(line, NotEmptyPattern).Success Then Continue Do
                        Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.TextBoxErrors, line & vbCrLf}})
                    Loop
            End Select
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    ''--------------------------------------------------------------------
    '' UpdateProgress
    ''
    '' Update the progress bar
    ''--------------------------------------------------------------------

    Private Sub UpdateProgress(Optional ByVal done As Boolean = False)
        Dim sentData As Long = CurrentSize * (CurrentProgress / 100)
        Dim percent As Long = 100 / (GlobalSize / (GlobalSizeSent + sentData))
        If percent > 100 Then percent = 100
        Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.ProgressBar, percent}})
        If done Then
            GlobalSizeSent += CurrentSize
            CurrentFile = ""
            CurrentSize = -1
        End If
    End Sub


    ''--------------------------------------------------------------------
    '' CacheSizes
    ''
    '' Global size calculation for progress bar
    ''--------------------------------------------------------------------

    Private Sub CacheSizes()
        Dim dir As String = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("srcpath")
        If Not Regex.Match(dir, WinPathPattern).Success Then Exit Sub
        Dim length As Long
        For Each file In Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories)
            length = New FileInfo(file).Length
            FileSizes(file.Replace(dir, "").Replace("\", "/")) = length
            GlobalSize += length
        Next
    End Sub

    ''--------------------------------------------------------------------
    '' ThreadProcessStart
    ''
    '' Start process and read output streams with new threads
    ''--------------------------------------------------------------------

    Public Sub ThreadProcessStart(ByVal obj As Object)
        Try
            Dim arg As String = BuildArgument(obj(1))
            Dim settingsHideWnd As Boolean = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("hidewnd")
            Dim settingsRedir As Boolean = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("redir")
            Progress = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress")
            Processus = New Process()
            Processus.StartInfo.FileName = obj(0)
            Processus.EnableRaisingEvents = False
            Processus.StartInfo.UseShellExecute = False
            Processus.StartInfo.CreateNoWindow = settingsHideWnd
            Processus.StartInfo.RedirectStandardOutput = settingsRedir
            Processus.StartInfo.RedirectStandardError = settingsRedir
            If settingsRedir Then
                Processus.StartInfo.StandardOutputEncoding = Encoding.UTF8
                Processus.StartInfo.StandardErrorEncoding = Encoding.UTF8
            End If
            Processus.StartInfo.WindowStyle = If(settingsHideWnd, ProcessWindowStyle.Hidden, ProcessWindowStyle.Normal)
            If arg <> "" Then Processus.StartInfo.Arguments = arg
            CacheSizes()
            Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.ButtonExec, False}})
            Processus.Start()
            If settingsRedir Then
                Dim srStd As StreamReader = Processus.StandardOutput
                Dim srErr As StreamReader = Processus.StandardError
                Dim thdStd = New Thread(AddressOf ThreadReadStreams)
                thdStd.IsBackground = True
                thdStd.Start({srStd, True})
                Dim thdErr = New Thread(AddressOf ThreadReadStreams)
                thdErr.IsBackground = True
                thdErr.Start({srErr, False})
            End If
            Processus.WaitForExit()
            Processus.Close()
            Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.ButtonExec, True}})
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    ''--------------------------------------------------------------------
    '' BuildOptions
    ''
    '' Command-line options
    ''--------------------------------------------------------------------

    Private Function BuildOptions(ByVal dryrun As Boolean)
        Try
            Dim str As String = ""
            If dryrun Then str = "--dry-run "
            For Each opt As Object In My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")
                Dim key = opt.Key
                If opt.Value Then
                    Select Case key
                        Case "-v"
                            key = "-" & New String("v"c, CInt(My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("-v")))
                    End Select
                    str = str & key & " "
                End If
            Next
            If Regex.Match(My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("customoptions"), "\S+").Success Then str = String.Concat(str, My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("customoptions") & " ")
            Return str
        Catch ex As Exception
            Return MsgBox(ex.ToString)
        End Try
    End Function

    ''--------------------------------------------------------------------
    '' UpdateStatusBarCommand
    ''
    '' Update the command line shown on status bar
    ''--------------------------------------------------------------------

    Public Sub UpdateStatusBarCommand(ByVal dryrun As Boolean)
        Fm.StatusBarText.Text = If(Not My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("showcmd"), "", BuildOptions(dryrun))
    End Sub

    ''--------------------------------------------------------------------
    '' InitializeRsyncs
    ''
    '' Detect rsync that are presents and very cygwin is correctly setup
    ''--------------------------------------------------------------------

    Private Sub InitializeRsyncs()
        Dim items(100) As String
        Dim i As Integer = 0
        Dim rsyncName As String
        For Each dir As String In Directory.GetDirectories(RsyncDirectory)
            If Not Regex.Match(Path.GetFileName(dir), "^rsync-\d").Success Then Continue For
            If Not File.Exists(dir & "\bin\rsync.exe") Then Continue For
            Try
                If Not File.Exists(dir & "\etc\fstab") Then
                    Directory.CreateDirectory(dir & "\etc")
                    Dim sw As StreamWriter = File.AppendText(dir & "\etc\fstab")
                    sw.Write("none /cygdrive cygdrive binary,posix=0,user,noacl 0 0")
                    sw.Close()
                End If
                If Not Directory.Exists(dir & "\tmp") Then Directory.CreateDirectory(dir & "\tmp")
            Catch
            End Try
            rsyncName = Regex.Replace(Path.GetFileName(dir), "(?<first>\-\d{2}\-\d{2}\-\d{4})(?<last>\-\w+)", "${first}")
            If rsyncName = "rsync-3.0.8-ntstreams" Then
                RsyncPaths(rsyncName & " (unofficial)") = dir
                Continue For
            End If
            items(i) = rsyncName
            RsyncPaths(items(i)) = dir
            i += 1
        Next
        If i = 0 Then Exit Sub
        If RsyncPaths.ContainsKey("rsync-3.0.8-ntstreams (unofficial)") Then
            items(i) = "rsync-3.0.8-ntstreams (unofficial)"
            i += 1
        End If
        Array.Resize(items, i)
        Fm.ComboRsync.Items.AddRange(items)
        Fm.ComboRsync.SelectedIndex = 0
    End Sub

    ''--------------------------------------------------------------------
    '' InitializeOptions
    ''
    '' Default options initialization
    ''--------------------------------------------------------------------

    Private Sub InitializeOptions()
        Try
            If My.Settings.ProfilesList Is Nothing Then
                My.Settings.ProfilesList = New List(Of String)
                My.Settings.ProfilesList.Add(My.Settings.CurrentProfile)
            End If
            If My.Settings.P Is Nothing Then My.Settings.P = New Hashtable()
            If My.Settings.P(My.Settings.CurrentProfile) Is Nothing Then My.Settings.P(My.Settings.CurrentProfile) = New Hashtable()
            If My.Settings.P(My.Settings.CurrentProfile)("OptionsVar") Is Nothing Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar") = New Hashtable()
            If My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch") Is Nothing Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch") = New Hashtable()
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress") = True
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-r") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-r") = True
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-t") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-t") = True
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("-v") Is String Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("-v") = "1"
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-v") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-v") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--backup-nt-streams --restore-nt-streams") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--backup-nt-streams --restore-nt-streams") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("srcpath") Is String Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("srcpath") = ""
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("dstpath") Is String Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("dstpath") = ""
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("showcmd") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("showcmd") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("hidewnd") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("hidewnd") = True
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("redir") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("redir") = True
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("customoptions") Is String Then My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("customoptions") = ""
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-p") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-p") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-o") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-o") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-g") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-g") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--no-whole-file") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--no-whole-file") = True
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--modify-window=2") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--modify-window=2") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--delete") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--delete") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--ignore-existing") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--ignore-existing") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-u") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-u") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--size-only") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--size-only") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-x") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-x") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-h") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-h") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-c") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-c") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--existing") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--existing") = False
            If Not TypeOf My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--ignore-times") Is Boolean Then My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--ignore-times") = False
            My.Settings.Save()
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    ''--------------------------------------------------------------------
    '' LoadConfig
    ''
    '' Default options load
    ''--------------------------------------------------------------------

    Private Sub LoadConfig(Optional ByVal init As Boolean = False)
        Try
            If init Then
                For Each _text In My.Settings.ProfilesList
                    Fm.ComboProfiles.Items.AddRange(New Object() {_text})
                Next
                Fm.ComboProfiles.SelectedIndex = Fm.ComboProfiles.FindStringExact(My.Settings.CurrentProfile)
            End If
            Fm.CbDate.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-t")
            Fm.CbRecurse.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-r")
            Fm.CbVerbose.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-v")
            Fm.CbProgress.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress")
            Fm.CbPerm.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-p")
            Fm.CbOwner.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-o")
            Fm.CbGroup.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-g")
            Fm.CbDelta.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--no-whole-file")
            Fm.CbWinCompat.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--modify-window=2")
            Fm.CbDelete.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--delete")
            Fm.CbExisting.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--ignore-existing")
            Fm.CbNewer.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-u")
            Fm.CbSizeOnly.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--size-only")
            Fm.CbFS.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-x")
            Fm.CbReadable.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-h")
            Fm.CbChecksum.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("-c")
            Fm.CbExistingOnly.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--existing")
            Fm.CbIgnoreTimes.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--ignore-times")
            Fm.CbPermWin.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--backup-nt-streams --restore-nt-streams")
            Fm.CbEnglish.Checked = My.Settings.Locales = "English"
            Fm.CbFrench.Checked = My.Settings.Locales = "French"
            Fm.CbEnglish.CheckOnClick = Not Fm.CbEnglish.Checked
            Fm.CbFrench.CheckOnClick = Not Fm.CbFrench.Checked
            Fm.ComboVerbose.Enabled = Fm.CbVerbose.Checked
            Fm.ComboVerbose.Text = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("-v")
            Fm.TextBoxSrc.Text = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("srcpath")
            Fm.TextBoxDst.Text = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("dstpath")
            Fm.TextBoxOptions.Text = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("customoptions")
            Fm.CbShowCmd.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("showcmd")
            Fm.CbHideWindows.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("hidewnd")
            Fm.CbRedir.Checked = My.Settings.P(My.Settings.CurrentProfile)("OptionsVar")("redir")
        Catch ex As Exception
            MsgBox(ex.ToString)
        End Try
    End Sub

    ''--------------------------------------------------------------------
    '' ChangeLanguage
    ''
    '' Change language in real-time
    ''--------------------------------------------------------------------

    Public Sub ChangeLanguage(ByVal lang As String)
        Dim cultureInfo = If(lang = "", Nothing, New CultureInfo(lang))
        Dim resources As ComponentResourceManager = New ComponentResourceManager(Fm.GetType)
        resources.ApplyResources(Fm, "$this", cultureInfo)
        For Each c In Fm.Controls
            resources.ApplyResources(c, c.Name, cultureInfo)
            If resources.GetString(c.Name & ".ToolTip") <> "" Then Fm.ToolTip1.SetToolTip(c, resources.GetString(c.Name & ".ToolTip", cultureInfo))
            For Each d As Control In c.Controls
                resources.ApplyResources(d, d.Name, cultureInfo)
                If resources.GetString(d.Name & ".ToolTip") <> "" Then Fm.ToolTip1.SetToolTip(d, resources.GetString(d.Name & ".ToolTip", cultureInfo))
                For Each e As Control In d.Controls
                    resources.ApplyResources(e, e.Name, cultureInfo)
                    If resources.GetString(e.Name & ".ToolTip") <> "" Then Fm.ToolTip1.SetToolTip(e, resources.GetString(e.Name & ".ToolTip", cultureInfo))
                    For Each f As Control In e.Controls
                        resources.ApplyResources(f, f.Name, cultureInfo)
                        If resources.GetString(f.Name & ".ToolTip") <> "" Then Fm.ToolTip1.SetToolTip(f, resources.GetString(f.Name & ".ToolTip", cultureInfo))
                    Next f
                Next e
            Next d
            If TypeOf c Is MenuStrip Then
                For Each i As ToolStripMenuItem In c.Items
                    resources.ApplyResources(i, i.Name, cultureInfo)
                    For Each j As ToolStripMenuItem In i.DropDownItems
                        resources.ApplyResources(j, j.Name, cultureInfo)
                        For Each k As ToolStripMenuItem In j.DropDownItems
                            resources.ApplyResources(k, k.Name, cultureInfo)
                        Next k
                    Next j
                Next i
            End If
        Next c
    End Sub

    ''--------------------------------------------------------------------
    '' ResetProgress
    ''
    '' Reset progress bar datas
    ''--------------------------------------------------------------------

    Public Sub ResetProgress()
        Fm.ProgressBar.Visible = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress")
        Fm.ProgressBarText.Visible = My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress")
        If Not My.Settings.P(My.Settings.CurrentProfile)("OptionsSwitch")("--progress") Then Exit Sub
        Fm.ProgressBar.Value = 0
        Fm.ProgressBarText.Text = "0%"
        GlobalSize = -1
        GlobalSizeSent = 0
        CurrentSize = -1
        CurrentProgress = 0
        CurrentFile = ""
        FileSizes = New Hashtable
    End Sub

    ''--------------------------------------------------------------------
    '' LoadProfile
    ''
    '' Profiles load
    ''--------------------------------------------------------------------

    Public Sub LoadProfile(ByVal profileName As String)
        If profileName = My.Settings.CurrentProfile Then Exit Sub
        My.Settings.CurrentProfile = profileName
        My.Settings.Save()
        InitializeOptions()
        LoadConfig()
    End Sub

    'Private Sub WriteFile(ByRef t As String, ByVal fichier As String)
    '    Try
    '        Dim sw As StreamWriter = File.AppendText(fichier)
    '        sw.Write(t)
    '        sw.Close()
    '    Catch
    '    End Try
    'End Sub

End Module

'Private Sub ThreadReadStreams(ByVal arg As Object)
'    Dim stdLog = arg(1)
'    Dim line As String
'    Try
'        Select Case stdLog
'            Case True
'                Do While Not arg(0).EndOfStream
'                    line = arg(0).ReadLine()
'                    Dim matchProgress As Match = Regex.Match(line, ProgressPattern)
'                    Dim matchLastProgress As Match = Regex.Match(LastLine, ProgressPattern)
'                    If Progress AndAlso CurrentFile = "" Then
'                        Try
'                            If FileSizes.ContainsKey(line) Then
'                                CurrentSize = FileSizes(line)
'                                CurrentFile = line
'                            End If
'                        Catch
'                        End Try
'                    End If
'                    If matchProgress.Success AndAlso matchLastProgress.Success AndAlso matchLastProgress.Groups(5).Value = "" Then
'                        Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.TextBoxLogs.Name, line & vbCrLf, 0, LastLine}})
'                    Else
'                        If Regex.Match(line, NotEmptyPattern).Success Then Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.TextBoxLogs.Name, line & vbCrLf, 1}})
'                    End If
'                    If Progress AndAlso CurrentFile <> "" AndAlso matchProgress.Success Then
'                        CurrentProgress = CInt(matchProgress.Groups(1).Value)
'                        UpdateProgress(CurrentProgress >= 100)
'                    End If
'                    LastLine = line & vbCrLf
'                Loop
'            Case False
'                Do While Not arg(0).EndOfStream
'                    line = arg(0).ReadLine()
'                    Fm.BeginInvoke(New DelegateInvoke(AddressOf InvokeChangeControl), New Object() {New Object() {Fm.TextBoxErrors.Name, line & vbCrLf}})
'                Loop
'        End Select
'    Catch ex As Exception
'        MsgBox(ex.ToString)
'    End Try
'End Sub

'Private Sub InvokeChangeControl(ByVal obj() As Object)
'    Try
'        Dim line As String = obj(1)
'        Select Case obj(0)
'            Case Fm.TextBoxLogs.Name
'                Select Case obj(2)
'                    Case 0
'                        Fm.TextBoxLogs.SuspendLayout()
'                        Fm.TextBoxLogs.Text = Regex.Replace(Fm.TextBoxLogs.Text, obj(3), line)
'                        Fm.TextBoxLogs.SelectionStart = Fm.TextBoxLogs.Text.Length
'                        Fm.TextBoxLogs.ScrollToCaret()
'                        Fm.TextBoxLogs.ResumeLayout()
'                    Case 1
'                        Fm.TextBoxLogs.AppendText(line)
'                End Select
'            Case Fm.TextBoxErrors.Name
'                Fm.TextBoxErrors.AppendText(line & vbCrLf)
'            Case Fm.ButtonExec.Name
'                Dim active As Boolean = obj(1)
'                Fm.ButtonExec.Enabled = active
'                Fm.ButtonTest.Enabled = active
'                Fm.ButtonPause.Enabled = Not active
'                Fm.ButtonStop.Enabled = Not active
'            Case Fm.ProgressBar.Name
'                Fm.ProgressBarText.Text = Math.Round(obj(1)) & "%"
'                Fm.ProgressBar.Value = obj(1)
'        End Select
'    Catch ex As Exception
'        MsgBox(ex.ToString)
'    End Try
'End Sub
