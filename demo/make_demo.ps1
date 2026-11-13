<#
.SYNOPSIS
  Makes a narrated demo video of Parallel Passages Editor (MP4, 1920x1080), following
  tools\demo_video\Demo_Video_Standard.md.
  1. Starts ParallelPassagesEditor.exe (its UI-test build, which runs without administrator rights:
     Windows does not let a normal program drive an administrator's window) on a copy of Paratext's
     stock ParallelPassages.xml and drives it through UI
     Automation patterns (Value, Invoke, SelectionItem, ExpandCollapse, Scroll, Transform; WPF, so
     there are no child windows to post messages to). The mouse is never moved. The window is
     captured after each step.
  2. Draws each frame: a headline, the window with orange boxes round the controls used and a
     pointer, and the narration as subtitles.
  3. Speaks the narration (Azure AI Speech, see azure_tts.ps1) and joins the steps with ffmpeg.
  The text is in narration.json (UTF-8). This file stays ASCII.

.EXAMPLE
  .\make_demo.ps1 -Lang en -NoVoice
#>
param(
    [string]$Lang = 'en',
    [string]$Work = '',
    # Paratext's own file, read only: the stock copy kept by the editor's first save (.xmlorg), else
    # the file itself.
    [string]$Source = 'C:\Program Files\Paratext 9\ParallelPassages\ParallelPassages.xmlorg',
    # The path the window's file bar shows in the pictures (the file Paratext reads).
    [string]$ShownPath = 'C:\Program Files\Paratext 9\ParallelPassages\ParallelPassages.xml',
    # The editor's UI-test build (asInvoker manifest; never deployed).
    [string]$Exe = '',
    # An Azure voice (a name ending in 'Neural', e.g. en-US-JennyNeural; key and region from
    # AZURE_SPEECH_KEY / AZURE_SPEECH_REGION, see azure_tts.ps1) or a Windows voice (System.Speech).
    [string]$Voice = '',
    [int]$Rate = -1,                 # Windows voices: -10..10
    [string]$AzureRate = '0%',       # Azure voices: e.g. '-5%'
    # ffmpeg.exe to use; by default a newer GPL build in C:\ffmpeg-*, else the one on PATH.
    [string]$FFmpeg = '',
    # Use the screenshots of the last capture in $Work (no window is driven): for a voiced build or
    # a narration change after a -NoVoice test. The capture needs about 40 seconds without typing.
    [switch]$Reuse,
    # Only capture the window (then build with -Reuse): several languages can be captured in one
    # quiet-keyboard moment.
    [switch]$CaptureOnly,
    # Zoom into the boxed controls in steps without cues (written as ..._zoom.mp4).
    [switch]$Zoom,
    # No narration: each step stays up as long as its subtitle takes to read (for testing).
    [switch]$NoVoice,
    # Background music (looped, kept soft; lowered further while the voice speaks). '' = none.
    [string]$Music = 'C:\Users\Pleckrone\Dropbox\Camtasia work\Background Music Files\Aetheric - Coconut Kind of Love (freetouse.com).mp3',
    [double]$MusicVolume = 0.30,        # the music on its own, at the start (the standard)
    [double]$MusicUnderVoice = 0.025,    # from the narration on (the standard)
    [double]$CoverIntro = 3.5,          # seconds of music alone on the cover before the narration
    # The cover's second line, under the plugin's name (narration.json, step "title").
    [string]$Series = 'Plugins by Phil',
    # Dissolve between steps, and dip to black round the slides (seconds). This window has no
    # count line, so steps always dissolve (the standard, section 6); $Wipe is kept for that rule.
    [double]$Dissolve = 1.0,
    [double]$Wipe = 2.0,
    [double]$Dip = 1.4,
    # How many steps each pass of the video join takes (fewer = less memory).
    [int]$JoinSteps = 6
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $Exe) { $Exe = Join-Path (Split-Path $here) 'ParallelPassagesEditor\bin\UiTest\ParallelPassagesEditor.exe' }
if (-not (Test-Path -LiteralPath $Source)) { $Source = $Source -replace 'org$', '' }
if (-not $Work) { $Work = Join-Path $env:TEMP "parallelpassages_demo_$Lang" }
if (-not $Voice) { $Voice = @{ 'en' = 'en-US-JennyNeural'; 'es' = 'es-MX-JorgeNeural'; 'fr' = 'fr-FR-RemyMultilingualNeural' }[$Lang] }   # the standard's voices
$azure = $Voice -match 'Neural$'
$musicOn = $Music -and (Test-Path -LiteralPath $Music)
if ($Music -and -not $musicOn) { "music file not found, none used: $Music" }
$es = $Lang -like 'es*'; $isFr = $Lang -like 'fr*'
# The window's (and the slides') text in the video's language: English, Spanish, French
# (the editor's own words are in ParallelPassagesEditor\Resources\Strings.<lang>.xaml).
function T($en, $sp, $frText) { if ($es) { $sp } elseif ($isFr -and $null -ne $frText) { $frText } else { $en } }

$narr = Get-Content -LiteralPath (Join-Path $here 'narration.json') -Raw -Encoding UTF8 | ConvertFrom-Json
function Text($id) {
    $t = ($narr.steps | Where-Object { $_.id -eq $id } | Select-Object -First 1).$Lang
    if (-not $t) { throw "narration.json has no '$Lang' text for step $id" }
    $t
}
# Box cues in the narration (the standard, section 8). "{1}" before a phrase shows box 1 (the
# step's boxes, in order) from there on, "{0,2}" boxes 0 and 2, "{}" none; the pointer goes on the
# first box named. Before the first cue no box is shown. A text without cues shows all its boxes
# the whole time. Cues are left out of the subtitles and the voice.
$cueRx = '\{\s*(\d+(\s*,\s*\d+)*)?\s*\}'
function Clean($raw) { if ($raw) { [regex]::Replace($raw, $cueRx, '') } else { $raw } }
# The parts of a step: @{ Pos (in the clean text); Show (box numbers) }, or none without cues.
function Cues($raw) {
    $clean = ''; $marks = @(); $last = 0
    if (-not $raw) { return , @() }
    foreach ($m in [regex]::Matches($raw, $cueRx)) {
        $clean += $raw.Substring($last, $m.Index - $last)
        $show = @()
        if ($m.Groups[1].Success) { $show = @($m.Groups[1].Value -split '\s*,\s*' | ForEach-Object { [int]$_ }) }
        $marks += [pscustomobject]@{ Pos = $clean.Length; Show = $show }
        $last = $m.Index + $m.Length
    }
    if ($marks.Count -and $marks[0].Pos -gt 0) { $marks = @([pscustomobject]@{ Pos = 0; Show = @() }) + $marks }
    , $marks
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms, System.Speech
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DmWin {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    /// <summary>Per-monitor aware, so window rectangles, DWM frames and screen copies all use
    /// physical pixels on monitors with different scales.</summary>
    public static void BeAware() { try { if (SetProcessDpiAwarenessContext((IntPtr)(-4))) return; } catch { } SetProcessDPIAware(); }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out Rect r, int size);
    /// <summary>The visible frame (without the invisible resize borders), in screen pixels.</summary>
    public static Rect Frame(IntPtr h) {
        Rect r;
        if (DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(Rect))) != 0) GetWindowRect(h, out r);
        return r;
    }
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, int flags);
    /// <summary>Keeps a window above all others (no move, no size), so screen copies show it.</summary>
    public static void TopMost(IntPtr h) { SetWindowPos(h, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); }
    /// <summary>Moves a window (no size change, no activation).</summary>
    public static void MoveTo(IntPtr h, int x, int y) { SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004 | 0x0010); }
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    /// <summary>A button click posted to a Win32 button (BM_CLICK).</summary>
    public static void Click(IntPtr h) { PostMessage(h, 0xF5, IntPtr.Zero, IntPtr.Zero); }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, int flags, IntPtr env, string dir, ref StartupInfo si, out ProcessInfo pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    /// <summary>Starts a program whose first window is shown without being activated
    /// (SW_SHOWNOACTIVATE), so it does not take the keyboard from whatever the user is typing in.</summary>
    public static int StartNoActivate(string exe, string args) {
        var si = new StartupInfo(); si.cb = Marshal.SizeOf(typeof(StartupInfo));
        si.dwFlags = 1; si.wShowWindow = 4;
        ProcessInfo pi;
        if (!CreateProcess(exe, new System.Text.StringBuilder("\"" + exe + "\" " + args), IntPtr.Zero, IntPtr.Zero, false, 0, IntPtr.Zero, null, ref si, out pi))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
        return pi.dwProcessId;
    }
}
'@
[DmWin]::BeAware()
$AE = [Windows.Automation.AutomationElement]
$Scope = [Windows.Automation.TreeScope]
$CT = [Windows.Automation.ControlType]
$root = $AE::RootElement

