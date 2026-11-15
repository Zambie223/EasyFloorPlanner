<#
.SYNOPSIS
    Runs Easy Floor Planner's in-game self-test with no one at the keyboard.

.DESCRIPTION
    1. Builds the mod (Debug; this is the only configuration that contains the self-test) and deploys it.
    2. Backs up the save folders (Full, Profile, Progress) into the run folder.
    3. Writes the mod's single-use, expiring selftest.flag. While the flag is active, the mod blocks
       every save write.
    4. Launches PlateUp through Steam and drives the menus with AutoHotkey v2:
         "Press any button" -> Space, main menu -> Enter (Play), profile -> Down, Enter (Start).
       It takes a screenshot of the game window after each step.
    5. The mod starts a random restaurant, runs its checks (layout edits, a save and reload through
       memory-only saves, then a real day with customers), and quits the game. If a popup pauses
       the game before the day, the mod logs "PAUSED #n" and the script presses Enter to close it.
    6. Copies Player.log into the run folder, prints the [SELFTEST] lines, and verifies the save
       folders are byte-identical to the backup.

    While it runs, keyboard input goes to the PlateUp window, so don't use the PC.
    Output: _testruns\<timestamp>\ (git-ignored).

.EXAMPLE
    ./tools/Run-SelfTest.ps1
