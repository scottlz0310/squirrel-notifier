BeforeAll {
    Import-Module (Join-Path $PSScriptRoot 'DesktopE2ELaunchAutomation.psm1') -Force
}

Describe 'Assert-DesktopE2ELaunchStorage' {
    BeforeEach {
        $script:Template = [pscustomobject]@{ ImageId = 'ami-0123456789abcdef0' }
        $script:Image = [pscustomobject]@{
            RootDeviceName = '/dev/sda1'
            BlockDeviceMappings = @(
                [pscustomobject]@{ DeviceName = '/dev/sda1'; Ebs = [pscustomobject]@{ VolumeSize = 64 } }
                [pscustomobject]@{ DeviceName = 'xvdca'; VirtualName = 'ephemeral0' }
            )
        }
    }

    It 'root EBS だけなら許可する' {
        { Assert-DesktopE2ELaunchStorage -LaunchTemplateData $script:Template -Image $script:Image } | Should -Not -Throw
    }

    It '<Case> は拒否する' -ForEach @(
        @{ Case = 'Launch Template の追加 volume'; Setup = { param($t, $i) $t | Add-Member -NotePropertyName BlockDeviceMappings -NotePropertyValue @(@{ DeviceName = '/dev/sdf' }) }; Message = '*追加 block device*' }
        @{ Case = 'Launch Template の UserData'; Setup = { param($t, $i) $t | Add-Member -NotePropertyName UserData -NotePropertyValue 'dGVzdA==' }; Message = '*UserData*' }
        @{ Case = 'AMI の追加 EBS'; Setup = { param($t, $i) $i.BlockDeviceMappings += [pscustomobject]@{ DeviceName = '/dev/sdf'; Ebs = [pscustomobject]@{ VolumeSize = 8 } } }; Message = '*root device 1 本*' }
    ) {
        & $Setup $script:Template $script:Image

        { Assert-DesktopE2ELaunchStorage -LaunchTemplateData $script:Template -Image $script:Image } | Should -Throw $Message
    }
}