# ------------------------------------------------------------ the demo project
$shots = Join-Path $Work 'shots'
$frames = Join-Path $Work 'frames'
$stepsFile = Join-Path $Work 'steps.xml'
if ($Reuse) {
    if (-not (Test-Path -LiteralPath $stepsFile)) { throw "-Reuse: no capture in $Work (run once without -Reuse)" }
    $script:steps = @(Import-Clixml -LiteralPath $stepsFile)
    if (Test-Path $frames) { Remove-Item -Recurse -Force $frames }
    New-Item -ItemType Directory -Force $frames | Out-Null
    Get-ChildItem -LiteralPath $Work -File | Where-Object { $_.Name -ne 'steps.xml' } | Remove-Item -Force
    "reusing the capture of $((Get-Item -LiteralPath $stepsFile).LastWriteTime): $(($script:steps | ForEach-Object { $_.Id }) -join ' ')"
}
else {
if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
New-Item -ItemType Directory -Force $shots, $frames | Out-Null
"capturing: please do not type for about a minute (the editor's message boxes take the keyboard)" | Out-Host
# A copy of the stock file in the work folder (Paratext's own file is never written), and an empty
# folder named PDT15, passed as the project as the plugin does (the "Save this hidden list for project"
# box). The editor keeps its settings and the last saved hidden list (hidden.txt) in
# %APPDATA%\ParallelPassagesEditor, with no way to point it elsewhere: that folder is copied aside
# first, emptied for the capture, and put back afterwards (also on an error).
$xml = Join-Path $Work 'ParallelPassages.xml'
Copy-Item -LiteralPath $Source -Destination $xml
$stock = Join-Path $Work 'stock.xml'
Copy-Item -LiteralPath $Source -Destination $stock
$proj = Join-Path $Work 'PDT15'
New-Item -ItemType Directory -Force $proj | Out-Null
$appData = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'ParallelPassagesEditor'
$appSaved = Join-Path $Work 'appdata_saved'
New-Item -ItemType Directory -Force $appSaved | Out-Null
$script:appMoved = $false
if (Test-Path -LiteralPath $appData) {
    Get-ChildItem -LiteralPath $appData -File | Copy-Item -Destination $appSaved
    $script:appMoved = $true
    Get-ChildItem -LiteralPath $appData -File | Remove-Item -Force
}
"saved aside: $(@(Get-ChildItem -LiteralPath $appSaved -File | ForEach-Object { $_.Name }) -join ', ')"
function RemoveDemoProject {
    # Puts the editor's own folder back as it was.
    if (Test-Path -LiteralPath $appData) { Get-ChildItem -LiteralPath $appData -File | Remove-Item -Force }
    if ($script:appMoved) {
        Get-ChildItem -LiteralPath $appSaved -File | Copy-Item -Destination $appData
        $script:appMoved = $false
    }
    elseif ((Test-Path -LiteralPath $appData) -and -not @(Get-ChildItem -LiteralPath $appData -Force).Count) { Remove-Item -LiteralPath $appData -Force }
    "editor folder put back: $(@(Get-ChildItem -LiteralPath $appData -File -ErrorAction SilentlyContinue | ForEach-Object { $_.Name }) -join ', ')" | Out-Host
}

# ------------------------------------------------------------ driving the window
$script:proc = $null
$script:win = $null
trap {
    if ($script:proc -and -not $script:proc.HasExited) { $script:proc.Kill(); $script:proc.WaitForExit(5000) | Out-Null }
    RemoveDemoProject
    throw $_
}
function Launch {
    # As the plugin starts it: Paratext's file and the project folder.
    # Shown without being activated: keys typed elsewhere meanwhile must not reach it.
    $script:win = $null
    $script:proc = Get-Process -Id ([DmWin]::StartNoActivate($Exe, "`"$xml`" --project `"$proj`""))
    $byProcess = New-Object Windows.Automation.PropertyCondition ($AE::ProcessIdProperty, $script:proc.Id)
    for ($i = 0; $i -lt 100 -and -not $script:win; $i++) {
        Start-Sleep -Milliseconds 200
        $script:win = $root.FindFirst($Scope::Children, $byProcess)
    }
    if (-not $script:win) { throw 'window did not appear' }
    $h = [IntPtr]$script:win.Current.NativeWindowHandle
    # On a screen without the mouse cursor: hover effects would show in the pictures.
    $cursor = [Windows.Forms.Cursor]::Position
    $screen = [Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Bounds.Contains($cursor) } | Select-Object -First 1
    if ($screen) { [DmWin]::MoveTo($h, $screen.WorkingArea.X + 20, $screen.WorkingArea.Y + 20) }
    else { 'only one screen: park the mouse cursor away from the window' | Out-Host }
    [DmWin]::TopMost($h)
    Start-Sleep -Milliseconds 2500
    # A message box shown at start-up stays where the window was: put it over the window again.
    $d = Dialog
    if ($d) {
        $dh = [IntPtr]$d.Current.NativeWindowHandle; $wb = $script:win.Current.BoundingRectangle; $db = $d.Current.BoundingRectangle
        [DmWin]::MoveTo($dh, [int]($wb.X + ($wb.Width - $db.Width) / 2), [int]($wb.Y + ($wb.Height - $db.Height) / 2))
        [DmWin]::TopMost($dh)
        Start-Sleep -Milliseconds 600
    }
    "editor window active: $([DmWin]::GetForegroundWindow() -eq $h)" | Out-Host
}
# A message box of the editor (a top-level window of its process other than the main one), or $null.
function Dialog {
    $byPid = New-Object Windows.Automation.PropertyCondition ($AE::ProcessIdProperty, $script:proc.Id)
    $main = $script:win.Current.NativeWindowHandle
    $all = @($root.FindAll($Scope::Children, $byPid)) + @($script:win.FindAll($Scope::Children, (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, $CT::Window))))
    $all | Where-Object { $_.Current.NativeWindowHandle -ne $main -and $_.Current.ControlType -eq $CT::Window } | Select-Object -First 1
}
function ById($id, $under = $null) {
    if (-not $under) { $under = $script:win }
    $c = New-Object Windows.Automation.PropertyCondition ($AE::AutomationIdProperty, $id)
    $el = $under.FindFirst($Scope::Descendants, $c)
    if (-not $el) { throw "control $id not found" }
    $el
}
function OfType($under, $type) {
    @($under.FindAll($Scope::Descendants, (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, $type))))
}
function SetValue($el, $text) {
    $el.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text)
    Start-Sleep -Milliseconds 500
    $now = $el.GetCurrentPropertyValue([Windows.Automation.ValuePattern]::ValueProperty)
    if ($now -ne $text) { throw "a box reads '$now' instead of '$text' (was something typed meanwhile?)" }
}
# An editable combo box: its value, or the value of the text box inside it.
function SetCombo($combo, $text) {
    $edit = (OfType $combo $CT::Edit) | Select-Object -First 1
    if ($edit) { SetValue $edit $text } else { SetValue $combo $text }
}
function Invoke($el) {
    $el.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 900
}
function Expand($el) {
    $el.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 900
}
function SelectEl($el) {
    $el.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 900
}
# A tab of the window, by its header.
function Tab($name) {
    $t = OfType $script:win $CT::TabItem | Where-Object { $_.Current.Name -eq $name } | Select-Object -First 1
    if (-not $t) { throw "no tab '$name'" }
    $t
}
# A list (DataGrid): rows top to bottom, a row's cells left to right; the row whose first cell reads $text.
function Rows($grid) {
    $c = New-Object Windows.Automation.PropertyCondition ($AE::ClassNameProperty, 'DataGridRow')
    @($grid.FindAll($Scope::Descendants, $c)) | Where-Object { -not $_.Current.BoundingRectangle.IsEmpty } | Sort-Object { $_.Current.BoundingRectangle.Y }
}
function Cells($row) {
    $c = New-Object Windows.Automation.PropertyCondition ($AE::ClassNameProperty, 'DataGridCell')
    @($row.FindAll($Scope::Children, $c)) | Sort-Object { $_.Current.BoundingRectangle.X }
}
function RowOf($grid, $text) {
    $r = Rows $grid | Where-Object { $cs = Cells $_; $cs.Count -and $cs[0].Current.Name -eq $text } | Select-Object -First 1
    if (-not $r) { throw "no row '$text'" }
    $r
}
# A column of a list: its header plus the cells of the rows.
function ColumnEls($grid, $i) {
    $c = New-Object Windows.Automation.PropertyCondition ($AE::ClassNameProperty, 'DataGridColumnHeader')
    $heads = @($grid.FindAll($Scope::Descendants, $c)) | Where-Object { -not $_.Current.BoundingRectangle.IsEmpty -and $_.Current.Name } | Sort-Object { $_.Current.BoundingRectangle.X }
    $els = @($heads[$i])
    foreach ($r in Rows $grid) { $els += (Cells $r)[$i] }
    $els
}
# A text of the window, by its words.
function TextEl($like) {
    $t = OfType $script:win $CT::Text | Where-Object { $_.Current.Name -like $like } | Select-Object -First 1
    if (-not $t) { throw "no text like '$like'" }
    $t
}
# The add bar of a tab: the label before a control (the TextBlock just left of it, on its line).
function LabelOf($el) {
    $b = $el.Current.BoundingRectangle; $mid = $b.Y + $b.Height / 2
    OfType $script:win $CT::Text | Where-Object { $r = $_.Current.BoundingRectangle; -not $r.IsEmpty -and $r.Right -le $b.Left + 2 -and $r.Right -gt $b.Left - 60 -and [Math]::Abs($r.Y + $r.Height / 2 - $mid) -lt 8 } |
        Sort-Object { $_.Current.BoundingRectangle.Right } -Descending | Select-Object -First 1
}

