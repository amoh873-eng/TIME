$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$path = 'd:\TIME\data\review_118c.xlsx'
$out  = 'd:\TIME\data\inspect_118c.txt'

$zip = [System.IO.Compression.ZipFile]::OpenRead($path)
function RE($n) {
    $e = $zip.GetEntry($n)
    if (-not $e) { return $null }
    $s = $e.Open()
    $r = New-Object System.IO.StreamReader($s, [System.Text.Encoding]::UTF8)
    $t = $r.ReadToEnd(); $r.Close(); return $t
}

$L = New-Object System.Collections.Generic.List[string]

# أسماء الأوراق وربطها بالملفات
$wb = [xml](RE 'xl/workbook.xml')
$rels = [xml](RE 'xl/_rels/workbook.xml.rels')
$relMap = @{}
foreach ($rel in $rels.Relationships.Relationship) { $relMap[$rel.Id] = $rel.Target }
foreach ($sh in $wb.workbook.sheets.sheet) {
    $target = $relMap[$sh.id]
    if ($target -notlike 'xl/*') { $target = 'xl/' + $target }
    $L.Add("SHEET: $($sh.name)  ->  $target")
}

$ss = RE 'xl/sharedStrings.xml'
$strings = New-Object System.Collections.Generic.List[string]
if ($ss) {
    $x = [xml]$ss
    foreach ($si in $x.sst.si) {
        $strings.Add((($si.SelectNodes('.//*[local-name()=''t'']') | ForEach-Object { $_.InnerText }) -join ''))
    }
}

$sheetFiles = $zip.Entries | Where-Object { $_.FullName -like 'xl/worksheets/sheet*.xml' } | ForEach-Object { $_.FullName } | Sort-Object

foreach ($sf in $sheetFiles) {
    $x = [xml](RE $sf)
    $rows = $x.worksheet.sheetData.row
    $L.Add("=== $sf : rows=$($rows.Count) ===")
    $rn = 0
    foreach ($row in $rows) {
        $rn++
        if ($rn -gt 4) { break }
        $cells = @()
        foreach ($c in $row.c) {
            $v = $c.v
            if ($c.t -eq 's' -and $v -ne $null) { $v = $strings[[int]$v] }
            $cells += ($c.r + '=' + $v)
        }
        $L.Add('  ROW' + $rn + ': ' + ($cells -join ' | '))
    }
}

$zip.Dispose()
$L | Set-Content -Path $out -Encoding UTF8
Get-Content $out -Encoding UTF8
