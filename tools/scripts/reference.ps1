<#
.SYNOPSIS
    Эталон SWORD (этап 0 ARCHITECTURE_PLAN.md): закрепить или проверить эталонный пак контента.

.DESCRIPTION
    -Pin    копирует Content.swpak из замороженного SWORD в Content.reference.swpak (только для чтения, вне git)
            и записывает tests/reference/reference.json: коммит SWORD, SHA-256 и размер пака, сведения о его сборке,
            версия .NET SDK, ОС, видеокарты, разрешение снимков.
    -Check  сверяет SHA-256 и размер Content.reference.swpak с reference.json. Запускается перед каждой сверкой
            с SWORD: если пак перепекли, эталон незаметно поменялся бы.

.PARAMETER Json
    Итог одним JSON-объектом в stdout (для агентов и verify.ps1).

.EXAMPLE
    powershell -NoProfile -File tools/scripts/reference.ps1 -Check
#>
param(
    [switch]$Pin,
    [switch]$Check,
    [switch]$Json,
    [switch]$Force,
    [string]$Source = 'D:\Work\Visual Studio\SWORD',
    [string]$Commit = '2eab13b2262f6b336c3fc9bdcd71fb3dfc5a51a4',
    [string]$Resolution = '1280x720'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$referencePak = Join-Path $root 'Content.reference.swpak'
$referenceJson = Join-Path $root 'tests\reference\reference.json'

function Write-Utf8Crlf([string]$Path, [string]$Text) {
    $normalized = ($Text -replace "`r`n", "`n") -replace "`n", "`r`n"
    if (-not $normalized.EndsWith("`r`n")) { $normalized += "`r`n" }
    [System.IO.File]::WriteAllText($Path, $normalized, (New-Object System.Text.UTF8Encoding $false))
}

function ConvertFrom-JsonText([string]$Text) {
    # В Windows PowerShell 5.1 у ConvertFrom-Json предел длины строки; сведения о сборке пака бывают большими.
    if ($PSVersionTable.PSVersion.Major -ge 6) { return $Text | ConvertFrom-Json }
    Add-Type -AssemblyName System.Web.Extensions
    $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
    $serializer.MaxJsonLength = [int]::MaxValue
    return $serializer.DeserializeObject($Text)
}

function Get-Field($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        foreach ($key in $Object.Keys) { if ($key -ieq $Name) { return $Object[$key] } }
        return $null
    }
    $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $Name } | Select-Object -First 1
    if ($property) { return $property.Value }
    return $null
}

# Оглавление пака .swpak (формат — Sword.Content.PakFormat в SWORD): число файлов и сведения о сборке.
function Read-PakInfo([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $reader = New-Object System.IO.BinaryReader($stream)
        $magic = [System.Text.Encoding]::ASCII.GetString($reader.ReadBytes(4))
        if ($magic -ne 'SWPK') { throw "не пак .swpak: $Path" }
        $version = $reader.ReadUInt16(); [void]$reader.ReadUInt16()
        $count = $reader.ReadUInt32(); [void]$reader.ReadUInt32()
        $tocOffset = $reader.ReadUInt64(); [void]$reader.ReadUInt64()
        $stream.Position = [long]$tocOffset
        $buildEntry = $null
        for ($i = 0; $i -lt $count; $i++) {
            $length = $reader.ReadUInt16()
            $name = [System.Text.Encoding]::UTF8.GetString($reader.ReadBytes($length))
            $offset = $reader.ReadInt64(); $size = $reader.ReadInt64(); [void]$reader.ReadUInt64()
            if ($name -eq '.bake/build.json') { $buildEntry = @($offset, $size) }
        }
        $info = [ordered]@{ formatVersion = [int]$version; files = [int]$count }
        if ($buildEntry) {
            $stream.Position = $buildEntry[0]
            $text = [System.Text.Encoding]::UTF8.GetString($reader.ReadBytes([int]$buildEntry[1]))
            $build = ConvertFrom-JsonText $text
            $info.bakeProject = [string](Get-Field $build 'project')
            $info.builtUtc = [string](Get-Field $build 'builtUtc')
            $assets = Get-Field $build 'assets'
            if ($assets -is [System.Collections.IDictionary]) { $info.assets = $assets.Count }
            elseif ($assets) { $info.assets = @($assets.PSObject.Properties).Count }
        }
        return $info
    }
    finally { $stream.Dispose() }
}

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