# ------------------------------------------------------------ capturing a step
$script:steps = @()
# The window as it is on the screen (open menus included), in physical pixels.
function Capture([IntPtr]$h) {
    $f = [DmWin]::Frame($h)
    $bmp = New-Object Drawing.Bitmap ($f.R - $f.L), ($f.B - $f.T)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($f.L, $f.T, 0, 0, $bmp.Size)
    $g.Dispose()
    @{ Bmp = $bmp; Rect = $f }
}
# A box round some elements, in the capture's pixels (UI Automation gives physical pixels here).
function Box($els, $f) {
    $x1 = 1e9; $y1 = 1e9; $x2 = -1e9; $y2 = -1e9
    foreach ($e in $els) {
        if (-not $e) { continue }
        $b = $e.Current.BoundingRectangle
        if ($b.IsEmpty) { continue }
        $x1 = [Math]::Min($x1, $b.X); $y1 = [Math]::Min($y1, $b.Y)
        $x2 = [Math]::Max($x2, $b.X + $b.Width); $y2 = [Math]::Max($y2, $b.Y + $b.Height)
    }
    if ($x2 -lt $x1) { return $null }
    , @([int]($x1 - $f.L), [int]($y1 - $f.T), [int]($x2 - $x1), [int]($y2 - $y1))
}
# The file bar names the work copy; the picture shows the file Paratext reads instead (the plugin
# opens that one). Its text is drawn again in the bar's colours and font (Segoe UI Semibold, 14).
function ShowRealPath($cap) {
    $fh = ById 'FileHeaderText'
    $b = $fh.Current.BoundingRectangle
    if ($b.IsEmpty) { return }
    $x = [int]($b.X - $cap.Rect.L); $y = [int]($b.Y - $cap.Rect.T)
    $g = [Drawing.Graphics]::FromImage($cap.Bmp)
    # An open menu over the bar is left as it is ($script:pathExclude: screen rectangle).
    if ($script:pathExclude) {
        $e = $script:pathExclude
        $clip = New-Object Drawing.Rectangle ([int]($e.X - $cap.Rect.L - 3)), ([int]($e.Y - $cap.Rect.T - 3)), ([int]$e.Width + 10), ([int]$e.Height + 10)
        $g.SetClip($clip, [Drawing.Drawing2D.CombineMode]::Exclude)
    }
    $bg = $cap.Bmp.GetPixel([Math]::Max(0, $x - 4), $y + 2)
    $g.FillRectangle((New-Object Drawing.SolidBrush $bg), $x, $y, [int]$b.Width + 2, [int]$b.Height)
    $g.TextRenderingHint = 'ClearTypeGridFit'
    $px = [float]($b.Height * 14.0 / 18.62)
    $font = New-Object Drawing.Font 'Segoe UI Semibold', $px, ([Drawing.GraphicsUnit]::Pixel)
    $text = (T 'File: ' 'Archivo: ' 'Fichier : ') + $ShownPath
    $g.DrawString($text, $font, [Drawing.Brushes]::Black, [float]($x - $px * 0.12), [float]($y + $b.Height * 0.02))
    $g.Dispose()
}
# $groups: a list of element lists, one orange box each. $pointer: index of the box to point at, or -1.
# $pointEl: an element to put the pointer on instead (e.g. a menu item).
function Step($id, $groups, $pointer = -1, $pointEl = $null) {
    Start-Sleep -Milliseconds 400
    $main = Capture ([IntPtr]$script:win.Current.NativeWindowHandle)
    ShowRealPath $main
    $boxes = @()
    foreach ($grp in $groups) {
        $b = Box @($grp) $main.Rect
        if (-not $b) { throw "step $id : a box has no element on screen" }
        $boxes += , $b
    }
    $pointAt = $null
    if ($pointEl) { $pointAt = Box @($pointEl) $main.Rect }
    $file = Join-Path $shots "$id.png"
    $main.Bmp.Save($file, [Drawing.Imaging.ImageFormat]::Png)
    $main.Bmp.Dispose()
    # Rows = -1: no count line, so the joins between steps are always dissolves.
    $script:steps += [pscustomobject]@{ Id = $id; Shot = $file; Boxes = $boxes; Pointer = [int]$pointer; Dialog = $null; Rows = -1; PointAt = $pointAt }
    "captured $id$(if ([DmWin]::GetForegroundWindow() -eq [IntPtr]$script:win.Current.NativeWindowHandle) { ' (the editor window is active: keys typed now would reach it)' })"
}

# ------------------------------------------------------------ the demo
# A list's item (ListBoxItem) by its first text ("1CH", "Chapter 18"), and its tick box.
function Item($list, $text) {
    $c = New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, $CT::ListItem)
    foreach ($it in @($list.FindAll($Scope::Children, $c))) {
        $t = (OfType $it $CT::Text) | Select-Object -First 1
        if ($t -and $t.Current.Name -eq $text) { return $it }
    }
    throw "no item '$text'"
}
function ShowItem($it) { try { $it.GetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern).ScrollIntoView(); Start-Sleep -Milliseconds 500 } catch { } }
function AddSel($it) { $it.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).AddToSelection(); Start-Sleep -Milliseconds 900 }
function Tick($it) { ((OfType $it $CT::CheckBox) | Select-Object -First 1).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep -Milliseconds 900 }
function StatusEl { ById 'StatusText' }

Launch
$books = ById 'BookList'; $chapters = ById 'ChapterList'; $grid = ById 'EntryGrid'
$banner = TextEl (T 'Restart Paratext after saving*' 'Reinicie Paratext*' 'Red*marrez Paratext*')
Step 's01_open' @(@(ById 'FileHeaderText'), @($banner), @($books, $chapters, $grid), @(StatusEl)) -1

# One book: its chapters and its entries.
$ch1 = Item $books '1CH'
ShowItem $ch1
SelectEl $ch1
Step 's02_book' @(@($ch1), @($chapters), @($grid)) 0

# Hide 1 and 2 Chronicles: both selected, then Hide book.
$ch2 = Item $books '2CH'
AddSel $ch2
$hideBook = ById 'HideBookBtn'
Invoke $hideBook
Start-Sleep -Milliseconds 600
"status: $((StatusEl).Current.Name)"
if ((StatusEl).Current.Name -notmatch '^[1-9]') { throw "nothing hidden: $((StatusEl).Current.Name)" }
Step 's03_hidebook' @(@($ch1, $ch2), @($hideBook), @(StatusEl)) 1

# A passage left with fewer than 2 entries is hidden whole: 2 Samuel's parallels with Chronicles.
$sa2 = Item $books '2SA'
ShowItem $sa2
SelectEl $sa2
$noteHead = T 'Note' 'Nota' 'Remarque'
$rows = Rows $grid
$kept = @($rows | Where-Object { $cs = Cells $_; $cs.Count -ge 5 -and $cs[4].Current.Name })
"2SA rows on screen: $($rows.Count), hidden with their passage: $($kept.Count)"
if (-not $kept.Count) { throw 'no 2SA entry is hidden with its passage' }
$keptCells = @(); foreach ($r in $kept) { $keptCells += (Cells $r)[4] }
Step 's04_passage' @(@($sa2), $keptCells, @(StatusEl)) 1

