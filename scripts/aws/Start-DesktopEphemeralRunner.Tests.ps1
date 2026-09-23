# Pester v5 tests for Start-DesktopEphemeralRunner.ps1
# 使い捨て runner の起動手順を固定する（#380）。
# - Launch Template の default version だけで起動し、値を上書きしない
# - ephemeral-runner タグを確かめてから JIT config を発行する（タグが無ければ発行しない）
# - JIT config は一時ファイル経由で Advanced tier の parameter に置き、コマンドライン・出力に載せない
# - instance_id / runner_label / runner_name は分かった時点で GITHUB_OUTPUT に書き、後続の失敗でも cleanup できる
# - run 固有ラベルの無い runner、待機中の instance の破棄は失敗させる
# aws / gh CLI は、呼び出しを記録して状態に応じた応答を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Start-DesktopEphemeralRunner.ps1'
    $script:InstanceId = 'i-0aaaaaaaaaaaaaaaa'
    $script:RunnerName = "squirrel-notifier-ephemeral-$script:InstanceId"
    $script:Label = 'squirrel-notifier-desktop-123'
    $script:Secret = 'ENCODED-JIT-CONFIG-SECRET'

    # global の fake 関数から参照する値。fake の中では $script: のスコープが Pester の実行スコープと一致しない。
    $global:FakeConst = @{ InstanceId = $script:InstanceId; RunnerName = $script:RunnerName; Label = $script:Label; Secret = $script:Secret }

    # 再試行・待機の間隔を待たずに進める。
    function global:Start-Sleep { }

    function global:aws
    {
        $operation = "$($args[0]) $($args[1])"
        $global:FakeCalls.Add(($args -join ' '))
        $global:LASTEXITCODE = 0
        $fake = $global:FakeState

        switch ($operation)
        {
            'ec2 run-instances' { return $global:FakeConst.InstanceId }
            'ec2 describe-instances'
            {
                $query = $args[[array]::IndexOf($args, '--query') + 1]
                if ($query -like '*Tags*')
                {
                    if ($fake.TagNotFoundCount -gt 0)
                    {
                        $fake.TagNotFoundCount--
                        $global:LASTEXITCODE = 254
                        return 'An error occurred (InvalidInstanceID.NotFound) when calling the DescribeInstances operation'
                    }
                    return $fake.TagValue
                }
                return $fake.InstanceState
            }
            'ssm put-parameter'
            {
                $uri = $args[[array]::IndexOf($args, '--cli-input-json') + 1]
                $path = $uri.Substring('file://'.Length)
                $fake.ParameterInput = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
                $fake.ParameterInputPath = $path
                return '{"Version": 1, "Tier": "Advanced"}'
            }
            default { throw "想定外の aws 呼び出し: $($args -join ' ')" }
        }
    }

    function global:gh
    {
        $global:FakeCalls.Add('gh ' + ($args -join ' '))
        $global:LASTEXITCODE = 0
        $fake = $global:FakeState

        if ($args -contains 'repos/scottlz0310/squirrel-notifier/actions/runners/generate-jitconfig')
        {
            $fake.JitRequest = @($input) -join "`n" | ConvertFrom-Json
            if ($fake.JitError)
            {
                $global:LASTEXITCODE = 1
                return 'HTTP 422: Validation Failed'
            }
            return (@{ runner = @{ id = 42; name = $global:FakeConst.RunnerName }; encoded_jit_config = $global:FakeConst.Secret } | ConvertTo-Json -Compress)
        }

        $status = if ($fake.RunnerStates.Count -gt 1) { $fake.RunnerStates.Dequeue() } else { $fake.RunnerStates.Peek() }
        if ($status -eq 'missing')
        {
            return @()
        }

        $labels = if ($status -eq 'wrong-label') { @('self-hosted', 'windows', 'squirrel-notifier-desktop') } else { @('self-hosted', 'windows', $global:FakeConst.Label) }
        $runnerStatus = if ($status -eq 'wrong-label') { 'online' } else { $status }
        return (@{ id = 42; name = $global:FakeConst.RunnerName; status = $runnerStatus; busy = $false; labels = @($labels | ForEach-Object { @{ name = $_ } }) } | ConvertTo-Json -Compress -Depth 4)
    }

    function Invoke-Start
    {
        & $script:ScriptPath -Repository 'scottlz0310/squirrel-notifier' -LaunchTemplateId 'lt-09e208553b742f6c1' -RunId '123' `
            -GitHubOutputPath $script:OutputPath -TempDirectory $TestDrive -RunnerWaitSeconds 60 -PollSeconds 1
    }

    function Get-Outputs
    {
        $outputs = @{}
        foreach ($line in @(Get-Content -LiteralPath $script:OutputPath -ErrorAction SilentlyContinue))
        {
            $key, $value = $line -split '=', 2
            $outputs[$key] = $value
        }
        return $outputs
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws', 'function:global:gh', 'function:global:Start-Sleep' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeCalls', 'FakeState', 'FakeConst' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Start-DesktopEphemeralRunner.ps1' {
    BeforeEach {
        $global:FakeCalls = [System.Collections.Generic.List[string]]::new()
        $states = [System.Collections.Generic.Queue[string]]::new()
        'missing', 'offline', 'online' | ForEach-Object { $states.Enqueue($_) }
        $global:FakeState = @{
            TagValue           = 'ephemeral-runner'
            TagNotFoundCount   = 1
            InstanceState      = 'running'
            RunnerStates       = $states
            JitError           = $false
            JitRequest         = $null
            ParameterInput     = $null
            ParameterInputPath = $null
        }
        $script:OutputPath = Join-Path $TestDrive 'github-output.txt'
        Remove-Item -LiteralPath $script:OutputPath -ErrorAction SilentlyContinue
    }

    Context '正常系' {
        BeforeEach {
            $script:Stdout = Invoke-Start
            $script:Result = $script:Stdout | ConvertFrom-Json
        }

        It 'runner が online になったら起動結果を返す' {
            $script:Result.instanceId | Should -Be $script:InstanceId
            $script:Result.runnerName | Should -Be $script:RunnerName
            $script:Result.runnerLabel | Should -Be $script:Label
            $script:Result.parameterName | Should -Be "/squirrel-notifier/desktop-e2e/jit/$script:InstanceId"
        }

        It 'Launch Template の default version だけで起動し、値を上書きしない' {
            $launch = @($global:FakeCalls | Where-Object { $_ -like 'ec2 run-instances*' })

            $launch.Count | Should -Be 1
            $launch[0] | Should -Match 'LaunchTemplateId=lt-09e208553b742f6c1,Version=\$Default'
            $launch[0] | Should -Not -Match '--(instance-type|network-interfaces|subnet-id|security-group-ids|block-device-mappings|image-id|user-data|tag-specifications)'
        }

        It 'JIT config は run 固有ラベルだけで発行する' {
            $global:FakeState.JitRequest.name | Should -Be $script:RunnerName
            $global:FakeState.JitRequest.labels | Should -Be @('self-hosted', 'windows', $script:Label)
        }

        It 'JIT config を Advanced tier・上書きなしの SecureString として置き、一時ファイルを消す' {
            $global:FakeState.ParameterInput.Value | Should -Be $script:Secret
            $global:FakeState.ParameterInput.Tier | Should -Be 'Advanced'
            $global:FakeState.ParameterInput.Type | Should -Be 'SecureString'
            $global:FakeState.ParameterInput.Overwrite | Should -BeFalse
            Test-Path -LiteralPath $global:FakeState.ParameterInputPath | Should -BeFalse
        }

        It 'JIT config をコマンドラインにも出力にも載せない' {
            ($global:FakeCalls -join "`n") | Should -Not -Match $script:Secret
            ($script:Stdout -join "`n") | Should -Not -Match $script:Secret
        }

        It 'instance_id / runner_label / runner_name を GITHUB_OUTPUT に書く' {
            $outputs = Get-Outputs

            $outputs['instance_id'] | Should -Be $script:InstanceId
            $outputs['runner_label'] | Should -Be $script:Label
            $outputs['runner_name'] | Should -Be $script:RunnerName
        }
    }

    It 'ephemeral-runner タグが無ければ JIT config を発行せずに失敗し、instance_id は書き出す' {
        $global:FakeState.TagValue = 'None'

        { Invoke-Start } | Should -Throw '*のタグがありません*'
        @($global:FakeCalls | Where-Object { $_ -like '*generate-jitconfig*' }).Count | Should -Be 0
        (Get-Outputs)['instance_id'] | Should -Be $script:InstanceId
    }

    It 'JIT config の発行に失敗したら parameter を置かない' {
        $global:FakeState.JitError = $true

        { Invoke-Start } | Should -Throw '*gh api が失敗しました*'
        @($global:FakeCalls | Where-Object { $_ -like 'ssm put-parameter*' }).Count | Should -Be 0
    }

    It 'run 固有ラベルの無い runner は待たずに失敗させる' {
        $global:FakeState.RunnerStates = [System.Collections.Generic.Queue[string]]::new()
        $global:FakeState.RunnerStates.Enqueue('wrong-label')

        { Invoke-Start } | Should -Throw '*run 固有のラベル*'
    }

    It '待機中に instance が破棄されたら失敗させる' {
        $global:FakeState.RunnerStates = [System.Collections.Generic.Queue[string]]::new()
        $global:FakeState.RunnerStates.Enqueue('offline')
        $global:FakeState.InstanceState = 'shutting-down'

        { Invoke-Start } | Should -Throw '*shutting-down になりました*'
    }
}
