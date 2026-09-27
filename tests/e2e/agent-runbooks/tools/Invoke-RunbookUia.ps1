<#
.SYNOPSIS
  対象プロセスの UI 要素を AutomationId で特定し、読み取り・操作する（#433）。
.DESCRIPTION
  ランブックの合否は UIA ツリーの値などの機械的な証跡で判定する。表示文言ではなく AutomationId で要素を
  特定し、結果を JSON で標準出力へ出す。-EvidencePath を指定すると同じ内容を JSONL に追記する。
  対象はプロセスのすべてのトップレベルウィンドウ（メインウィンドウ・通知ポップアップ・ライブログ）。

  Action:
    Read     要素の名前・種類・有効状態・値と、リストなら各項目の文字列を返す
    Click    要素の中心を実際のマウスでクリックする。クリック位置の要素が対象プロセスのものでなければ
             押さずに失敗する（常駐中の既定インスタンスや他のウィンドウを誤って押さないため）
    SetText  テキストボックスに値を設定する（ValuePattern）
    Select   ComboBox の項目を名前で選ぶ
    WaitText 要素の文字列（Read の name / value / items）が -Text の正規表現に一致するまで待つ
    WaitEnabled / WaitDisabled  要素が有効 / 無効になるまで待つ（購読の開始・停止はボタンの状態で判定する）

  -ListId と -ItemText を指定すると、-ListId のリストのうち、項目の文字列が -ItemText の正規表現に
  一致する項目の中から要素を探す（Recent review events のように、同じ AutomationId のボタンが項目ごとにある場合）。
  項目の文字列は、項目内の要素の Name（テキスト・リンク・ボタンの名前）を空白でつないだもの。
  -Text と -ItemText の正規表現は大文字小文字を区別する（-cmatch）。合否の判定を記載どおりの厳密さにするため。

  ボタンの操作に InvokePattern は使わない。UIA だけで reviewer の起動と購読の停止を続けて行うと、
  アプリの UI スレッドが応答しなくなる事象を観測したため（マウス入力では再現しなかった）。
.EXAMPLE
  ./Invoke-RunbookUia.ps1 -ProcessId 1234 -Action WaitText -AutomationId StatusText -Text '^Subscribed' -TimeoutSeconds 60
.EXAMPLE
  ./Invoke-RunbookUia.ps1 -ProcessId 1234 -Action Click -AutomationId LaunchReviewerButton -ListId ReviewEventList -ItemText 'fixture-repository #307'
#>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][ValidateSet('Read', 'Click', 'SetText', 'Select', 'WaitText', 'WaitEnabled', 'WaitDisabled')][string]$Action,
    [Parameter(Mandatory)][string]$AutomationId,
    [string]$Text,
    [string]$ItemText,
    [string]$ListId,
    [int]$TimeoutSeconds = 30,
    [string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RunbookMouse
{
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    // UIA の BoundingRectangle と SetCursorPos を同じ物理座標で扱うため、Per-Monitor v2 にする
    public static void UsePhysicalCoordinates() => SetThreadDpiAwarenessContext(new IntPtr(-4));

    public static void Click(int x, int y)
    {
        if (!SetCursorPos(x, y))
        {
            throw new InvalidOperationException("マウスカーソルを移動できませんでした。");
        }

        mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // 別プロセスからの SetForegroundWindow はフォアグラウンドロックで拒否されうるため、
    // 現在の前面ウィンドウのスレッドと入力を一時的に結び付けてから前面に出す
    public static bool BringToFront(IntPtr window)
    {
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != currentThread && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThread, foregroundThread, false);
            }
        }

        return GetForegroundWindow() == window;
    }

    // 失敗時の証跡用。通知ポップアップはフォーカスを失うと閉じるため、そのときの前面を記録する
    public static string ForegroundWindow()
    {
        IntPtr window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint processId);
        var title = new System.Text.StringBuilder(256);
        GetWindowText(window, title, title.Capacity);
        return processId + " " + title;
    }
}
'@
[RunbookMouse]::UsePhysicalCoordinates()

