# Tests the settings.json merge/unmerge in Noodle.exe against temp files only, the Windows twin of
# scripts/test-hooks.sh. Build first: dotnet build windows/Noodle
# Usage: windows/test/hooks-test.ps1 [-Exe path\to\Noodle.exe] [-Fixture settings.json to round-trip a copy of]
param(
    [string]$Exe = "$PSScriptRoot\..\Noodle\bin\Debug\net8.0-windows\Noodle.exe",
    [string]$Fixture = ""
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
$T = Join-Path ([IO.Path]::GetTempPath()) ("noodle-hooks-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $T | Out-Null
$script:pass = 0; $script:fail = 0
$fwd = $Exe.Replace('\', '/')
$P = "`"$fwd`" --event prompt"
$S = "`"$fwd`" --event stop"

function Check([string]$name, [scriptblock]$test) {
    $ok = $false
    try { $ok = [bool](& $test) } catch { }
    if ($ok) { Write-Host "  ok   $name"; $script:pass++ } else { Write-Host "  FAIL $name" -ForegroundColor Red; $script:fail++ }
}

# Runs `Noodle.exe setup|remove -y` on $file, keeps the output in $script:out and the exit code in $script:rc.
function Run([string]$file, [string]$verb, [string]$stdin = $null) {
    $env:CLAUDE_SETTINGS = $file; $env:SKIP_APP = '1'
    $psi = New-Object Diagnostics.ProcessStartInfo $Exe
    $psi.Arguments = if ($stdin -ne $null -and $stdin -ne '') { $verb } else { "$verb -y" }
    $psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardInput = $true
    $p = [Diagnostics.Process]::Start($psi)
    if ($stdin) { $p.StandardInput.WriteLine($stdin) }
    $p.StandardInput.Close()
    $script:out = $p.StandardOutput.ReadToEnd(); $p.WaitForExit(); $script:rc = $p.ExitCode
}

function Count([string]$file, [string]$cmd) {
    function Walk($node) {
        if ($node -is [Array]) { return ($node | ForEach-Object { Walk $_ } | Measure-Object -Sum).Sum }
        if ($node -is [Management.Automation.PSCustomObject]) {
            $n = if ($node.PSObject.Properties.Name -contains 'command' -and $node.command -eq $cmd) { 1 } else { 0 }
            foreach ($prop in $node.PSObject.Properties) { $n += Walk $prop.Value }
            return $n
        }
        return 0
    }
    [int](Walk (Get-Content -Raw $file | ConvertFrom-Json))
}
function Same([string]$a, [string]$b) { [IO.File]::ReadAllText($a) -ceq [IO.File]::ReadAllText($b) }
function Baks([string]$file) { @(Get-ChildItem "$file.bak-*" -ErrorAction SilentlyContinue).Count }
function Lf([string]$s) { $s.Replace("`r`n", "`n") }

Write-Host "missing file"
$F = "$T\missing\settings.json"
Run $F setup
Check "created with both hooks" { (Count $F $P) -eq 1 -and (Count $F $S) -eq 1 }
Check "no backup for a new file" { (Baks $F) -eq 0 }
Run $F remove
Check "remove leaves {}" { (Get-Content -Raw $F).Trim() -eq '{}' }

$other = Lf @'
{
  "model": "opus",
  "permissions": {
    "allow": [
      "Bash(git status)"
    ]
  },
  "hooks": {
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "echo hi"
          }
        ]
      }
    ],
    "Stop": [
      {
        "matcher": "",
        "hooks": [
          {
            "type": "command",
            "command": "powershell -c \"[console]::beep()\""
          }
        ]
      }
    ],
    "PreToolUse": [
      {
        "matcher": "Bash",
        "hooks": [
          {
            "type": "command",
            "command": "./guard.sh"
          }
        ]
      }
    ]
  }
}

'@