#>
[CmdletBinding()]
param(
    [int]$TimeoutMinutes = 12,
    [switch]$NoBuild,
    # Optional restaurant setting to start the run in, e.g. -Setting Turbo (default: whatever HQ has selected).
    [string]$Setting
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$gameDir = if ($env:PLATEUP_GAME_DIR) { $env:PLATEUP_GAME_DIR } else { "${env:ProgramFiles(x86)}\Steam\steamapps\common\PlateUp\PlateUp" }
$modDir = Join-Path $gameDir 'Mods\EasyFloorPlanner'
$saveDir = Join-Path $env:USERPROFILE "AppData\LocalLow\It's Happening\PlateUp"
$log = Join-Path $saveDir 'Player.log'
$ahkExe = "$env:ProgramFiles\AutoHotkey\v2\AutoHotkey64.exe"
$ahkScript = Join-Path $PSScriptRoot 'automation\Send-ToPlateUp.ahk'
$capture = Join-Path $PSScriptRoot 'automation\Capture-GameWindow.ps1'

$run = Join-Path $repo ("_testruns\" + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
$shots = Join-Path $run 'shots'
New-Item -ItemType Directory -Force $shots | Out-Null

function Say($m) { Write-Host "[$(Get-Date -Format HH:mm:ss)] $m" }
function Send-Key([string]$key) {
    $p = Start-Process $ahkExe -ArgumentList "`"$ahkScript`" $key" -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "AutoHotkey failed to send '$key' (exit $($p.ExitCode)); is the game window open?" }
}
function Shot([string]$name) {
    try { & $capture -OutFile (Join-Path $shots "$name.png") | Out-Null } catch { Say "screenshot '$name' failed: $($_.Exception.Message)" }
}
function Log-Has([string]$pattern) {
    (Test-Path $log) -and ((Get-Item $log).LastWriteTime -gt $script:launchTime) -and (Select-String -Path $log -Pattern $pattern -Quiet)
}
function Wait-For([scriptblock]$cond, [int]$seconds, [string]$what) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $cond) { return $true }
        Start-Sleep -Milliseconds 1000
    }
    Say "timed out after ${seconds}s waiting for: $what"
    return $false
}
function Save-Hashes([string]$root) {
    foreach ($d in 'Full', 'Profile', 'Progress') {
        $base = Join-Path $root $d
        if (Test-Path $base) {
            Get-ChildItem -Recurse -File $base | ForEach-Object { "$d$($_.FullName.Substring($base.Length)) $((Get-FileHash $_.FullName -Algorithm MD5).Hash)" }
        }
    }
}

# ---- preconditions ----
if (Get-Process PlateUp -ErrorAction SilentlyContinue) { throw 'PlateUp is running. Close it first.' }
if (-not (Test-Path $ahkExe)) { throw "AutoHotkey v2 not found at $ahkExe" }
if (-not (Get-Process steam -ErrorAction SilentlyContinue)) { throw 'Steam is not running.' }

# ---- build ----
if (-not $NoBuild) {
    Say 'building EasyFloorPlanner (Debug)...'
    dotnet build (Join-Path $repo 'mods\EasyFloorPlanner') -c Debug -v:q -nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
}

# ---- backup ----
$backup = Join-Path $run 'save-backup'
New-Item -ItemType Directory -Force $backup | Out-Null
foreach ($d in 'Full', 'Profile', 'Progress') {
    if (Test-Path (Join-Path $saveDir $d)) { Copy-Item -Recurse (Join-Path $saveDir $d) $backup }
}
$before = Save-Hashes $saveDir
Say "saves backed up to $backup"

# ---- flag + launch ----
$expiry = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() + ($TimeoutMinutes * 60) + 300
$flagLines = @("$expiry", $shots, 'quit')
if ($Setting) { $flagLines += "setting=$Setting" }
[IO.File]::WriteAllLines((Join-Path $modDir 'selftest.flag'), $flagLines)
$script:launchTime = Get-Date
Start-Process 'steam://rungameid/1599600'
Say 'launched; waiting for the mod to activate...'

$ok = $true
try {
    if (-not (Wait-For { Log-Has '\[SELFTEST\] ACTIVE' } 120 'self-test activation in Player.log')) { throw 'self-test never activated (flag missing/expired, or the mod failed to load)' }
    if (-not (Wait-For { Get-Process PlateUp -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } } 60 'game window')) { throw 'no game window' }

    # Title screen: press Space until the game registers the keyboard.
    Say 'title screen: pressing Space until the keyboard is registered...'
    $joined = Wait-For { Send-Key 'Space'; Start-Sleep -Seconds 3; Log-Has 'Controller Keyboard.*assigned' } 120 'keyboard registration at the title screen'
    Shot 'nav-1-main-menu'
    if (-not $joined) { throw 'title screen never accepted input' }

    Say 'main menu: Enter (Play)'
    Send-Key 'Enter'; Start-Sleep -Seconds 4; Shot 'nav-2-profile'
    Say 'profile: Down, Enter (Start)'
    Send-Key 'Down'; Start-Sleep -Milliseconds 800; Send-Key 'Enter'

    if (-not (Wait-For { Log-Has '\[SELFTEST\] in HQ' } 90 'HQ')) { Shot 'stuck-before-hq'; throw 'never reached HQ; see stuck-before-hq.png' }
    Say 'in HQ; the mod is starting a restaurant. Waiting for the test to finish...'

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes); $n = 0; $pausesAnswered = 0; $nextShot = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (Log-Has '\[SELFTEST\] DONE') { break }
        if (-not (Get-Process PlateUp -ErrorAction SilentlyContinue)) { Say 'game exited before DONE'; break }
        # The mod logs "PAUSED #n" when a popup (e.g. the dish card) blocks the day from starting.
        $pauses = @(Select-String -Path $log -Pattern '\[SELFTEST\] PAUSED #' -ErrorAction SilentlyContinue).Count
        if ($pauses -gt $pausesAnswered) {
            $pausesAnswered = $pauses
            Say "game paused by a popup (#$pauses): pressing Enter to close it"
            Send-Key 'Enter'
        }
        if ((Get-Date) -ge $nextShot) { $n++; Shot ("progress-{0:00}" -f $n); $nextShot = (Get-Date).AddSeconds(15) }
        Start-Sleep -Seconds 2
    }
    if (-not (Log-Has '\[SELFTEST\] DONE')) { $ok = $false; Say 'test did not finish' }
    [void](Wait-For { -not (Get-Process PlateUp -ErrorAction SilentlyContinue) } 30 'game to quit')
}
catch {
    $ok = $false
    Say "ERROR: $($_.Exception.Message)"
}
finally {
    $flag = Join-Path $modDir 'selftest.flag'
    if (Test-Path $flag) { Remove-Item $flag; Say 'removed unused flag file' }
    if (Test-Path $log) { Copy-Item $log (Join-Path $run 'Player.log') }
}

# ---- results ----
$lines = if (Test-Path (Join-Path $run 'Player.log')) { Select-String -Path (Join-Path $run 'Player.log') -Pattern '\[SELFTEST\]' | ForEach-Object { $_.Line } } else { @() }
$lines | Set-Content (Join-Path $run 'selftest.log')
$lines | Where-Object { $_ -match 'FAIL|DONE|ERROR' } | ForEach-Object { Write-Host $_ }

if (Get-Process PlateUp -ErrorAction SilentlyContinue) {
    Say 'game still running; save comparison skipped (close it, then compare against the backup).'
}
else {
    $after = Save-Hashes $saveDir
    $diff = Compare-Object $before $after
    if ($diff) { $ok = $false; Say 'WARNING: save files changed during the run!'; $diff | Out-Host }
    else { Say 'saves unchanged (byte-identical to the backup).' }
}

$done = $lines | Where-Object { $_ -match 'DONE pass=(\d+) fail=(\d+)' } | Select-Object -Last 1
if (-not $done -or $done -notmatch 'fail=0') { $ok = $false }
Say "run folder: $run"
if ($ok) { Say 'RESULT: PASS'; exit 0 } else { Say 'RESULT: FAIL'; exit 1 }