# A chapter: Psalm 18, which is also 2 Samuel 22, ticked in the Chapters list.
$psa = Item $books 'PSA'
ShowItem $psa
SelectEl $psa
$c18 = $null
foreach ($name in @((T 'Chapter 18' "Cap$([char]0xED)tulo 18" 'Chapitre 18'), '18')) { try { $c18 = Item $chapters $name; break } catch { } }
if (-not $c18) { $c18 = @($chapters.FindAll($Scope::Children, (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, $CT::ListItem)))) | Where-Object { ((OfType $_ $CT::Text) | Select-Object -First 1).Current.Name -match '(^|\D)18$' } | Select-Object -First 1 }
if (-not $c18) { throw 'no chapter 18 in PSA' }
ShowItem $c18
SelectEl $c18
# (A UI Automation toggle of the tick box shows it ticked but does not run the box's Click.)
$hideCh = ById 'HideChaptersBtn'
$before = (StatusEl).Current.Name
Invoke $hideCh
Start-Sleep -Milliseconds 600
"after hiding the chapter: $((StatusEl).Current.Name)"
if ((StatusEl).Current.Name -eq $before) { throw 'the chapter was not hidden' }
Step 's05_chapter' @(@($c18), @($hideCh), @($grid)) 1

# The project's own list (the plugin passes the project).
$projCheck = ById 'ProjectOptionCheck'
Step 's06_project' @(, @($projCheck)) 0

