# ============================================================
#  verify_approval_rule.ps1
#  التحقق من قاعدة «لا يُحتسب أي عمل إضافي بلا موافقة مسبقة»:
#   1) إعدادات التحليل: overtime.requireApproval = true (الافتراضي)
#   2) إعادة التحليل بلا تصاريح ⇒ صفر إضافي محتسب + دقائق معلَّقة بانتظار تصريح
#   3) واجهات API: daily.overtimeNeedsApproval و monthly.overtimeNeedsApprovalDays/Minutes
#   4) تصدير Excel: عمود «حالة الموافقة المسبقة» + عمود «أيام معلَّقة بانتظار تصريح»
#      + سطر «دقائق معلَّقة بانتظار موافقة مسبقة» + 17 ورقة في التقرير الشامل
#   5) واجهة /punch.html: خيار الاشتراط + مؤشر «معلَّق بانتظار تصريح إضافي»
#  التشغيل: powershell -ExecutionPolicy Bypass -File d:\TIME\data\verify_approval_rule.ps1
# ============================================================

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$base = 'http://localhost:5000/api/v1/punch'
$dataDir = 'd:\TIME\data'
$fails = New-Object System.Collections.Generic.List[string]

function Check([string]$title, [bool]$ok, [string]$detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if (-not $ok) { $script:fails.Add($title) }
    Write-Host ("[{0}] {1} — {2}" -f $tag, $title, $detail)
}

# ---------- 1) الإعدادات ----------
$settings = Invoke-RestMethod "$base/analysis-settings"
$ot = $settings.rule.overtime
Check 'إعدادات: اشتراط الموافقة المسبقة للعمل الإضافي' ([bool]$ot.requireApproval) `
    "requireApproval=$($ot.requireApproval) | isScoped=$($ot.isScoped) | إدارات/أرقام=$($ot.departments.Count)/$($ot.employees.Count)"

# ---------- 2) التحليل ----------
$run = Invoke-RestMethod -Uri "$base/analyze" -Method Post -ContentType 'application/json' -Body '{}'
$s = $run.summary
$noApprovals = ((Invoke-RestMethod "$base/work-approvals").total -eq 0)
if ($noApprovals) {
    Check 'التحليل بلا تصاريح: الإضافي المحتسب = صفر' ([int]$s.overtimeMinutes -eq 0) `
        "overtimeMinutes=$($s.overtimeMinutes) | أيام=$($s.overtimeDays) | موظفون=$($s.overtimeEmployees)"
} else {
    Write-Host '[SKIP] توجد تصاريح مسجّلة ⇒ تخطّي التحقق من الصفر'
}
Check 'التحليل: تسجيل الدقائق المعلَّقة بانتظار تصريح' ([long]$s.overtimeNeedsApprovalMinutes -gt 0) `
    "أيام معلَّقة=$($s.overtimeNeedsApprovalDays) | دقائق=$($s.overtimeNeedsApprovalMinutes) | خارج الدوام=$($s.overtimeRawMinutes)"
Check 'التحليل: المستبعد = الدقائق خارج الدوام (كلها بلا موافقة)' `
    ([long]$s.overtimeExcludedMinutes -eq [long]$s.overtimeRawMinutes) `
    "excluded=$($s.overtimeExcludedMinutes) | raw=$($s.overtimeRawMinutes)"

# ---------- 3) واجهات API ----------
$day = (Invoke-RestMethod "$base/daily?pageSize=1").items[0]
Check 'API اليومي: حقل overtimeNeedsApproval' `
    (($day | Get-Member -MemberType NoteProperty -Name overtimeNeedsApproval) -ne $null) `
    "overtimeNeedsApproval=$($day.overtimeNeedsApproval) | overtimeMinutes=$($day.overtimeMinutes)"

$monthly = (Invoke-RestMethod "$base/monthly?pageSize=5").items[0]
Check 'API الشهري: حقول OvertimeNeedsApprovalDays/Minutes' `
    ((($monthly | Get-Member -MemberType NoteProperty -Name overtimeNeedsApprovalDays) -ne $null) -and
     (($monthly | Get-Member -MemberType NoteProperty -Name overtimeNeedsApprovalMinutes) -ne $null)) `
    "أيام=$($monthly.overtimeNeedsApprovalDays) | دقائق=$($monthly.overtimeNeedsApprovalMinutes)"

$tops = (Invoke-RestMethod "$base/monthly?pageSize=3000").items |
    Sort-Object overtimeNeedsApprovalMinutes -Descending | Select-Object -First 1
Check 'API الشهري: أعلى موظف معلَّق بانتظار تصريح' ([int]$tops.overtimeNeedsApprovalMinutes -gt 0) `
    "الموظف=$($tops.jobNumber) | $($tops.overtimeNeedsApprovalDays) يوماً | $($tops.overtimeNeedsApprovalMinutes) دقيقة"

