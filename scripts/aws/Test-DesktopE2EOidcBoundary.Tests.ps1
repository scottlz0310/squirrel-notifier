# Pester v5 tests for Test-DesktopE2EOidcBoundary.ps1
# OIDC ロールの権限境界の確認を固定する（#380）。
# - 上書きの値は永続 instance の subnet と Security Group から取り、すべて --dry-run で呼ぶ
# - 期待どおりなら結果を返し、期待と違う・判定できない結果が 1 件でもあれば失敗させる
# - observe の操作（IAM で拒否できない root の DeleteOnTermination=false）は結果を記録するだけで失敗させない
# aws CLI は、呼び出しを記録して操作ごとの DryRun の結果を返す関数で置き換える。

BeforeAll {
    $script:ScriptPath = Join-Path $PSScriptRoot 'Test-DesktopE2EOidcBoundary.ps1'

    function global:aws
    {
        $global:FakeCalls.Add(($args -join ' '))

        if ($args -notcontains '--dry-run')
        {
            $global:LASTEXITCODE = 0
            return '{"SubnetId": "subnet-05a6dbdf30e0b6664", "SecurityGroupId": "sg-0f5751845b3fa696b"}'
        }

        $case = switch -Wildcard ($args -join ' ')
        {
            '*--network-interfaces*' { 'override-network-interface'; break }
            '*VolumeType=io2*' { 'override-volume-type'; break }
            '*DeleteOnTermination=false*' { 'retain-root-volume'; break }
            '*DeviceName=/dev/sdf*' { 'add-extra-volume'; break }
            '*--block-device-mappings*' { 'override-block-device'; break }
            '*--instance-type*' { 'override-instance-type'; break }
            'ec2 terminate-instances*' { 'terminate-persistent-instance'; break }
            default { 'launch-template-default' }
        }

        $global:LASTEXITCODE = 254
        return "An error occurred ($($global:FakeState[$case])) when calling the operation"
    }

    function Invoke-Boundary
    {
        & $script:ScriptPath -LaunchTemplateId 'lt-09e208553b742f6c1' -ReferenceInstanceId 'i-00b4e23b910eade6c'
    }
}

AfterAll {
    Remove-Item -Path 'function:global:aws' -ErrorAction SilentlyContinue
    Remove-Variable -Name 'FakeCalls', 'FakeState' -Scope Global -ErrorAction SilentlyContinue
}

Describe 'Test-DesktopE2EOidcBoundary.ps1' {
    BeforeEach {
        $global:FakeCalls = [System.Collections.Generic.List[string]]::new()
        $global:FakeState = @{
            'launch-template-default'       = 'DryRunOperation'
            'override-network-interface'    = 'UnauthorizedOperation'
            'override-block-device'         = 'UnauthorizedOperation'
            'override-volume-type'          = 'UnauthorizedOperation'
            'add-extra-volume'              = 'UnauthorizedOperation'
            'retain-root-volume'            = 'DryRunOperation'
            'override-instance-type'        = 'UnauthorizedOperation'
            'terminate-persistent-instance' = 'UnauthorizedOperation'
        }
    }

    It '期待どおりなら全件の結果を返す' {
        $report = Invoke-Boundary | ConvertFrom-Json

        @($report.results).Count | Should -Be 8
        @($report.results | Where-Object { $_.expected -ne 'observe' -and $_.actual -ne $_.expected }) | Should -BeNullOrEmpty
    }

    It 'observe の操作（DeleteOnTermination=false）は <Code> でも失敗させず、結果を記録する' -ForEach @(
        @{ Code = 'DryRunOperation'; Actual = 'allowed' }
        @{ Code = 'UnauthorizedOperation'; Actual = 'denied' }
        @{ Code = 'InvalidParameterCombination'; Actual = 'unknown' }
    ) {
        $global:FakeState['retain-root-volume'] = $Code

        $report = Invoke-Boundary | ConvertFrom-Json

        ($report.results | Where-Object name -EQ 'retain-root-volume').actual | Should -Be $Actual
    }

    It '永続 instance の subnet と Security Group を上書きの値に使い、すべて --dry-run と region 付きで呼ぶ' {
        Invoke-Boundary | Out-Null

        $dryRuns = @($global:FakeCalls | Where-Object { $_ -like '*--dry-run*' })
        $dryRuns.Count | Should -Be 8
        $dryRuns | ForEach-Object { $_ | Should -BeLike '*--region us-east-1*' }
        @($dryRuns | Where-Object { $_ -like '*DeviceIndex=0,SubnetId=subnet-05a6dbdf30e0b6664,Groups=sg-0f5751845b3fa696b*' }).Count | Should -Be 1
        @($global:FakeCalls | Where-Object { $_ -notlike '*--dry-run*' -and $_ -notlike 'ec2 describe-instances*' }) | Should -BeNullOrEmpty
    }

    It '<Case> が <Code> なら失敗させる' -ForEach @(
        @{ Case = 'override-instance-type'; Code = 'DryRunOperation' }
        @{ Case = 'terminate-persistent-instance'; Code = 'DryRunOperation' }
        @{ Case = 'launch-template-default'; Code = 'UnauthorizedOperation' }
        @{ Case = 'override-network-interface'; Code = 'InvalidParameterCombination' }
    ) {
        $global:FakeState[$Case] = $Code

        { Invoke-Boundary } | Should -Throw "*$Case*"
    }
}