# File > Save, with the File menu open.
$fileMenu = OfType $script:win $CT::MenuItem | Where-Object { ($_.Current.Name -replace '_', '') -eq (T 'File' 'Archivo' 'Fichier') } | Select-Object -First 1
if (-not $fileMenu) { throw 'File menu not found' }
$saveItem = $null
for ($try = 0; $try -lt 3 -and -not $saveItem; $try++) {
    Expand $fileMenu
    $menuItems = @($fileMenu.FindAll($Scope::Descendants, (New-Object Windows.Automation.PropertyCondition ($AE::ControlTypeProperty, $CT::MenuItem))))
    $saveItem = $menuItems | Where-Object { ($_.Current.Name -replace '_', '') -eq (T 'Save' 'Guardar' 'Enregistrer') -and -not $_.Current.BoundingRectangle.IsEmpty } | Select-Object -First 1
    if (-not $saveItem) { "the File menu did not open (try $($try + 1))" | Out-Host; try { $fileMenu.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { }; Start-Sleep -Milliseconds 800 }
}
if (-not $saveItem) { throw 'File > Save not found' }
$x1 = 1e9; $y1 = 1e9; $x2 = -1e9; $y2 = -1e9
foreach ($mi in $menuItems) { $r = $mi.Current.BoundingRectangle; if ($r.IsEmpty) { continue }; $x1 = [Math]::Min($x1, $r.X); $y1 = [Math]::Min($y1, $r.Y); $x2 = [Math]::Max($x2, $r.Right); $y2 = [Math]::Max($y2, $r.Bottom) }
$script:pathExclude = New-Object Windows.Rect $x1, $y1, ($x2 - $x1), ($y2 - $y1)
Step 's07_save' @(, @($fileMenu, $saveItem)) -1 $saveItem
$script:pathExclude = $null
Invoke $saveItem
Start-Sleep -Milliseconds 1200
# Saved: hidden lines are XML comments, the stock file is kept as .xmlorg, and the list as hidden.txt.
$hidden = @(Select-String -LiteralPath $xml -Pattern '^\s*<!--').Count
"saved: $hidden commented lines; xmlorg: $(Test-Path -LiteralPath ($xml + 'org')); hidden.txt: $(Test-Path -LiteralPath (Join-Path $appData 'hidden.txt'))"
if (-not $hidden -or -not (Test-Path -LiteralPath ($xml + 'org')) -or -not (Test-Path -LiteralPath (Join-Path $appData 'hidden.txt'))) { throw 'the save did not write what it should' }
Step 's08_saved' @(@(StatusEl), @($banner)) -1
$script:win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
$script:proc.WaitForExit(10000) | Out-Null
if (-not $script:proc.HasExited) { $script:proc.Kill(); $script:proc.WaitForExit(5000) | Out-Null }

# A Paratext update puts the stock file back: the editor offers the list saved last time.
Copy-Item -LiteralPath $stock -Destination $xml -Force
Launch
$askTitle = T 'Reapply Hidden List' 'Volver a aplicar la lista' 'R'
$ask = $null
for ($i = 0; $i -lt 20 -and -not $ask; $i++) {
    $byPid = New-Object Windows.Automation.PropertyCondition ($AE::ProcessIdProperty, $script:proc.Id)
    $ask = @($root.FindAll($Scope::Descendants, $byPid)) | Where-Object { $_.Current.ControlType -eq $CT::Window -and $_.Current.Name -like "$askTitle*" } | Select-Object -First 1
    if (-not $ask) { Start-Sleep -Milliseconds 500 }
}
if (-not $ask) { throw 'the editor did not offer the saved list' }
$wb = $script:win.Current.BoundingRectangle; $db = $ask.Current.BoundingRectangle
[DmWin]::MoveTo([IntPtr]$ask.Current.NativeWindowHandle, [int]($wb.X + ($wb.Width - $db.Width) / 2), [int]($wb.Y + ($wb.Height - $db.Height) / 2))
[DmWin]::TopMost([IntPtr]$ask.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 700
$askAll = @($ask.FindAll($Scope::Descendants, [Windows.Automation.Condition]::TrueCondition))
$askAll | ForEach-Object { "  dialog: $($_.Current.ControlType.ProgrammaticName) '$(($_.Current.Name -replace '\s+', ' '))'" } | Out-Host
$askText = $askAll | Where-Object { $_.Current.Name.Length -gt 30 } | Select-Object -First 1
if (-not $askText) { $askText = $ask }
# A Win32 message box: its text and buttons reach UI Automation as panes.
$yes = $askAll | Where-Object { $_.Current.Name -match "^&?(Yes|S$([char]0xED)|Oui)$" } | Select-Object -First 1
"offer: $(($askText.Current.Name -replace '\s+', ' '))"
Step 's09_update' @(@($askText), @($yes)) 1
[DmWin]::Click([IntPtr]$yes.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 1200
if (Dialog) { throw 'the offer is still open after Yes' }
$books = ById 'BookList'
$ch1 = Item $books '1CH'
ShowItem $ch1
if ((StatusEl).Current.Name -notmatch '^1313 ') { throw "not everything was hidden again: $((StatusEl).Current.Name)" }
"after reapplying: $((StatusEl).Current.Name)"
Step 's10_reapplied' @(@($ch1, (Item $books '2CH')), @(StatusEl)) -1
$script:win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
$script:proc.WaitForExit(10000) | Out-Null
if (-not $script:proc.HasExited) { $script:proc.Kill(); $script:proc.WaitForExit(5000) | Out-Null }
RemoveDemoProject
$script:steps | Export-Clixml -LiteralPath $stepsFile
}
if ($CaptureOnly) { "captured in ${Work}; build the video with -Reuse"; return }

# The Paratext Tools menu, before the window opens, and again at the end of the install section:
# the user's screenshot with no item highlighted (tools\demo_video\tools_menu_plain_en.png,
# 2026-11-10), with Parallel Passages Editor... highlighted by MenuShot.ps1, which also gives the two boxes
# (the Tools heading and the item).
$demoTools = Join-Path (Split-Path (Split-Path $here)) 'tools\demo_video'
. (Join-Path $demoTools 'MenuShot.ps1')
$menu = New-ToolsMenuShot -Item 'Parallel Passages Editor...' -OutFile (Join-Path $Work 'tools_menu.png') -Lang $Lang
$mb = @(); foreach ($b in $menu.Boxes) { $mb += , $b }
$script:steps = @([pscustomobject]@{ Id = 's00_menu'; Shot = $menu.Shot; Boxes = $mb; Pointer = 1; Dialog = $null; Rows = -1 }) + $script:steps

# Getting and installing the tool: the shared install section (Install_Phils_tools.exe, see
# tools\demo_video\install\InstallSection.ps1), then the Tools menu again.
. (Join-Path $demoTools 'install\InstallSection.ps1')
$script:steps += Get-InstallSteps -Plugin 'ParallelPassages' -Lang $Lang -Name (Text 'title').title
$script:steps += [pscustomobject]@{ Id = 'i5_ready'; Shot = $menu.Shot; Boxes = $mb; Pointer = 1; Dialog = $null; Rows = -1 }

# ------------------------------------------------------------ frames
$W = 1920; $H = 1080; $top = 96; $bottomBand = 190
# The "Sky" design (the user's choice): deep blue cover, light-to-mid blue steps, gold accents.
function Hex($h) { [Drawing.ColorTranslator]::FromHtml($h) }
$navy = Hex '#1F3A5F'          # headline bar
$orange = [Drawing.Color]::FromArgb(255, 140, 0)   # boxes round the controls
$gold = Hex '#F2B544'          # accents
$band = Hex '#18222E'          # subtitle band
$bgTop = Hex '#EEF7FF'; $bgBottom = Hex '#94BEE4'          # steps: the gradient
$coverTop = Hex '#2C5A9A'; $coverBottom = Hex '#0B1830'    # cover and closing slide
function Gradient($g, $x, $y, $w, $h, $c1, $c2) {
    $br = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.RectangleF $x, ($y - 1), $w, ($h + 2)), $c1, $c2, 90.0
    $g.FillRectangle($br, [float]$x, [float]$y, [float]$w, [float]$h)
    $br.Dispose()
}
function RoundRect($x, $y, $w, $h, $r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc([float]$x, [float]$y, $d, $d, 180, 90); $p.AddArc([float]($x + $w - $d), [float]$y, $d, $d, 270, 90)
    $p.AddArc([float]($x + $w - $d), [float]($y + $h - $d), $d, $d, 0, 90); $p.AddArc([float]$x, [float]($y + $h - $d), $d, $d, 90, 90)
    $p.CloseFigure(); $p
}
function NewFrame { $f = New-Object Drawing.Bitmap $W, $H; $g = [Drawing.Graphics]::FromImage($f); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'; @{ F = $f; G = $g } }
function Centered($g, $text, $font, $brush, $rect) {
    $sf = New-Object Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString($text, $font, $brush, $rect, $sf)
}
# A font of the given size, or smaller until the text fits on one line in $maxWidth pixels
# (long names, e.g. "Edicion de notas en lote", were cut off on the cover).
function FitFont($g, $text, $family, $size, $maxWidth) {
    $f = New-Object Drawing.Font $family, $size
    while ($size -gt 36 -and $g.MeasureString($text, $f).Width -gt $maxWidth) {
        $f.Dispose(); $size -= 2; $f = New-Object Drawing.Font $family, $size
    }
    $f
}
function Card($id, $file) {
    $t = Text $id
    $fr = NewFrame; $g = $fr.G
    Gradient $g 0 0 $W $H $coverTop $coverBottom
    $light = New-Object Drawing.SolidBrush $gold
    $grey = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(190, 205, 225))
    if ($id -eq 'title') {
        # The cover: a framed panel (gold border) with a tag on its top edge; the plugin's name
        # large and white, a gold rule, then the series smaller (the user's order).
        $px = 360; $py = 290; $pw = $W - 720; $ph = 470
        $panel = RoundRect $px $py $pw $ph 28
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(40, 255, 255, 255))), $panel)
        $g.DrawPath((New-Object Drawing.Pen $gold, 4), $panel)
        $tagFont = New-Object Drawing.Font 'Segoe UI Semibold', 20
        $tagText = T 'PARATEXT PLUGIN' 'COMPLEMENTO DE PARATEXT' 'MODULE PARATEXT'
        $tw = $g.MeasureString($tagText, $tagFont).Width + 44; $th = 48; $tx = ($W - $tw) / 2; $ty = $py - $th / 2
        $g.FillPath((New-Object Drawing.SolidBrush $gold), (RoundRect $tx $ty $tw $th 24))
        Centered $g $tagText $tagFont (New-Object Drawing.SolidBrush $coverBottom) (New-Object Drawing.RectangleF $tx, $ty, $tw, $th)
        Centered $g $t.title (FitFont $g $t.title 'Segoe UI Semibold' 80 ($pw - 60)) ([Drawing.Brushes]::White) (New-Object Drawing.RectangleF $px, ($py + 60), $pw, 170)
        $g.FillRectangle($light, [float]($W / 2 - 160), [float]($py + 250), 320, 5)
        Centered $g $Series (New-Object Drawing.Font 'Segoe UI', 50) $light (New-Object Drawing.RectangleF $px, ($py + 280), $pw, 120)
    }
    elseif ($id -like 'sec_*') {
        # A section slide (the user's "subtitle" slide): the cover's panel, its title large and
        # white, a gold rule, then the plugin's name smaller.
        $px = 360; $py = 290; $pw = $W - 720; $ph = 470
        $panel = RoundRect $px $py $pw $ph 28
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(40, 255, 255, 255))), $panel)
        $g.DrawPath((New-Object Drawing.Pen $gold, 4), $panel)
        Centered $g $t.title (FitFont $g $t.title 'Segoe UI Semibold' 80 ($pw - 60)) ([Drawing.Brushes]::White) (New-Object Drawing.RectangleF $px, ($py + 60), $pw, 170)
        $g.FillRectangle($light, [float]($W / 2 - 160), [float]($py + 250), 320, 5)
        # The plugin's name, smaller when it would not fit on one line (the French name was cut).
        $name = (Text 'title').title
        Centered $g $name (FitFont $g $name 'Segoe UI' 50 ($pw - 60)) $light (New-Object Drawing.RectangleF $px, ($py + 280), $pw, 120)
    }
    else {
        $f1 = New-Object Drawing.Font 'Segoe UI Semibold', 72
        $f2 = New-Object Drawing.Font 'Segoe UI', 34
        # Smaller when a long name would not fit on one line (the French name of this tool).
        $f1 = FitFont $g $t.title 'Segoe UI Semibold' 72 ($W - 200)
        Centered $g $t.title $f1 ([Drawing.Brushes]::White) (New-Object Drawing.RectangleF 80, 300, ($W - 160), 160)
        $g.FillRectangle($light, [float]($W / 2 - 160), 462, 320, 5)
        # The key points on one line, or each on its own line when they don't fit (a wrap fell in
        # the middle of a point in Spanish and French).
        $sub = $t.sub
        $subH = 120
        if ($g.MeasureString($sub, $f2).Width -gt ($W - 160)) { $sub = ($sub -split ('\s+' + [char]0xB7 + '\s+')) -join "`n"; $subH = 200 }
        Centered $g $sub $f2 $light (New-Object Drawing.RectangleF 80, 480, ($W - 160), $subH)
        # Credits, in small print.
        $credits = @(T 'Created with Claude AI' 'Creado con Claude AI' "Cr$([char]0xE9)$([char]0xE9) avec Claude AI")
        if (-not $NoVoice -and $azure) { $credits += T 'Narration: AI-generated voice (Microsoft Azure AI Speech)' "Narraci$([char]0xF3)n: voz generada por IA (Microsoft Azure AI Speech)" "Narration : voix g$([char]0xE9)n$([char]0xE9)r$([char]0xE9)e par IA (Microsoft Azure AI Speech)" }
        if ($musicOn) { $credits += T 'Music: Coconut Kind of Love by Aetheric (freetouse.com)' "M$([char]0xFA)sica: Coconut Kind of Love de Aetheric (freetouse.com)" 'Musique : Coconut Kind of Love par Aetheric (freetouse.com)' }
        $f3 = New-Object Drawing.Font 'Segoe UI', 22
        $y = $H - 70 - 42 * $credits.Count
        foreach ($c in $credits) { Centered $g $c $f3 $grey (New-Object Drawing.RectangleF 80, $y, ($W - 160), 42); $y += 42 }
    }
    $g.Dispose(); $fr.F.Save($file, [Drawing.Imaging.ImageFormat]::Png); $fr.F.Dispose()
}
function Pointer($g, $x, $y) {
    # A mouse arrow with its tip at (x, y).
    $s = 1.6
    $pts = @(@(0, 0), @(0, 25), @(6, 19), @(11, 30), @(15, 28), @(10, 18), @(18, 18)) | ForEach-Object { New-Object Drawing.PointF ($x + $_[0] * $s), ($y + $_[1] * $s) }
    $g.FillPolygon([Drawing.Brushes]::White, [Drawing.PointF[]]$pts)
    $pen = New-Object Drawing.Pen ([Drawing.Color]::Black), 2
    $g.DrawPolygon($pen, [Drawing.PointF[]]$pts)
}
# The window area of a frame: the band between the headline and the subtitles, less margins.
$areaX = 40; $areaY = $top + 22; $areaW = $W - 80; $areaH = $H - $top - $bottomBand - 44
# The view of the whole window: the shot widened (or heightened) to the area's shape.
function FullView($iw, $ih) {
    $ar = $areaW / $areaH
    if ($iw / $ih -lt $ar) { $w0 = $ih * $ar; , @((($iw - $w0) / 2), 0, $w0, $ih) }
    else { $h0 = $iw / $ar; , @(0, (($ih - $h0) / 2), $iw, $h0) }
}
# The close-up of a step: the boxes (and the dialog) with a margin, in the area's shape, at most
# twice the size of the full view; $null when that would hardly be closer than the full view.
function ZoomView($st, $iw, $ih) {
    $rects = @($st.Boxes)
    if ($st.Dialog) { $rects += , $st.Dialog }
    if ($rects.Count -eq 0) { return $null }
    $x1 = 1e9; $y1 = 1e9; $x2 = -1e9; $y2 = -1e9
    foreach ($r in $rects) {
        $x1 = [Math]::Min($x1, $r[0]); $y1 = [Math]::Min($y1, $r[1])
        $x2 = [Math]::Max($x2, $r[0] + $r[2]); $y2 = [Math]::Max($y2, $r[1] + $r[3])
    }
    $pad = 0.05 * $iw
    $x1 -= $pad; $y1 -= $pad; $x2 += $pad; $y2 += $pad
    $ar = $areaW / $areaH
    $w = $x2 - $x1; $h = $y2 - $y1
    if ($w / $h -lt $ar) { $w = $h * $ar } else { $h = $w / $ar }
    $full = FullView $iw $ih
    $maxZoom = 2.0
    if ($full[2] / $w -gt $maxZoom) { $w = $full[2] / $maxZoom; $h = $w / $ar }
    if ($full[2] / $w -lt 1.15) { return $null }
    $cx = ($x1 + $x2) / 2; $cy = ($y1 + $y2) / 2
    $x = $cx - $w / 2; $y = $cy - $h / 2
    if ($w -le $iw) { $x = [Math]::Max(0, [Math]::Min($x, $iw - $w)) } else { $x = ($iw - $w) / 2 }
    if ($h -le $ih) { $y = [Math]::Max(0, [Math]::Min($y, $ih - $h)) } else { $y = ($ih - $h) / 2 }
    , @($x, $y, $w, $h)
}
# Between two views, at s (0..1): the centre moves in a straight line, the size changes
# geometrically, so the zoom feels even.
function MixView($a, $b, $s) {
    $w = $a[2] * [Math]::Pow($b[2] / $a[2], $s); $h = $a[3] * [Math]::Pow($b[3] / $a[3], $s)
    $cx = ($a[0] + $a[2] / 2) * (1 - $s) + ($b[0] + $b[2] / 2) * $s
    $cy = ($a[1] + $a[3] / 2) * (1 - $s) + ($b[1] + $b[3] / 2) * $s
    , @(($cx - $w / 2), ($cy - $h / 2), $w, $h)
}
# $part: one part of a step with cues (its Show = the boxes shown); $null = all boxes.
function StepFrame($st, $num, $total, $file, $view = $null, $part = $null) {
    $t = Text $st.Id
    $fr = NewFrame; $g = $fr.G
    Gradient $g 0 $top $W ($H - $top - $bottomBand) $bgTop $bgBottom
    # headline, with a gold line under it
    $g.FillRectangle((New-Object Drawing.SolidBrush $navy), 0, 0, $W, $top)
    $g.FillRectangle((New-Object Drawing.SolidBrush $gold), 0, ($top - 5), $W, 5)
    $fh = New-Object Drawing.Font 'Segoe UI Semibold', 30
    $g.DrawString($t.title, $fh, [Drawing.Brushes]::White, 40, 22)
    $fn = New-Object Drawing.Font 'Segoe UI', 20
    $sf = New-Object Drawing.StringFormat; $sf.Alignment = 'Far'
    $g.DrawString("$num / $total", $fn, (New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(190, 205, 225))), (New-Object Drawing.RectangleF 0, 34, ($W - 40), 40), $sf)
    # the window: the view (a rectangle of the shot) fills the area; float coordinates, so a
    # zoom moves smoothly. Whatever falls outside the band between headline and subtitles is cut.
    $img = [Drawing.Image]::FromFile($st.Shot)
    if (-not $view) { $view = FullView $img.Width $img.Height }
    $sc = [Math]::Min($areaW / $view[2], $areaH / $view[3])
    $ox = $areaX + ($areaW - $view[2] * $sc) / 2 - $view[0] * $sc
    $oy = $areaY + ($areaH - $view[3] * $sc) / 2 - $view[1] * $sc
    $iw = $img.Width * $sc; $ih = $img.Height * $sc
    $g.SetClip((New-Object Drawing.RectangleF 0, $top, $W, ($H - $top - $bottomBand)))
    # a soft shadow (spread, faint layers) and a white frame, so the grey window floats on the page
    for ($k = 12; $k -ge 2; $k -= 2) {
        $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(12, 10, 30, 60))), (RoundRect ($ox - $k + 6) ($oy - $k + 10) ($iw + 2 * $k) ($ih + 2 * $k) (10 + $k)))
    }
    $g.FillPath([Drawing.Brushes]::White, (RoundRect ($ox - 6) ($oy - 6) ($iw + 12) ($ih + 12) 10))
    $g.DrawImage($img, [float]$ox, [float]$oy, [float]$iw, [float]$ih)
    $pen = New-Object Drawing.Pen $orange, 5
    if ($st.Dialog) {
        $d = $st.Dialog
        $g.DrawRectangle((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(31, 58, 95)), 4), [float]($ox + $d[0] * $sc - 4), [float]($oy + $d[1] * $sc - 4), [float]($d[2] * $sc + 8), [float]($d[3] * $sc + 8))
    }
    $i = 0; $pointAtShown = -not $part
    foreach ($b in $st.Boxes) {
        $shown = (-not $part) -or ($part.Show -contains $i)
        if ($shown) {
            $bx = $ox + $b[0] * $sc - 6; $by = $oy + $b[1] * $sc - 6; $bwid = $b[2] * $sc + 12; $bhei = $b[3] * $sc + 12
            $g.DrawRectangle($pen, [float]$bx, [float]$by, [float]$bwid, [float]$bhei)
            # With cues, the pointer (if the step has one) is on the first box named.
            $pointHere = if ($part) { $st.Pointer -ge 0 -and $part.Show[0] -eq $i } else { $i -eq $st.Pointer }
            if ($pointHere) { Pointer $g ($bx + $bwid * 0.6) ($by + $bhei * 0.6) }
            if ($st.PointAt) {
                $pa = $st.PointAt; $cx = $pa[0] + $pa[2] / 2; $cy = $pa[1] + $pa[3] / 2
                if ($cx -ge $b[0] -and $cx -le $b[0] + $b[2] -and $cy -ge $b[1] -and $cy -le $b[1] + $b[3]) { $pointAtShown = $true }
            }
        }
        $i++
    }
    if ($st.PointAt -and $pointAtShown) {
        $pa = $st.PointAt
        # at the right end of the item, so its text stays readable
        Pointer $g ($ox + ($pa[0] + $pa[2] * 0.88) * $sc) ($oy + ($pa[1] + $pa[3] * 0.5) * $sc)
    }
    $g.ResetClip()
    $iwShot = $img.Width; $ihShot = $img.Height
    $img.Dispose()
    # subtitles
    $g.FillRectangle((New-Object Drawing.SolidBrush $band), 0, ($H - $bottomBand), $W, $bottomBand)
    $fs = New-Object Drawing.Font 'Segoe UI', 27
    Centered $g (Clean $t.text) $fs ([Drawing.Brushes]::White) (New-Object Drawing.RectangleF 70, ($H - $bottomBand + 8), ($W - 140), ($bottomBand - 16))
    $g.Dispose(); $fr.F.Save($file, [Drawing.Imaging.ImageFormat]::Png); $fr.F.Dispose()
}