$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]
$script:MatchedItem = $null

function New-Condition($Property, $Value) {
    [System.Windows.Automation.PropertyCondition]::new($Property, $Value)
}

function Get-ProcessWindows {
    if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        throw "プロセス $ProcessId は終了しています。"
    }
    @($ae::RootElement.FindAll($scope::Children, (New-Condition $ae::ProcessIdProperty $ProcessId)))
}

function Get-Texts($Element) {
    $texts = @($Element.FindAll($scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ })
    , $texts
}

function Find-Element {
    $idCondition = New-Condition $ae::AutomationIdProperty $AutomationId
    foreach ($window in Get-ProcessWindows) {
        if ($ItemText) {
            if (-not $ListId) {
                throw '-ItemText には -ListId（項目を持つリストの AutomationId）が必要です。'
            }
            $list = $window.FindFirst($scope::Descendants, (New-Condition $ae::AutomationIdProperty $ListId))
            if (-not $list) { continue }
            $items = $list.FindAll($scope::Children, (New-Condition $ae::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem)))
            foreach ($item in $items) {
                if (((Get-Texts $item) -join ' ') -cmatch $ItemText) {
                    $found = $item.FindFirst($scope::Descendants, $idCondition)
                    if ($found) {
                        $script:MatchedItem = $item
                        return $found
                    }
                }
            }
        }
        else {
            $found = $window.FindFirst($scope::Descendants, $idCondition)
            if ($found) { return $found }
        }
    }
    $null
}

function Wait-Element {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $element = Find-Element
        if ($element) { return $element }
        if ((Get-Date) -ge $deadline) {
            $where = if ($ItemText) { "（項目: $ItemText）" } else { '' }
            throw "AutomationId '$AutomationId'$where の要素が $TimeoutSeconds 秒以内に見つかりません。"
        }
        Start-Sleep -Milliseconds 500
    }
}

function Get-State($Element) {
    $current = $Element.Current
    $value = $null
    $pattern = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
        $value = $pattern.Current.Value
    }
    $items = @()
    if ($current.ControlType -eq [System.Windows.Automation.ControlType]::List) {
        $items = @($Element.FindAll($scope::Children, (New-Condition $ae::ControlTypeProperty ([System.Windows.Automation.ControlType]::ListItem))) |
            ForEach-Object { (Get-Texts $_) -join ' ' })
    }
    [ordered]@{
        automationId = $AutomationId
        itemText = $ItemText
        name = $current.Name
        controlType = $current.ControlType.ProgrammaticName
        isEnabled = $current.IsEnabled
        value = $value
        items = $items
    }
}

function Get-Pattern($Element, $Pattern) {
    $result = $null
    if (-not $Element.TryGetCurrentPattern($Pattern, [ref]$result)) {
        throw "AutomationId '$AutomationId' は $($Pattern.ProgrammaticName) に対応していません。"
    }
    $result
}

