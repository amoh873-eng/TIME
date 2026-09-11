# ============================================================
#  verify_maintenance.ps1
#  التحقق من «زر تنظيف البيانات واستقبال بيانات جديدة»:
#   1) نقطة الحالة /api/v1/maintenance/data-status: الجداول وأعداد الصفوف وإجماليات النطاقين
#   2) الحماية: التنفيذ بلا كلمة التأكيد يُرفض (HTTP 400)
#   3) «معاينة بلا حذف» (dryRun): تُنفّذ نفس مسار الحذف ثم تُلغي المعاملة — والأعداد لا تتغيّر
#   4) الواجهات: شاشة «تنظيف البيانات» + عنصر القائمة + الزر في الشريط العلوي في punch.html و index.html
#   5) الملفات المشتركة: TIME.maintenance في shell.js وأنماط .dz في shell.css
#  التشغيل: powershell -ExecutionPolicy Bypass -File d:\TIME\data\verify_maintenance.ps1
#  ملاحظة: الفحوص الحيّة لا تحذف أي صف — التنفيذ الفعلي متروك للمشغّل من الواجهة.
# ============================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = 'd:\TIME\AttendanceApi\wwwroot'
$srv  = 'http://localhost:5000'
$fails = New-Object System.Collections.Generic.List[string]
$pass  = 0

function Check([string]$title, [bool]$ok, [string]$detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if ($ok) { $script:pass++ } else { $script:fails.Add($title) }
    Write-Host ("[{0}] {1} — {2}" -f $tag, $title, $detail)
}
function Detail([bool]$cond, [string]$text) { if ($cond) { " | " + $text } else { "" } }

# كلمة التأكيد تُبنى من رموز يونيكود لتجنّب أي تشويش ترميز في الطرفية
$confirmWord = [string]([char]0x062D) + [char]0x0630 + [char]0x0641   # حذف

function Post-Json([string]$url, [hashtable]$payload) {
    $json  = $payload | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    try {
        $r = Invoke-WebRequest $url -Method Post -ContentType 'application/json; charset=utf-8' -Body $bytes -UseBasicParsing
        return @{ ok = $true; status = [int]$r.StatusCode; body = $r.Content }
    } catch {
        $resp = $_.Exception.Response
        $code = if ($resp) { [int]$resp.StatusCode } else { 0 }
        return @{ ok = $false; status = $code; body = '' }
    }
}

# ---------- 1) الملفات المشتركة ----------
$js  = Get-Content (Join-Path $root 'assets\shell.js')  -Raw -Encoding UTF8
$css = Get-Content (Join-Path $root 'assets\shell.css') -Raw -Encoding UTF8

Check 'shell.js : دالة منطقة التنظيف TIME.maintenance موجودة ومصدَّرة' `
    ($js -match 'function maintenance\(cfg\)' -and $js -match 'maintenance: maintenance') 'function maintenance + export'
Check 'shell.js : التنظيف يمرّر النطاق والتأكيد إلى الواجهة الخلفية' `
    ($js -match 'api \+ "/reset"') 'POST /api/v1/maintenance/reset'