$order = @('title')
foreach ($st in $script:steps) {
    if ($st.Id -eq 'd1_folder') { $order += 'sec_install' }
    $order += $st.Id
}
$order += 'end'
# Slides (cover, section slides, closing slide) have no step number.
function IsCard($id) { $id -eq 'title' -or $id -eq 'end' -or $id -like 'sec_*' }
$stepIds = @($order | Where-Object { -not (IsCard $_) })
# Zoom timing: it starts when the dissolve into the step is over, and takes $zoomTime seconds.
$zoomStart = 1.3; $zoomTime = 1.6; $zoomFrames = [int]($zoomTime * 30)
$zooms = @{}; $partFrames = @{}
$n = 0
foreach ($id in $order) {
    $file = Join-Path $frames ('{0:D2}_{1}.png' -f $n, $id)
    if (IsCard $id) { Card $id $file }
    else {
        $st = $script:steps | Where-Object { $_.Id -eq $id }
        $num = [array]::IndexOf($stepIds, $id) + 1
        StepFrame $st $num $stepIds.Count $file
        $cues = Cues (Text $id).text
        if ($cues.Count) {
            $pf = @()
            for ($k = 0; $k -lt $cues.Count; $k++) {
                $cfile = Join-Path $frames ('{0:D2}_{1}_c{2}.png' -f $n, $id, $k)
                StepFrame $st $num $stepIds.Count $cfile $null $cues[$k]
                $pf += $cfile
            }
            $partFrames[$n] = $pf
            "cues $id : $(($cues | ForEach-Object { '{' + ($_.Show -join ',') + '}' }) -join ' ')"
        }
        elseif ($Zoom) {
            $img = [Drawing.Image]::FromFile($st.Shot); $iw0 = $img.Width; $ih0 = $img.Height; $img.Dispose()
            $v1 = ZoomView $st $iw0 $ih0
            if ($v1) {
                $v0 = FullView $iw0 $ih0
                $zf = @()
                for ($k = 1; $k -le $zoomFrames; $k++) {
                    $pp = $k / $zoomFrames
                    $ss = $pp * $pp * (3 - 2 * $pp)     # ease in and out
                    $zfile = Join-Path $frames ('{0:D2}_z{1:D3}.png' -f $n, $k)
                    StepFrame $st $num $stepIds.Count $zfile (MixView $v0 $v1 $ss)
                    $zf += $zfile
                }
                $zooms[$n] = $zf
                "zoom $id"
            }
        }
    }
    $n++
}