function Out-Result($Result) {
    if ($Json) { $Result | ConvertTo-Json -Depth 6 -Compress }
    else { $Result.GetEnumerator() | ForEach-Object { '{0,-10} {1}' -f $_.Key, ($_.Value | Out-String).Trim() } }
}

if ($Pin -eq $Check) { throw 'укажите ровно один режим: -Pin или -Check' }

if ($Pin) {
    $sourcePak = Join-Path $Source 'Content.swpak'
    if (-not (Test-Path -LiteralPath $sourcePak)) { throw "нет пака $sourcePak" }

    $head = (& git -C $Source rev-parse HEAD).Trim()
    $dirty = (& git -C $Source status --porcelain) -join "`n"
    if ($head -ne $Commit -and -not $Force) { throw "SWORD на коммите $head, ожидается $Commit (-Force — закрепить всё равно)" }
    if ($dirty -and -not $Force) { throw "в SWORD есть незакоммиченные изменения (-Force — закрепить всё равно)" }

    $sourceHash = Get-Sha256 $sourcePak
    if (Test-Path -LiteralPath $referencePak) {
        $existing = Get-Sha256 $referencePak
        if ($existing -ne $sourceHash -and -not $Force) { throw "Content.reference.swpak уже есть и отличается от пака SWORD (-Force — заменить)" }
        if ($existing -ne $sourceHash) { (Get-Item -LiteralPath $referencePak).IsReadOnly = $false; Copy-Item -LiteralPath $sourcePak -Destination $referencePak -Force }
    }
    else { Copy-Item -LiteralPath $sourcePak -Destination $referencePak }
    (Get-Item -LiteralPath $referencePak).IsReadOnly = $true

    $hash = Get-Sha256 $referencePak
    if ($hash -ne $sourceHash) { throw 'копия пака не совпала с исходным по SHA-256' }

    $gpus = @(Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{ name = $_.Name; driver = $_.DriverVersion } })
    $reference = [ordered]@{
        purpose       = 'Эталон SWORD для сверки фич SWORD2 (этап 0 ARCHITECTURE_PLAN.md). Сверки с SWORD — только на этом паке.'
        sourceRepo    = $Source
        sourceCommit  = $head
        sourceClean   = -not $dirty
        content       = [ordered]@{
            file      = 'Content.reference.swpak'
            sha256    = $hash
            size      = (Get-Item -LiteralPath $referencePak).Length
            pak       = Read-PakInfo $referencePak
        }
        environment   = [ordered]@{
            dotnetSdk = (& dotnet --version).Trim()
            os        = [System.Environment]::OSVersion.VersionString
            gpus      = $gpus
        }
        resolution    = $Resolution
        pinnedUtc     = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $referenceJson) | Out-Null
    Write-Utf8Crlf $referenceJson ($reference | ConvertTo-Json -Depth 6)
    Out-Result ([ordered]@{ ok = $true; mode = 'pin'; sha256 = $hash; commit = $head; reference = $referenceJson })
    exit 0
}

# -Check
if (-not (Test-Path -LiteralPath $referenceJson)) { Out-Result ([ordered]@{ ok = $false; mode = 'check'; error = "нет $referenceJson — сначала -Pin" }); exit 1 }
if (-not (Test-Path -LiteralPath $referencePak)) { Out-Result ([ordered]@{ ok = $false; mode = 'check'; error = "нет $referencePak — сначала -Pin" }); exit 1 }

$expected = ConvertFrom-JsonText ([System.IO.File]::ReadAllText($referenceJson))
$expectedContent = Get-Field $expected 'content'
$expectedHash = [string](Get-Field $expectedContent 'sha256')
$expectedSize = [long](Get-Field $expectedContent 'size')
$actualHash = Get-Sha256 $referencePak
$actualSize = (Get-Item -LiteralPath $referencePak).Length
$ok = ($actualHash -eq $expectedHash) -and ($actualSize -eq $expectedSize)
Out-Result ([ordered]@{ ok = $ok; mode = 'check'; expected = $expectedHash; actual = $actualHash; size = $actualSize })
if ($ok) { exit 0 } else { exit 1 }
