<#
.SYNOPSIS
  対象プロセスの UI 要素を AutomationId で特定し、読み取り・操作する（#433）。
.DESCRIPTION
  ランブックの合否は UIA ツリーの値などの機械的な証跡で判定する。表示文言ではなく AutomationId で要素を
  特定し、結果を JSON で標準出力へ出す。-EvidencePath を指定すると同じ内容を JSONL に追記する。
  対象はプロセスのすべてのトップレベルウィンドウ（メインウィンドウ・通知ポップアップ・ライブログ）。

  Action:
    Read     要素の名前・種類・有効状態・値と、リストなら各項目の文字列を返す
    Invoke   ボタンを押す（InvokePattern）
    SetText  テキストボックスに値を設定する（ValuePattern）
    Select   ComboBox の項目を名前で選ぶ
    WaitText 要素の文字列（Read の name / value / items）が -Text の正規表現に一致するまで待つ

  -ListId と -ItemText を指定すると、-ListId のリストのうち、項目の文字列が -ItemText の正規表現に
  一致する項目の中から要素を探す（Recent review events のように、同じ AutomationId のボタンが項目ごとにある場合）。
  項目の文字列は、項目内の要素の Name（テキスト・リンク・ボタンの名前）を空白でつないだもの。
.EXAMPLE
  ./Invoke-RunbookUia.ps1 -ProcessId 1234 -Action WaitText -AutomationId StatusText -Text '^Running' -TimeoutSeconds 60
.EXAMPLE
  ./Invoke-RunbookUia.ps1 -ProcessId 1234 -Action Invoke -AutomationId LaunchReviewerButton -ListId ReviewEventList -ItemText 'fixture-repository#307'
#>
param(
    [Parameter(Mandatory)][int]$ProcessId,
    [Parameter(Mandatory)][ValidateSet('Read', 'Invoke', 'SetText', 'Select', 'WaitText')][string]$Action,
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

$ae = [System.Windows.Automation.AutomationElement]
$scope = [System.Windows.Automation.TreeScope]

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
                if (((Get-Texts $item) -join ' ') -match $ItemText) {
                    $found = $item.FindFirst($scope::Descendants, $idCondition)
                    if ($found) { return $found }
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

$element = Wait-Element
switch ($Action) {
    'Read' { }
    'Invoke' {
        (Get-Pattern $element ([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
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
    'WaitText' {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ($true) {
            $state = Get-State $element
            $haystack = @($state.name, $state.value) + $state.items | Where-Object { $_ }
            if (($haystack -join "`n") -match $Text) { break }
            if ((Get-Date) -ge $deadline) {
                throw "AutomationId '$AutomationId' の文字列が $TimeoutSeconds 秒以内に /$Text/ に一致しません。最後の状態: $($state | ConvertTo-Json -Compress)"
            }
            Start-Sleep -Milliseconds 500
            $element = Wait-Element
        }
    }
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