# ------------------------------------------------------------ speech and video
# Spoken text is kept between runs ($Work is emptied each time), so a rebuild asks Azure only for
# what changed (the free tier allows few requests a minute).
$ttsCache = Join-Path $env:TEMP 'bulknotes_demo_tts'
New-Item -ItemType Directory -Force $ttsCache | Out-Null
function Say($text, $wav) {
    if (-not $azure) { $tts.SetOutputToWaveFile($wav); $tts.Speak($text); $tts.SetOutputToNull(); return }
    $keyText = "$Voice|$AzureRate|$text|" + ($script:SayAs | ConvertTo-Json -Compress -Depth 4)
    $sha = [Security.Cryptography.SHA1]::Create()
    $hash = ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($keyText)) | ForEach-Object { $_.ToString('x2') }) -join ''
    $cached = Join-Path $ttsCache "$hash.wav"
    if (-not (Test-Path -LiteralPath $cached)) { Speak-Azure -Text $text -Voice $Voice -OutFile $cached -Rate $AzureRate }
    Copy-Item -LiteralPath $cached $wav -Force
}
# Seconds from the start of a WAV (16-bit PCM, data at byte 44) to the end of the speech in it.
function SpeechEnd($wav) {
    $bytes = [IO.File]::ReadAllBytes($wav)
    $byteRate = [BitConverter]::ToInt32($bytes, 28)
    $i = $bytes.Length - 2
    while ($i -ge 44 -and [Math]::Abs([int][BitConverter]::ToInt16($bytes, $i)) -lt 700) { $i -= 2 }
    [Math]::Max(0.0, ($i - 44) / $byteRate)
}
if (-not $NoVoice) {
    if ($azure) { . (Join-Path $here 'azure_tts.ps1') }
    else {
        $tts = New-Object System.Speech.Synthesis.SpeechSynthesizer
        $tts.SelectVoice($Voice)
        $tts.Rate = $Rate
    }
    "voice: $Voice"
}
else { 'voice: none' }
$ffmpeg = $FFmpeg
if (-not $ffmpeg) {
    $ffmpeg = Get-ChildItem 'C:\ffmpeg-*' -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Recurse -Filter ffmpeg.exe -ErrorAction SilentlyContinue } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $ffmpeg) { $ffmpeg = (Get-Command ffmpeg).Source }
"ffmpeg: $ffmpeg"
if (-not ((& $ffmpeg -hide_banner -filters 2>&1 | Out-String) -match '\bxfade\b')) {
    throw "This ffmpeg has no xfade filter (it needs ffmpeg 4.3 or later): $ffmpeg"
}
$inv = [Globalization.CultureInfo]::InvariantCulture
function Num($x) { ([double]$x).ToString('0.###', $inv) }

# The joins: a dip to black after the cover and before the closing slide, dissolves elsewhere.
$count = $order.Count
$joinType = @{}; $joinLen = @{}
for ($k = 1; $k -lt $count; $k++) {
    if ((IsCard $order[$k - 1]) -or (IsCard $order[$k])) { $joinType[$k] = 'fadeblack'; $joinLen[$k] = $Dip }
    else {
        $ra = ($script:steps | Where-Object { $_.Id -eq $order[$k - 1] }).Rows
        $rb = ($script:steps | Where-Object { $_.Id -eq $order[$k] }).Rows
        if ($ra -ge 0 -and $ra -eq $rb) { $joinType[$k] = 'wipeleft'; $joinLen[$k] = $Wipe }
        else { $joinType[$k] = 'fade'; $joinLen[$k] = $Dissolve }
    }
    "join $($order[$k - 1]) -> $($order[$k]): $($joinType[$k]) $($joinLen[$k]) s"
}
$fadeInFirst = 0.8; $endHold = 1.5
# Each step: its join in, a breath, the voice (or the reading time), a pause, its join out.
$vIn = @{}; $aIn = @(); $voiceIdx = @{}
$durs = @(); $leads = @()
$idx = 0
for ($n = 0; $n -lt $count; $n++) {
    $id = $order[$n]
    $frame = Join-Path $frames ('{0:D2}_{1}.png' -f $n, $id)
    $in = if ($n -eq 0) { $fadeInFirst } else { $joinLen[$n] }
    $out = if ($n -eq $count - 1) { $endHold } else { $joinLen[$n + 1] }
    $lead = $in + 0.3; $tail = 0.8
    if ($n -eq 0) { $lead += $CoverIntro }
    $text = Clean (Text $id).text
    if ($NoVoice) {
        # About 2.6 words a second: time to read the subtitle.
        $words = @($text -split '\s+' | Where-Object { $_ }).Count
        $secs = [Math]::Max(2.5, $words / 2.6)
        # The cover and closing slides show no subtitle: a short, fixed time.
        if ($id -eq 'title') { $secs = 1.5 } elseif ($id -eq 'end') { $secs = 4.5 } elseif ($id -like 'sec_*') { $secs = 2.5 }
        $wav = $null
    }
    else {
        $wav = Join-Path $Work ('{0:D2}.wav' -f $n)
        Say $text $wav
        $fs = [IO.File]::OpenRead($wav); $br = New-Object IO.BinaryReader $fs; $fs.Position = 28; $rate = $br.ReadInt32(); $br.Close()
        $secs = ((Get-Item $wav).Length - 44) / $rate
    }
    $dur = [Math]::Round([Math]::Max($lead + $secs + $tail + $out, 4.5), 2)
    $durs += $dur; $leads += $lead
    if ($partFrames.ContainsKey($n)) {
        # When each part starts: where its cue is spoken (the speech of the text before the cue,
        # spoken on its own), or without a voice, its share of the words.
        $cues = Cues (Text $id).text
        $starts = @()
        for ($k = 0; $k -lt $cues.Count; $k++) {
            $pos = $cues[$k].Pos
            if ($pos -eq 0) { $starts += 0.0; continue }
            $before = $text.Substring(0, $pos).Trim()
            if ($wav) {
                $pw = Join-Path $Work ('{0:D2}_cue{1}.wav' -f $n, $k)
                Say $before $pw
                $t = SpeechEnd $pw
            }
            else {
                $all = @($text -split '\s+' | Where-Object { $_ }).Count
                $t = $secs * @($before -split '\s+' | Where-Object { $_ }).Count / [Math]::Max(1, $all)
            }
            $starts += [Math]::Min($dur - 0.5, $lead + $t)
        }
        "  cues at $(($starts | ForEach-Object { Num $_ }) -join ', ') s"
        $q = { param($x) "file '" + ($x -replace '\\', '/') + "'" }
        $lines = @('ffconcat version 1.0')
        $pf = $partFrames[$n]
        for ($k = 0; $k -lt $pf.Count; $k++) {
            $end = if ($k -lt $pf.Count - 1) { $starts[$k + 1] } else { $dur + 1 }
            $lines += (& $q $pf[$k]); $lines += "duration $(Num ([Math]::Max(0.034, $end - $starts[$k])))"
        }
        $lines += (& $q $pf[-1])     # the concat reader ignores the last duration
        $listFile = Join-Path $Work ('{0:D2}_cues.txt' -f $n)
        [IO.File]::WriteAllLines($listFile, $lines)
        # Made into a clip of its own first: many image-list inputs in the one big join ran ffmpeg
        # out of memory.
        $clip = Join-Path $Work ('{0:D2}_cues.mp4' -f $n)
        & $ffmpeg -hide_banner -loglevel error -y -f concat -safe 0 -i $listFile -vf 'fps=30,format=yuv420p' -t (Num $dur) -c:v libx264 -preset veryfast -crf 12 -tune stillimage -threads 4 $clip
        if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed on the cues of $id" }
        # One decoding thread each: with frame threads, every clip's decoder holds many 1080p
        # frames, and the big join ran out of memory.
        $vIn[$n] = @('-threads', '1', '-i', $clip)
    }
    elseif ($zooms.ContainsKey($n)) {
        $zf = $zooms[$n]
        $q = { param($x) "file '" + ($x -replace '\\', '/') + "'" }
        $zs = $in + 0.3
        $lines = @('ffconcat version 1.0', (& $q $frame), "duration $(Num $zs)")
        foreach ($z in $zf) { $lines += (& $q $z); $lines += 'duration 0.0333333' }
        $lines += (& $q $zf[-1]); $lines += "duration $(Num ($dur - $zs - $zoomTime + 1))"
        $lines += (& $q $zf[-1])     # the concat reader ignores the last duration
        $listFile = Join-Path $Work ('{0:D2}_zoom.txt' -f $n)
        [IO.File]::WriteAllLines($listFile, $lines)
        $vIn[$n] = @('-f', 'concat', '-safe', '0', '-i', $listFile)
    }
    else { $vIn[$n] = @('-threads', '1', '-loop', '1', '-framerate', '30', '-t', (Num $dur), '-i', $frame) }
    if ($wav) { $aIn += @('-i', $wav); $voiceIdx[$n] = $idx; $idx++ }
    "step $id $(Num $dur)s"
}
if (-not $NoVoice -and -not $azure) { $tts.Dispose() }
if ($musicOn) { $aIn += @('-stream_loop', '-1', '-i', $Music); $musicIdx = $idx; $idx++ }

