$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# ASCII-only script: Arabic keywords are built from Unicode code points.
function A([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }

$W_RASMIYA = A @(0x0631,0x0633,0x0645,0x064A,0x0629)             # "rasmeya"
$W_MUHIMA  = A @(0x0645,0x0647,0x0645,0x0629)                   # "muhima"
$W_INTIDAB = A @(0x0627,0x0646,0x062A,0x062F,0x0627,0x0628)     # "intidab"
$W_TADRIB  = A @(0x062A,0x062F,0x0631,0x064A,0x0628)            # "tadrib"

$officialRx = (@($W_RASMIYA, $W_MUHIMA, $W_INTIDAB, $W_TADRIB) | ForEach-Object { [regex]::Escape($_) }) -join '|'

$path = 'd:\TIME\data\review_revert.xlsx'
$out  = 'd:\TIME\data\verify_restore.txt'

$zip = [System.IO.Compression.ZipFile]::OpenRead($path)
function RE($n) {
    $e = $zip.GetEntry($n)
    if (-not $e) { return $null }
    $s = $e.Open()
    $r = New-Object System.IO.StreamReader($s, [System.Text.Encoding]::UTF8)
    $t = $r.ReadToEnd(); $r.Close(); return $t
}

$ss = RE 'xl/sharedStrings.xml'
$strings = New-Object System.Collections.Generic.List[string]
if ($ss) {
    $x = [xml]$ss
    foreach ($si in $x.sst.si) {
        $strings.Add((($si.SelectNodes('.//*[local-name()=''t'']') | ForEach-Object { $_.InnerText }) -join ''))
    }
}

$L = New-Object System.Collections.Generic.List[string]

$targets = @(
    @{ F = 'xl/worksheets/sheet1.xml'; Col = 'A'; Label = 'sheet1 summary' },
    @{ F = 'xl/worksheets/sheet2.xml'; Col = 'D'; Label = 'sheet2 118-b violations' },
    @{ F = 'xl/worksheets/sheet4.xml'; Col = 'J'; Label = 'sheet4 request detail' }
)

foreach ($t in $targets) {
    $x = [xml](RE $t.F)
    $rows = $x.worksheet.sheetData.row
    $rx = [regex]("^$($t.Col)\d+$")
    $types = @{}
    $official = 0
    $total = 0
    $rn = 0
    foreach ($row in $rows) {
        $rn++
        if ($rn -eq 1) { continue }
        $total++
        $cell = $row.c | Where-Object { $_.r -match $rx } | Select-Object -First 1
        if (-not $cell) { continue }
        $val = $cell.v
        if ($cell.t -eq 's' -and $val -ne $null) { $val = $strings[[int]$val] }
        if ($null -eq $val) { continue }
        $val = [string]$val
        if ($types.ContainsKey($val)) { $types[$val]++ } else { $types[$val] = 1 }
        if ($val -match $officialRx) { $official++ }
    }
    $L.Add("=== $($t.Label) [$($t.F)] : data rows=$total, official-duty matches=$official ===")
    foreach ($k in ($types.Keys | Sort-Object)) { $L.Add("     $k = $($types[$k])") }
}

$zip.Dispose()
$L | Set-Content -Path $out -Encoding UTF8
Write-Host "written: $out"
