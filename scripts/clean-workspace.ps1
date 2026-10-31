[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$separator = [IO.Path]::DirectorySeparatorChar
$prefix = $repo + $separator

# Limit cleanup to generated directories of versioned projects. Never use git clean:
# ignored files also include model weights, native dependencies and the private dataset.
$tracked = @(git -C $repo ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Git inventory failed; cleanup cancelled.' }
$trackedPaths = @($tracked | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repo $_)) })
$protectedPaths = @(
    (Join-Path $repo 'artifacts/android/assets'),
    (Join-Path $repo 'artifacts/android/signing')
)
$activeOutputs = @(Get-Process -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $executable = $_.Path
        if ($executable -and $executable.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            Split-Path $executable -Parent
        }
    } catch { } # Some system processes do not expose their executable path.
} | Sort-Object -Unique)

$candidates = @((Join-Path $repo 'artifacts'))
foreach ($project in $tracked | Where-Object { $_.EndsWith('.csproj') }) {
    $directory = Split-Path (Join-Path $repo $project) -Parent
    foreach ($name in @('bin', 'obj', 'artifacts', 'TestResults')) {
        $candidates += Join-Path $directory $name
    }
}
$candidates = @($candidates | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath $_ })

# Inspect all candidates before the first deletion. Protect unexpected signing files
# as well as the normal signing directory; refuse links instead of traversing them.
foreach ($candidate in $candidates) {
    $resolved = [IO.Path]::GetFullPath($candidate)
    if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup target outside repository: $resolved"
    }
    $parent = (Get-Item -LiteralPath $resolved -Force).Parent
    while ($parent -and $parent.FullName.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Linked parent requires manual inspection: $($parent.FullName)"
        }
        $parent = $parent.Parent
    }
    if (@($trackedPaths | Where-Object { $_.StartsWith($resolved + $separator, [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "Versioned files in cleanup target: $resolved"
    }
    $items = @((Get-Item -LiteralPath $resolved -Force)) + @(Get-ChildItem -LiteralPath $resolved -Force -Recurse)
    foreach ($item in $items) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Linked path requires manual inspection: $($item.FullName)"
        }
        if (!$item.PSIsContainer -and ($item.Extension -in @('.keystore', '.jks') -or $item.Name -in @('password.txt', 'secrets.json', 'credentials.json', '.env'))) {
            $protectedPaths += $item.FullName
        }
    }
}

$removedBytes = [long]0
$failed = [Collections.Generic.List[string]]::new()
function Remove-GeneratedPath([string]$path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $path = [IO.Path]::GetFullPath($path)
    if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup target outside repository: $path"
    }
    $childPrefix = $path + $separator
    foreach ($protected in $protectedPaths) {
        if ($path.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith($protected + $separator, [StringComparison]::OrdinalIgnoreCase)) { return }
    }
    if ($activeOutputs -contains $path) {
        Write-Output "Running application preserved: $([IO.Path]::GetRelativePath($repo, $path))"
        # RID subfolders are independent publish/build outputs, not the native
        # libraries in runtimes/. Keep the files used by the running application.
        foreach ($child in Get-ChildItem -LiteralPath $path -Directory -Force | Where-Object Name -Match '^(win|linux|osx)-(x64|x86|arm64|arm)$') {
            Remove-GeneratedPath $child.FullName
        }
        return
    }
    $containsProtected = @($protectedPaths + $activeOutputs | Where-Object { $_.StartsWith($childPrefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
    if ($containsProtected) {
        foreach ($child in Get-ChildItem -LiteralPath $path -Force) { Remove-GeneratedPath $child.FullName }
        return
    }
    if (@($trackedPaths | Where-Object { $_.Equals($path, [StringComparison]::OrdinalIgnoreCase) -or $_.StartsWith($childPrefix, [StringComparison]::OrdinalIgnoreCase) }).Count) {
        throw "Versioned files in cleanup target: $path"
    }
    $bytes = if ((Get-Item -LiteralPath $path -Force).PSIsContainer) {
        (Get-ChildItem -LiteralPath $path -Force -Recurse -File | Measure-Object Length -Sum).Sum
    } else { (Get-Item -LiteralPath $path -Force).Length }
    $relative = [IO.Path]::GetRelativePath($repo, $path)
    if ($PSCmdlet.ShouldProcess($relative, ('Remove generated files ({0:N2} GiB)' -f ($bytes / 1GB)))) {
        try {
            Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
            $script:removedBytes += $bytes
            Write-Output ('Removed {0} ({1:N2} GiB)' -f $relative, ($bytes / 1GB))
        } catch {
            $failed.Add($relative)
            Write-Warning "Could not completely remove ${relative}: $($_.Exception.Message)"
        }
    }
}

foreach ($candidate in $candidates) { Remove-GeneratedPath $candidate }
Write-Output ('Removed successfully: {0:N2} GiB' -f ($removedBytes / 1GB))
if ($failed.Count) { throw "Some files are in use or inaccessible. Remaining targets: $($failed -join ', ')" }