foreach ($eol in @("LF", "CRLF")) {
    Write-Host "other hooks and settings ($eol)"
    $D = "$T\other-$eol"; New-Item -ItemType Directory $D | Out-Null
    $F = "$D\settings.json"
    $text = if ($eol -eq "CRLF") { $other.Replace("`n", "`r`n") } else { $other }
    [IO.File]::WriteAllText($F, $text); [IO.File]::WriteAllText("$D\orig.json", $text)
    Run $F setup
    Check "ours added once each" { (Count $F $P) -eq 1 -and (Count $F $S) -eq 1 }
    Check "other hooks kept" { (Count $F 'echo hi') -eq 1 -and (Count $F './guard.sh') -eq 1 }
    Check "other settings kept" { $j = Get-Content -Raw $F | ConvertFrom-Json; $j.model -eq 'opus' -and $j.permissions.allow[0] -eq 'Bash(git status)' }
    Check "line endings kept" { ([IO.File]::ReadAllText($F).Contains("`r`n")) -eq ($eol -eq "CRLF") }
    Check "backup created and equals original" { (Baks $F) -eq 1 -and (Same (Get-ChildItem "$F.bak-*").FullName "$D\orig.json") }
    Copy-Item $F "$D\once.json"
    Start-Sleep -Milliseconds 1100; Run $F setup
    Check "second setup changes nothing" { Same $F "$D\once.json" }
    Check "second setup says nothing to add" { $script:out -match 'nothing to add' }
    Check "second setup makes no backup" { (Baks $F) -eq 1 }
    Run $F remove
    Check "round trip is byte-identical" { Same $F "$D\orig.json" }
    Run $F remove
    Check "second remove is a no-op" { (Same $F "$D\orig.json") -and $script:out -match 'nothing to remove' }
}

Write-Host "4-space indent"
$F = "$T\indent.json"
[IO.File]::WriteAllText($F, "{`n    `"model`": `"opus`"`n}`n")
Run $F setup
Check "indent kept" { [IO.File]::ReadAllText($F).Contains("`n    `"hooks`": {`n        `"UserPromptSubmit`"") }

Write-Host "our command already inside someone else's group"
$F = "$T\shared.json"
$shared = @{ hooks = @{ UserPromptSubmit = @(@{ hooks = @(@{ type = 'command'; command = 'echo a' }, @{ type = 'command'; command = $P }) }) } }
[IO.File]::WriteAllText($F, ($shared | ConvertTo-Json -Depth 10))
Run $F setup
Check "no duplicate prompt hook" { (Count $F $P) -eq 1 -and (Count $F $S) -eq 1 }
Run $F remove
Check "remove takes only ours, keeps their group" {
    (Count $F $P) -eq 0 -and (Count $F 'echo a') -eq 1 -and @((Get-Content -Raw $F | ConvertFrom-Json).hooks.UserPromptSubmit).Count -eq 1
}

Write-Host "a Noodle hook for another copy"
$F = "$T\stale.json"
$old = '"C:/Old Place/Noodle.exe" --event prompt'
[IO.File]::WriteAllText($F, (@{ hooks = @{ UserPromptSubmit = @(@{ hooks = @(@{ type = 'command'; command = $old }) }) } } | ConvertTo-Json -Depth 10))
Run $F setup
Check "stale path replaced, not duplicated" { (Count $F $old) -eq 0 -and (Count $F $P) -eq 1 -and @((Get-Content -Raw $F | ConvertFrom-Json).hooks.UserPromptSubmit).Count -eq 1 }

Write-Host "invalid JSON"
$F = "$T\bad.json"; [IO.File]::WriteAllText($F, "{ nope`n")
Run $F setup
Check "refuses, nonzero exit, file untouched" { $script:rc -ne 0 -and [IO.File]::ReadAllText($F) -eq "{ nope`n" -and (Baks $F) -eq 0 }

Write-Host "declining the prompt"
$F = "$T\decline.json"; [IO.File]::WriteAllText($F, "{}")
Run $F setup 'n'
Check "file unchanged when not confirmed" { [IO.File]::ReadAllText($F) -eq "{}" }
Check "asked first" { $script:out -match 'Write these changes\?' }

if ($Fixture) {
    Write-Host "round trip on a copy of $Fixture"
    $F = "$T\real\settings.json"; New-Item -ItemType Directory "$T\real" | Out-Null; Copy-Item $Fixture $F
    Run $F setup; Run $F setup; Run $F remove
    Check "byte-identical after setup x2 + remove" { Same $F $Fixture }
}

Remove-Item Env:CLAUDE_SETTINGS, Env:SKIP_APP -ErrorAction SilentlyContinue
Write-Host "$script:pass passed, $script:fail failed  (temp files in $T)"
exit [int]($script:fail -ne 0)