$preview = Invoke-RestMethod "$base/reports/overtime-flexible/preview?maxRows=5"
Check 'تقرير overtime-flexible: عمود «حالة الموافقة المسبقة»' ($preview.columns -contains 'حالة الموافقة المسبقة') `
    "الأعمدة=$($preview.columns.Count) | وصف المعلَّق في الترويسة=$(($preview.subtitle -match 'معلَّق بانتظار تصريح مسبق'))"


# ---------- 4) ملفات Excel ----------
function Save-File([string]$url, [string]$path) {
    if (Test-Path $path) { Remove-Item $path -Force }
    curl.exe -s $url -o $path | Out-Null
    return (Test-Path $path) -and ((Get-Item $path).Length -gt 4000)
}

function Read-Sheets([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    $read = {
        param($name)
        $e = $zip.GetEntry($name)
        if (-not $e) { return $null }
        $sr = New-Object System.IO.StreamReader($e.Open(), [System.Text.Encoding]::UTF8)
        $t = $sr.ReadToEnd(); $sr.Close(); return $t
    }

    $wb = [xml](& $read 'xl/workbook.xml')
    $result = @{
        SheetCount = $wb.workbook.sheets.sheet.Count
        Texts      = @{}
        MaxCells   = @{}
    }

    $strings = New-Object System.Collections.Generic.List[string]
    $ssXml = & $read 'xl/sharedStrings.xml'
    if ($ssXml) {
        $x = [xml]$ssXml
        foreach ($si in $x.sst.si) {
            $strings.Add((($si.SelectNodes(".//*[local-name()='t']") | ForEach-Object { $_.InnerText }) -join ''))
        }
    }

    foreach ($sf in ($zip.Entries | Where-Object { $_.FullName -like 'xl/worksheets/sheet*.xml' } |
            ForEach-Object { $_.FullName } | Sort-Object)) {
        $xml = [xml](& $read $sf)
        $own = New-Object System.Collections.Generic.List[string]
        $maxCells = 0
        foreach ($row in $xml.worksheet.sheetData.row) {
            $n = 0
            foreach ($c in $row.c) {
                $n++
                $v = $c.v
                if ($c.t -eq 's' -and $v -ne $null) { $v = $strings[[int]$v] }
                if ($v) { $own.Add([string]$v) }
            }
            if ($n -gt $maxCells) { $maxCells = $n }
        }
        $result.Texts[$sf] = $own
        $result.MaxCells[$sf] = $maxCells
    }

    $zip.Dispose()
    return $result
}

function Sheet-With($doc, [string]$needle) {
    return ($doc.Texts.Keys | Where-Object { ($doc.Texts[$_] -join '~').Contains($needle) } |
        Select-Object -First 1)
}

$otFile = Join-Path $dataDir 'verify_ot_export.xlsx'
if (Save-File "$base/reports/overtime-flexible/export?maxRows=300" $otFile) {
    $otDoc = Read-Sheets $otFile
    $all = ($otDoc.Texts.Values | ForEach-Object { $_ }) -join '~'
    Check 'تصدير overtime-flexible: عمود «حالة الموافقة المسبقة»' $all.Contains('حالة الموافقة المسبقة') `
        "الملف=$((Get-Item $otFile).Length) بايت"
    Check 'تصدير overtime-flexible: وسم «معلَّق بانتظار تصريح مسبق» في الصفوف' $all.Contains('معلَّق بانتظار تصريح مسبق') `
        'يظهر لمن ليس لديه تصريح ساري'
} else {
    Check 'تصدير overtime-flexible' $false 'لم يُنزَّل الملف'
}

$fullFile = Join-Path $dataDir 'verify_full_legal.xlsx'
if (Save-File "$base/reports/full-legal/export?maxRows=120" $fullFile) {
    $fullDoc = Read-Sheets $fullFile
    $all = ($fullDoc.Texts.Values | ForEach-Object { $_ }) -join '~'
    Check 'التقرير الشامل: 17 ورقة' ([int]$fullDoc.SheetCount -eq 17) "عدد الأوراق=$($fullDoc.SheetCount)"
    Check 'التقرير الشامل: ورقة العمل الإضافي والدوام المرن محدَّثة' $all.Contains('أيام معلَّقة بانتظار تصريح') `
        "الملف=$((Get-Item $fullFile).Length) بايت"
    $wsFile = Sheet-With $fullDoc 'أيام معلَّقة بانتظار تصريح'
    if ($wsFile) {
        Check 'ورقة الشهرية: عدد أعمدة الترويسة = 17' ([int]$fullDoc.MaxCells[$wsFile] -eq 17) `
            "الورقة=$wsFile | أوسع صف=$($fullDoc.MaxCells[$wsFile]) عموداً"
    } else {
        Check 'ورقة الشهرية: عدد أعمدة الترويسة = 17' $false 'لم تُعثر ورقة العمل الإضافي والدوام المرن'
    }
    Check 'الملخص التنفيذي: سطر «دقائق معلَّقة بانتظار موافقة مسبقة»' $all.Contains('دقائق معلَّقة بانتظار موافقة مسبقة') `
        'يقرأ OvertimeNeedsApprovalMinutes من ملخص التحليل'
} else {
    Check 'تصدير التقرير الشامل' $false 'لم يُنزَّل الملف'
}

# ---------- 5) الواجهة ----------
$htmlPath = Join-Path $dataDir '_verify_punch.html'
curl.exe -s 'http://localhost:5000/punch.html' -o $htmlPath | Out-Null
$html = [System.IO.File]::ReadAllText($htmlPath, [System.Text.Encoding]::UTF8)
Check 'الواجهة: خيار «اشتراط موافقة مسبقة» (القسم 9)' $html.Contains('اشتراط موافقة مسبقة') 'مُفعَّل افتراضياً'
Check 'الواجهة: بطاقة «اشتراط الموافقة المسبقة»' $html.Contains('اشتراط الموافقة المسبقة') 'renderWorkTimeSummary'
Check 'الواجهة: مؤشر «معلَّق بانتظار تصريح إضافي»' $html.Contains('overtimeNeedsApprovalMinutes') `
    'بطاقات الملخص التنفيذي'

# ---------- الخلاصة ----------
Write-Host ''
if ($fails.Count -eq 0) {
    Write-Host 'النتيجة: كل الفحوص نجحت ✅ — لا يُحتسب عمل إضافي بلا موافقة مسبقة.'
} else {
    Write-Host ("النتيجة: فشل {0} فحصاً ❌: {1}" -f $fails.Count, ($fails -join ' | '))
}
