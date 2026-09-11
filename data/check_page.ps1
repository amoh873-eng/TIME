$ErrorActionPreference = 'Stop'
function A([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }

# "al-maghaderat al-rasmeya"
$needle = (A @(0x0627,0x0644,0x0645,0x063A,0x0627,0x062F,0x0631,0x0627,0x062A,0x0020,0x0627,0x0644,0x0631,0x0633,0x0645,0x064A,0x0629))

curl.exe -s 'http://localhost:5000/' -o 'd:\TIME\data\page.html' | Out-Null
$html = [System.IO.File]::ReadAllText('d:\TIME\data\page.html', [System.Text.Encoding]::UTF8)

Write-Host ("page bytes      : {0}" -f $html.Length)
Write-Host ("contains needle : {0}" -f $html.Contains($needle))

$line = ($html -split "`n" | Where-Object { $_.Contains($needle) } | Select-Object -First 1)
if ($line) { Write-Host ("line            : {0}" -f $line.Trim()) }