# The length of the video: each join starts $joinLen before the end of what is joined so far.
$total = $durs[0]
for ($k = 1; $k -lt $count; $k++) { $total += $durs[$k] - $joinLen[$k] }
# Newer ffmpeg reads an option's value from a file with -/option; older ones had -filter_complex_script.
$fromFile = if ((& $ffmpeg -hide_banner -h long 2>&1 | Out-String) -match 'filter_complex_script') { '-filter_complex_script' } else { '-/filter_complex' }

# Video: xfade joins, in passes of $JoinSteps steps. One join of every step (and every voice) at
# once ran ffmpeg out of memory when little was free; each pass starts from the video so far.
$part = $null; $partLen = 0.0; $n = 0; $pass = 0
$videoFile = Join-Path $Work 'video.mp4'
while ($n -lt $count) {
    $a = @('-hide_banner', '-loglevel', 'error', '-y'); $f = @(); $i = 0; $prev = $null; $len = 0.0
    if ($part) { $a += @('-threads', '1', '-i', $part); $f += '[0:v]settb=AVTB,setpts=PTS-STARTPTS[p]'; $prev = 'p'; $len = $partLen; $i = 1 }
    $end = [Math]::Min($count, $n + $JoinSteps)
    for ($m = $n; $m -lt $end; $m++) {
        $a += $vIn[$m]
        $f += "[${i}:v]fps=30,format=yuv420p,setsar=1,trim=0:$(Num $durs[$m]),settb=AVTB,setpts=PTS-STARTPTS[v$m]"
        if (-not $prev) { $prev = "v$m"; $len = $durs[$m] }
        else {
            $f += "[$prev][v$m]xfade=transition=$($joinType[$m]):duration=$(Num $joinLen[$m]):offset=$(Num ($len - $joinLen[$m]))[x$m]"
            $prev = "x$m"; $len += $durs[$m] - $joinLen[$m]
        }
        $i++
    }
    $last = $end -eq $count
    if ($last) { $f += "[$prev]fade=t=in:st=0:d=$(Num $fadeInFirst),fade=t=out:st=$(Num ($total - 1.2)):d=1.2[vout]"; $out = $videoFile }
    else { $f += "[$prev]null[vout]"; $out = Join-Path $Work ('part{0}.mp4' -f $pass) }
    $script = Join-Path $Work ('filter_v{0}.txt' -f $pass)
    [IO.File]::WriteAllText($script, ($f -join ";`n"))
    # The passes between keep a high quality (crf 12); the last is the standard's crf 18.
    $q = if ($last) { @('-preset', 'medium', '-crf', '18') } else { @('-preset', 'veryfast', '-crf', '12') }
    $a += @($fromFile, $script, '-map', '[vout]', '-an', '-t', (Num $len), '-c:v', 'libx264') + $q + @('-tune', 'stillimage', '-r', '30', '-threads', '4', $out)
    & $ffmpeg @a
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg failed (video, pass $pass)" }
    if ($part) { Remove-Item -LiteralPath $part -Force }
    $part = $out; $partLen = $len; $n = $end; $pass++
}

# Audio: the voices, each delayed to its place, joined with the same overlaps; the music under it.
$f = @()
$mix = $null
if (-not $NoVoice) {
    for ($n = 0; $n -lt $count; $n++) {
        $ms = [int]($leads[$n] * 1000)
        $f += "[$($voiceIdx[$n]):a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,adelay=$ms|$ms,apad,atrim=0:$(Num $durs[$n]),asetpts=PTS-STARTPTS[a$n]"
    }
    $aPrev = 'a0'
    for ($k = 1; $k -lt $count; $k++) {
        $f += "[$aPrev][a$k]acrossfade=d=$(Num $joinLen[$k]):c1=tri:c2=tri[xa$k]"
        $aPrev = "xa$k"
    }
    $mix = $aPrev
}
if ($musicOn) {
    # The music alone at $MusicVolume, then down to $MusicUnderVoice over the second before the
    # narration starts (where it would start, without a voice), and there for the rest.
    $t1 = $leads[0]; $t0 = [Math]::Max(0.0, $t1 - 1.0)
    $hi = Num $MusicVolume; $lo = Num $MusicUnderVoice
    $level = "if(lt(t,$(Num $t0)),$hi,if(lt(t,$(Num $t1)),$hi-($hi-$lo)*(t-$(Num $t0))/$(Num ($t1 - $t0)),$lo))"
    $f += "[$($musicIdx):a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=0:$(Num $total),asetpts=PTS-STARTPTS,volume='$level':eval=frame,afade=t=in:st=0:d=2,afade=t=out:st=$(Num ($total - 3)):d=3[music]"
    if ($mix) {
        $f += "[$mix][music]amix=inputs=2:duration=first:normalize=0[amixed]"
        $mix = 'amixed'
    }
    else { $mix = 'music' }
}
if (-not $mix) { $f += "anullsrc=r=48000:cl=stereo,atrim=0:$(Num $total)[silence]"; $mix = 'silence' }
$f += "[$mix]afade=t=out:st=$(Num ($total - 1.2)):d=1.2[aout]"
$script = Join-Path $Work 'filter_a.txt'
[IO.File]::WriteAllText($script, ($f -join ";`n"))
$audioFile = Join-Path $Work 'audio.m4a'
$a = @('-hide_banner', '-loglevel', 'error', '-y') + $aIn + @($fromFile, $script, '-map', '[aout]', '-t', (Num $total), '-c:a', 'aac', '-b:a', '160k', $audioFile)
& $ffmpeg @a
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed (audio)' }

# The two together.
$suffix = $(if ($Zoom) { '_zoom' } else { '' }) + $(if ($NoVoice) { '_novoice' } else { '' })
$outFile = Join-Path $here ("ParallelPassagesEditor_Demo_{0}{1}.mp4" -f $Lang.ToUpper(), $suffix)
& $ffmpeg -hide_banner -loglevel error -y -i $videoFile -i $audioFile -map 0:v -map 1:a -c copy -t (Num $total) -movflags +faststart $outFile
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed (joining picture and sound)' }
"written $outFile ($(Num $total)s)"

# The YouTube description (the standard, section 12), with each step's question ('q' in
# narration.json) at the time its step is fully on screen in this video: the end of the join into
# it, rounded up. Only for the voiced video, whose times differ from the test's. English is
# youtubetext.txt (the user's name for it), other languages youtubetext_<lang>.txt.
if (-not $NoVoice) {
    $yt = $narr.youtube.$Lang
    if (-not $yt) { "narration.json has no youtube text for '$Lang': no youtubetext written" }
    else {
        $lines = @($yt.about, '', $yt.download, $yt.link, '')
        $at = $durs[0]
        for ($k = 1; $k -lt $count; $k++) {
            $q = (Text $order[$k]).q
            if ($q) { $sec = [int][Math]::Ceiling($at); $lines += ('{0}:{1:D2} {2}' -f [int][Math]::Floor($sec / 60), ($sec % 60), $q) }
            $at += $durs[$k] - $joinLen[$k]
        }
        $lines += ''
        $lines += @($yt.credits)
        $ytFile = Join-Path $here $(if ($Lang -eq 'en') { 'youtubetext.txt' } else { "youtubetext_$Lang.txt" })
        [IO.File]::WriteAllText($ytFile, (($lines -join "`r`n") + "`r`n"), (New-Object Text.UTF8Encoding $true))
        "written $ytFile"
    }
}