Check 'shell.js : زر «معاينة بلا حذف» يستخدم dryRun' `
    ($js -match 'dryRun=true' -and $js -match '"dzDry"') 'dryRun=true + dzDry'
Check 'shell.css : أنماط منطقة التنظيف (.dz-*) + الزر الخطر في الشريط العلوي' `
    ($css -match '\.dz-warn\{' -and $css -match '\.dz-table\{' -and $css -match '\.dz-opt\{' -and `
     $css -match '\.dz-run\{' -and $css -match '\.dz-done\{' -and $css -match '\.iconbtn\.danger\{') `
    'dz-warn + dz-table + dz-opt + dz-run + dz-done + iconbtn.danger'

# ---------- 2) الواجهات: شاشة التنظيف وعنصر القائمة والزر ----------
foreach ($page in @('punch.html', 'index.html')) {
    $html = Get-Content (Join-Path $root $page) -Raw -Encoding UTF8

    Check ($page + ' : شاشة «تنظيف البيانات» معرفة بمعرّفها وشاشتها') `
        ($html -match 'data-screen="cleanup"' -and $html -match 'id="cleanup"') 'data-screen="cleanup" + id="cleanup"'
    Check ($page + ' : عنصر القائمة اليمنى يشير إلى شاشة التنظيف') `
        ($html -match "id: 'cleanup'") "id: 'cleanup'"
    Check ($page + ' : زر تنظيف في الشريط العلوي (متاح من كل الشاشات)') `
        ($html -match 'id="tbCleanup"') 'id="tbCleanup"'
    Check ($page + ' : زر تنظيف في الواجهة الترحيبية (data-goto=cleanup)') `
        ($html -match 'data-goto="cleanup"') 'data-goto="cleanup"'
    Check ($page + ' : جدول ما سيُحذف + النطاق + التأكيد + التنفيذ + المعاينة') `
        (($html -match 'id="dzTbody"') -and ($html -match 'name="dzScope"') -and ($html -match 'id="dzConfirm"') -and `
         ($html -match 'id="dzRun"') -and ($html -match 'id="dzDry"') -and ($html -match 'id="dzResult"')) `
        'dzTbody + dzScope + dzConfirm + dzRun + dzDry + dzResult'
    Check ($page + ' : تهيئة TIME.maintenance على شاشة التنظيف') `
        ($html -match 'TIME\.maintenance\(\{') 'TIME.maintenance({ root: ''cleanup'' ... })'
    Check ($page + ' : شاشة التنظيف مربوطة بالتنقل (onScreen ← load)') `
        ($html -match "id === 'cleanup'.*maintenance\.load\(\)") "id === 'cleanup' → load()"
}

# ---------- 3) الخدمة الحيّة (بلا حذف فعلي) ----------
$listening = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    Write-Host '[SKIP] الخدمة غير مستمعة على المنفذ 5000 — تخطّي الفحوص الحيّة لنقاط النهاية'
} else {
    $r  = Invoke-WebRequest ($srv + '/api/v1/maintenance/data-status') -UseBasicParsing
    $st = $r.Content | ConvertFrom-Json

    Check 'GET /api/v1/maintenance/data-status : يستجيب 200 ويعيد حالة البيانات' `
        ($r.StatusCode -eq 200 -and $st.database) ("HTTP=" + $r.StatusCode + " | db=" + $st.database)

    $groupKeys = (@($st.groups | ForEach-Object { $_.key }) -join ',')
    Check 'الحالة: مجموعات الكتالوج (البصمات + الورديات + المغادرات + المرجعية)' `
        (($groupKeys -match 'punch') -and ($groupKeys -match 'shifts') -and ($groupKeys -match 'departures') -and ($groupKeys -match 'reference')) `
        ("المجموعات: " + $groupKeys)

    $sumData = 0; $cntData = 0; $sumAll = 0; $cntAll = 0
    foreach ($g in $st.groups) {
        foreach ($t in $g.tables) {
            $sumAll += [int64]$t.rows; $cntAll++
            if ($g.scope -eq 'data') { $sumData += [int64]$t.rows; $cntData++ }
        }
    }
    Check 'الحالة: إجماليات نطاق «data» مطابقة لجمع صفوف جداوله' `
        (($sumData -eq [int64]$st.dataRows) -and ($cntData -eq [int]$st.dataTables)) `
        ("الجدول=" + $st.dataTables + " جدولاً (" + $st.dataRows + " صفاً) | المحسوب=" + $cntData + " (" + $sumData + " صفاً)")
    Check 'الحالة: إجماليات نطاق «all» مطابقة لجمع صفوف كل المجموعات' `
        (($sumAll -eq [int64]$st.allRows) -and ($cntAll -eq [int]$st.allTables)) `
        ("all=" + $st.allTables + " جدولاً (" + $st.allRows + " صفاً) | المحسوب=" + $cntAll + " (" + $sumAll + " صفاً)")

    # 3-أ) بلا كلمة التأكيد: يجب أن يُرفض
    $bad = Post-Json ($srv + '/api/v1/maintenance/reset') @{ scope = 'data'; confirm = 'delete now'; resetWeeklyRule = $false }
    Check 'POST /api/v1/maintenance/reset : يرفض التنفيذ بكلمة تأكيد غير مطابقة (400)' `
        ($bad.status -eq 400) ("HTTP=" + $bad.status)

    $bad2 = Post-Json ($srv + '/api/v1/maintenance/reset') @{ scope = 'data'; resetWeeklyRule = $false }
    Check 'POST /api/v1/maintenance/reset : يرفض التنفيذ عند غياب حقل التأكيد (400)' `
        ($bad2.status -eq 400) ("HTTP=" + $bad2.status)

    # 3-ب) معاينة بلا حذف: نفس مسار التنظيف ثم إلغاء المعاملة
    foreach ($scope in @('data', 'all')) {
        $dry = Post-Json ($srv + '/api/v1/maintenance/reset?dryRun=true') @{ scope = $scope; confirm = $confirmWord; resetWeeklyRule = $false }
        $dj  = $null
        if ($dry.body) { try { $dj = $dry.body | ConvertFrom-Json } catch { $dj = $null } }

        Check ("معاينة بلا حذف (scope=" + $scope + ") : 200 وتُعلم أنها معاينة") `
            ($dry.status -eq 200 -and $dj -and ($dj.result.dryRun -eq $true)) `
            ("HTTP=" + $dry.status + " | dryRun=" + $(if ($dj) { $dj.result.dryRun } else { 'n/a' }))
        Check ("معاينة بلا حذف (scope=" + $scope + ") : تُعيد الجداول المشمولة وأعداد صفوفها") `
            ($dj -and ([int]$dj.result.tablesCleared -gt 0) -and ($dj.result.cleared.Count -eq [int]$dj.result.tablesCleared)) `
            ("الجداول=" + $(if ($dj) { $dj.result.tablesCleared } else { '0' }) + " | الصفوف=" + $(if ($dj) { $dj.result.rowsDeleted } else { '0' }))
        Check ("معاينة بلا حذف (scope=" + $scope + ") : التأكيد النصّي مُسجَّل في ملاحظات النتيجة") `
            ($dj -and (@($dj.result.notes) -match 'dry-run').Count -gt 0) 'ملاحظة المعاينة'

        $after = (Invoke-WebRequest ($srv + '/api/v1/maintenance/data-status') -UseBasicParsing).Content | ConvertFrom-Json
        Check ("معاينة بلا حذف (scope=" + $scope + ") : لم تتغيّر أعداد الصفوف في قاعدة البيانات") `
            (([int64]$after.allRows -eq [int64]$st.allRows) -and ([int64]$after.dataRows -eq [int64]$st.dataRows)) `
            ("all: قبل=" + $st.allRows + " | بعد=" + $after.allRows + " — data: قبل=" + $st.dataRows + " | بعد=" + $after.dataRows)
    }

    # 3-ج) شاشة التنظيف تُخدَم فعلياً في الصفحتين
    foreach ($u in @('/punch.html', '/index.html')) {
        $page = Invoke-WebRequest ($srv + $u) -UseBasicParsing
        $ok = ($page.StatusCode -eq 200) -and $page.Content.Contains('data-screen="cleanup"') -and $page.Content.Contains('id="dzRun"')
        Check ("الخدمة: " + $u + " يحتوي شاشة التنظيف وزر التنفيذ") $ok ("HTTP=" + $page.StatusCode + " | bytes=" + $page.RawContentLength)
    }
}

# ---------- 4) فحص DOM الفعلي لشاشة التنظيف (متصفح بلا واجهة) ----------
$chrome = @(
    'C:\Program Files\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($chrome -and $listening) {
    $out = Join-Path 'd:\TIME\data' '_dom_cleanup.html'
    $ud  = Join-Path $env:TEMP ('time-cleanup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $ud | Out-Null
    $cmd = '"' + $chrome + '" --headless=new --disable-gpu --no-sandbox --window-size=1400,1200 ' +
           '--virtual-time-budget=8000 --user-data-dir="' + $ud + '" --dump-dom "http://localhost:5000/punch.html#/cleanup" > "' + $out + '" 2>NUL'
    cmd /c $cmd | Out-Null

    if (Test-Path $out) {
        $dom  = Get-Content $out -Raw -Encoding UTF8
        $rows = [regex]::Matches($dom, 'class="tname"').Count
        $tags = [regex]::Matches($dom, 'dz-tag (del|keep)').Count
        $sum  = $dom -match 'id="dzSum">[^<]*<b>'
        Check 'متصفح: شاشة التنظيف تبني جدول الجداول من واجهة الصيانة (حتى بالرابط المباشر #/cleanup)' `
            ($rows -gt 0 -and $tags -eq $rows) ("صفوف الجدول=" + $rows + " | وسوم الإجراء=" + $tags)
        Check 'متصفح: ملخص «سيُفرَّغ … جدولاً/صفاً» مبني من أعداد قاعدة البيانات' $sum 'dzSum + أعداد الصفوف'
        Check 'متصفح: زر التنفيذ مقيَّد حتى كتابة كلمة التأكيد' `
            ($dom -match 'id="dzRun"[^>]*disabled') 'dzRun disabled'
    } else {
        Write-Host '[SKIP] تعذّر إنشاء لقطة DOM لشاشة التنظيف'
    }
} else {
    Write-Host '[SKIP] فحص DOM لشاشة التنظيف (يتطلب متصفحاً + الخدمة)'
}


# ---------- النتيجة ----------
Write-Host ''
Write-Host ("=" * 74)
Write-Host ("نتيجة التحقق من تنظيف البيانات: PASS={0} | FAIL={1}" -f $pass, $fails.Count)
if ($fails.Count -gt 0) {
    Write-Host 'الفحوص الفاشلة:'
    foreach ($f in $fails) { Write-Host ("  x " + $f) }
    exit 1
}
Write-Host 'كل فحوص تنظيف البيانات ناجحة'
exit 0