try {
    $element = Wait-Element
    switch ($Action) {
        'Read' { }
        'Click' {
            # 一覧の項目は表示範囲外にあることがあるため、クリック前に項目を表示範囲へ送る
            $scrollItem = $null
            if ($script:MatchedItem -and $script:MatchedItem.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scrollItem)) {
                $scrollItem.ScrollIntoView()
                Start-Sleep -Milliseconds 300
            }
            if (-not $element.Current.IsEnabled) {
                throw "AutomationId '$AutomationId' は無効（IsEnabled=False）のため押せません。状態が変わるのを WaitEnabled で待ってから押してください。"
            }
            # 他のウィンドウ（ブラウザや常駐中の既定インスタンスのポップアップなど）が重なっていると押せないため、
        # 要素のトップレベルウィンドウを前面に出す。常駐インスタンスも同じタイトルなので、タイトルではなく要素から辿る。
        # 前面に出せなくても、この後のクリック位置の確認で別プロセスなら押さずに止まる
        $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
        $topLevel = $element
        while ($true) {
            $parent = $walker.GetParent($topLevel)
            if ($null -eq $parent -or $parent -eq $ae::RootElement) { break }
            $topLevel = $parent
        }
        [void][RunbookMouse]::BringToFront([IntPtr]$topLevel.Current.NativeWindowHandle)
        Start-Sleep -Milliseconds 300
        $rect = $element.Current.BoundingRectangle
            if ($rect.IsEmpty -or $element.Current.IsOffscreen) {
                throw "AutomationId '$AutomationId' は画面に表示されていません。ウィンドウを前面に出すか、スクロールしてから再実行してください。"
            }
            $x = [int]($rect.Left + $rect.Width / 2)
            $y = [int]($rect.Top + $rect.Height / 2)
            $hit = $ae::FromPoint([System.Windows.Point]::new($x, $y))
            if ($hit.Current.ProcessId -ne $ProcessId) {
                throw "($x, $y) には別プロセス（PID $($hit.Current.ProcessId)）の要素があります。対象のウィンドウを前面に出してから再実行してください。"
            }
            [RunbookMouse]::Click($x, $y)
        }
        'SetText' {
            (Get-Pattern $element ([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($Text)
        }
        'Select' {
            (Get-Pattern $element ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Expand()
            Start-Sleep -Milliseconds 300
            $option = $element.FindFirst($scope::Descendants, (New-Condition $ae::NameProperty $Text))
            if (-not $option) {
                throw "AutomationId '$AutomationId' に項目 '$Text' がありません。"
            }
            (Get-Pattern $option ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            (Get-Pattern $element ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)).Collapse()
        }
        { $_ -in 'WaitEnabled', 'WaitDisabled' } {
            $expected = $Action -eq 'WaitEnabled'
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ($element.Current.IsEnabled -ne $expected) {
                if ((Get-Date) -ge $deadline) {
                    throw "AutomationId '$AutomationId' が $TimeoutSeconds 秒以内に IsEnabled=$expected になりません。"
                }
                Start-Sleep -Milliseconds 500
                $element = Wait-Element
            }
        }
        'WaitText' {
            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            while ($true) {
                $state = Get-State $element
                $haystack = @($state.name, $state.value) + $state.items | Where-Object { $_ }
                if (($haystack -join "`n") -cmatch $Text) { break }
                if ((Get-Date) -ge $deadline) {
                    throw "AutomationId '$AutomationId' の文字列が $TimeoutSeconds 秒以内に /$Text/ に一致しません。最後の状態: $($state | ConvertTo-Json -Compress)"
                }
                Start-Sleep -Milliseconds 500
                $element = Wait-Element
            }
        }
    }
}
catch {
    # 見つからない・一致しないときも、原因を追えるようにその時点の前面とウィンドウ一覧を証跡に残す
    if ($EvidencePath) {
        $windows = @()
        try { $windows = @(Get-ProcessWindows | ForEach-Object { $_.Current.Name }) } catch { $windows = @("(取得できません: $($_.Exception.Message))") }
        $failure = [ordered]@{
            automationId = $AutomationId
            itemText = $ItemText
            action = $Action
            text = $Text
            error = $_.Exception.Message
            foregroundWindow = [RunbookMouse]::ForegroundWindow()
            processWindows = $windows
            observedAt = (Get-Date).ToUniversalTime().ToString('o')
        }
        Add-Content -LiteralPath $EvidencePath -Value ($failure | ConvertTo-Json -Depth 3 -Compress) -Encoding utf8NoBOM
    }
    throw
}

$record = Get-State $element
$record.action = $Action
$record.text = $Text
$record.observedAt = (Get-Date).ToUniversalTime().ToString('o')
$json = $record | ConvertTo-Json -Depth 4 -Compress
if ($EvidencePath) {
    Add-Content -LiteralPath $EvidencePath -Value $json -Encoding utf8NoBOM
}
$json
