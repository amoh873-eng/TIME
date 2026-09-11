# ============================================================
#  verify_dashboard.ps1
#  التحقق من إعادة تصميم الواجهات كلوحة مركزية احترافية:
#   1) ملفات النظام المشتركة: assets/shell.css + assets/shell.js موجودة وتعمل محلياً
#   2) البنية: شريط تنقّل يمين (rail) + شريط علوي + غلاف + شاشة واحدة فقط لكل عنصر قائمة
#   3) لا تظهر أي شاشة إلا عند الضغط على عنصرها (صنف screen + data-screen + إخفاء .off)
#   4) المخططات الدائرية ثلاثية الأبعاد: لوحة canvas + مفتاح (legend) + تلميح لكل بطاقة
#   5) اتساق قائمة التنقّل مع الشاشات في punch.html و index.html (عنصر ↔ شاشة)
#   6) صحة صياغة JavaScript (node --check + استخراج كتل <script>)
#   7) الخدمة الحيّة: الصفحات والأصول ونقاط النهاية التي تغذّي اللوحة
#   8) شاشة «تنظيف البيانات» (زر + شاشة + واجهة الصيانة) — تفاصيلها الكاملة في verify_maintenance.ps1

#  التشغيل: powershell -ExecutionPolicy Bypass -File d:\TIME\data\verify_dashboard.ps1
# ============================================================

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = 'd:\TIME\AttendanceApi\wwwroot'
$dataDir = 'd:\TIME\data'
$srv  = 'http://localhost:5000'
$fails = New-Object System.Collections.Generic.List[string]
$pass  = 0

function Check([string]$title, [bool]$ok, [string]$detail) {
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    if ($ok) { $script:pass++ } else { $script:fails.Add($title) }
    Write-Host ("[{0}] {1} — {2}" -f $tag, $title, $detail)
}

function Detail([bool]$cond, [string]$text) { if ($cond) { " | " + $text } else { "" } }

# ---------- 1) ملفات النظام المشتركة ----------
$cssPath = Join-Path $root 'assets\shell.css'
$jsPath  = Join-Path $root 'assets\shell.js'
Check 'ملف نظام التصميم assets/shell.css موجود' (Test-Path $cssPath) $cssPath
Check 'محرّك اللوحة assets/shell.js موجود' (Test-Path $jsPath) $jsPath

$css = Get-Content $cssPath -Raw -Encoding UTF8
$js  = Get-Content $jsPath -Raw -Encoding UTF8
Check 'CSS: الشريط الجانبي على اليمين (RTL)' ($css -match '\.rail\{' -and $css -match 'inset-inline-start:0') 'position:fixed; inset-inline-start:0'
Check 'CSS: آلية إخفاء الشاشات غير النشطة' ($css -match '\.screen\.off\{display:none') '.screen.off{display:none!important}'
Check 'CSS: مكوّنات اللوحة (Hero/KPI/مخطط/مفتاح/لوحة الأوامر)' `
    ($css -match '\.hero\{' -and $css -match '\.kpi\{' -and $css -match '\.chartcard' -and `
     $css -match '\.legend' -and $css -match '\.stage' -and $css -match '\.palette') 'hero + kpi + chartcard + legend + stage + palette'
Check 'CSS: استجابة الجوّال (درج جانبي)' ($css -match '@media \(max-width:980px\)' -and $css -match 'translateX\(102%\)') 'drawer'
Check 'JS: محرّك الشاشات + التنقّل + لوحة الأوامر' `
    ($js -match 'function shell\(' -and $js -match 'classList\.toggle\("off"' -and $js -match 'Ctrl' -and $js -match 'localStorage') 'shell() + off + Ctrl+K + طيّ محفوظ'
Check 'JS: المخطط ثلاثي الأبعاد (عمق + ظل + لمعة + فتحة وسطية)' `
    ($js -match 'function paint\(' -and $js -match 'depth' -and $js -match 'createRadialGradient' -and `
     $js -match 'destination-out' -and $js -match 'ctx\.ellipse\(') 'extrusion + shadow + glass + donut hole'
Check 'JS: ربط المخطط بلا تكرار مستمعي الأحداث' ($js -match 'if \(r\.canvas === cv\) rec = r;') 'idempotent chart()'
Check 'JS: إعادة الرسم عند تغيير الشاشة أو حجم النافذة' `
    ($js -match 'function refreshAll\(' -and $js -match 'time:screen' -and $js -match 'addEventListener\("resize"') 'refreshAll + resize'

# ---------- 2) فحص بنية كل صفحة ----------
function Test-Page([string]$path, [string]$name, [string[]]$noteIds) {
    $exists = Test-Path $path
    Check "$name : ملف الصفحة موجود" $exists $path
    if (-not $exists) { return }

    $html = Get-Content $path -Raw -Encoding UTF8

    Check "$name : شريط التنقّل على اليمين + درج الجوّال" `
        ($html -match '<aside class="rail" id="rail"' -and $html -match 'id="scrim"') 'rail + scrim'
    Check "$name : غلاف الصفحة والشريط العلوي" `
        ($html -match 'class="shell-body"' -and $html -match '<main class="shell-main">' -and `
         $html -match 'id="tbTitle"' -and $html -match 'id="crumb"' -and $html -match 'id="clock"') 'shell-body + topbar + crumb + clock'
    Check "$name : تحميل نظام التصميم والمحرّك محلياً" `
        ($html -match '/assets/shell\.css' -and $html -match '/assets/shell\.js') 'shell.css + shell.js'
    Check "$name : بلا أي مرجع خارجي (يعمل دون إنترنت)" `
        (-not ($html -match '(src|href)="https?://')) 'لا CDN خارجي'

    # كل عناصر [data-screen] يجب أن تحمل صنف screen حتى يخفيها المحرّك عند عدم التنشيط
    $withClass = [regex]::Matches($html, 'class="[^"]*\bscreen\b[^"]*"\s+data-screen="([^"]+)"')
    $allScreens = [regex]::Matches($html, 'data-screen="([^"]+)"')
    $scrIds = @($withClass | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    Check "$name : كل عناصر الشاشات تحمل صنف screen" `
        (($withClass.Count -eq $allScreens.Count) -and ($allScreens.Count -gt 0)) `
        ("شاشات=$($withClass.Count) | data-screen=$($allScreens.Count) | متطابقة=" + ($withClass.Count -eq $allScreens.Count))
    Check "$name : شاشة رئيسية واحدة فقط (home)" `
        ((@($scrIds | Where-Object { $_ -eq 'home' })).Count -eq 1) ("الشاشات: " + ($scrIds -join ', '))

    # عناصر القائمة اليمنى (تجاهل الروابط الخارجية وعناصر الأوامر)
    $menuIds = New-Object System.Collections.Generic.List[string]
    foreach ($m in [regex]::Matches($html, "\{\s*id:\s*'([^']+)'[^\}]*\}")) {
        $obj = $m.Value
        if ($obj -match 'href:' -or $obj -match 'act:') { continue }
        $menuIds.Add([regex]::Match($obj, "id:\s*'([^']+)'").Groups[1].Value)
    }
    $menu = @($menuIds | Sort-Object -Unique)
    $diff = Compare-Object $menu $scrIds
    $diffTxt = if ($diff) { " | فرق=" + (($diff | ForEach-Object { $_.SideIndicator + $_.InputObject }) -join ' ') } else { '' }
    Check "$name : كل عنصر قائمة يقابل شاشة موجودة والعكس" ($null -eq $diff) `
        ("عناصر=$($menu.Count) | شاشات=$($scrIds.Count)$diffTxt")

    # بطاقات المخططات: canvas + مفتاح + تلميح (تُستثنى لوحة الالتزام القديمة #pie ولها مرسِمها الخاص)
    $cards = [regex]::Matches($html, 'class="card chartcard"')
    $canvases = @([regex]::Matches($html, '<canvas id="([^"]+)"') | ForEach-Object { $_.Groups[1].Value })
    $dashCanvases = @($canvases | Where-Object { $_ -ne 'pie' })
    $legends = [regex]::Matches($html, 'data-legend').Count
    $tips = [regex]::Matches($html, 'class="tipbox"').Count
    Check "$name : بطاقة لكل مخطط (canvas + مفتاح + تلميح)" `
        (($cards.Count -eq $dashCanvases.Count) -and ($cards.Count -eq $legends) -and ($cards.Count -eq $tips) -and $cards.Count -gt 0) `
        ("بطاقات=$($cards.Count) | لوحات=$($dashCanvases.Count) | مفاتيح=$legends | تلميحات=$tips")
    Check "$name : كل لوحة رسم مرتبطة بمخطط ثلاثي الأبعاد" `
        ($dashCanvases.Count -gt 0 -and (@($dashCanvases | Where-Object { $html -notmatch ("TIME\.chart\('#" + [regex]::Escape($_) + "'") })).Count -eq 0) `
        ("اللوحات: " + ($dashCanvases -join ', '))

    # معرّفات اللوحة المركزية التي تكتبها دالة الرسم
    $ids = @('dashKpis', 'heroMeta', 'dashBars') + $noteIds
    $missing = @($ids | Where-Object { $html -notmatch ('id="' + [regex]::Escape($_) + '"') })
    Check "$name : معرّفات اللوحة المركزية موجودة في الصفحة" ($missing.Count -eq 0) `
        ("معرّفات=$($ids.Count)" + (Detail ($missing.Count -gt 0) ("ناقص=" + ($missing -join ','))))

    # عناصر التنقّل عبر الأزرار في الصفحة (data-goto)
    Check "$name : أزرار التنقّل بين الشاشات (data-goto)" `
        ([regex]::Matches($html, 'data-goto="').Count -gt 0) `
        ("أزرار data-goto=" + [regex]::Matches($html, '<button[^>]*data-goto="').Count)
}

Test-Page (Join-Path $root 'punch.html') 'punch.html' @('daysNote', 'overtimeNote', 'complianceNote')
Test-Page (Join-Path $root 'index.html') 'index.html' @('typesNote', 'empNote')

# ---------- 3) توافق الشاشات مع واجهات البرمجة التي تغذّيها ----------
$punch = Get-Content (Join-Path $root 'punch.html') -Raw -Encoding UTF8
$index = Get-Content (Join-Path $root 'index.html') -Raw -Encoding UTF8
Check 'punch.html : اللوحة تستخدم ملخص التحليل /summary' `
    ($punch.Contains('fetch(`${API}/summary`)') -and $punch.Contains('renderDashboard(s)')) 'renderDashboard ← renderKpis'
Check 'punch.html : المخططات الثلاثة (أيام / إضافي / التزام)' `
    ($punch -match 'dashDaySlices' -and $punch -match 'dashOvertimeSlices' -and $punch -match 'dashComplianceSlices' -and `
     $punch -match 'overtimeNeedsApprovalMinutes') 'يومي + إضافي + التزام'
Check 'punch.html : شارات القائمة من الملخص (مخالفات/معلَّق/118-ج)' `
    ($punch -match 'setBadge\(.violations' -and $punch -match 'setBadge\(.workTimeSection' -and $punch -match 'setBadge\(.weeklyRuleSection') 'badges'
Check 'index.html : لوحة المغادرات (أنواع الطلبات + حالة الموظفين)' `
    ($index -match 'dashTypeSlices' -and $index -match 'dashEmpSlices' -and $index -match 'renderDash\(imp, rev\)') 'types + employees'
Check 'index.html : الانتقال لشاشة النتيجة بعد المعالجة' `
    ($index -match 'shell\.go\("result"\)') 'auto-navigate'
Check 'punch.html : شاشة تنظيف البيانات (شاشة + زر تنفيذ + تهيئة TIME.maintenance)' `
    ($punch -match 'data-screen="cleanup"' -and $punch -match 'id="dzRun"' -and $punch -match 'TIME\.maintenance\(\{') 'cleanup + dzRun + TIME.maintenance'
Check 'index.html : شاشة تنظيف البيانات (شاشة + زر تنفيذ + تهيئة TIME.maintenance)' `
    ($index -match 'data-screen="cleanup"' -and $index -match 'id="dzRun"' -and $index -match 'TIME\.maintenance\(\{') 'cleanup + dzRun + TIME.maintenance'

# ---------- 4) صحة صياغة JavaScript ----------
$mjs = 'd:\TIME\data\check-html-js.mjs'
if (Get-Command node -ErrorAction SilentlyContinue) {
    & node --check $jsPath 2>&1 | Out-Null
    Check 'shell.js : صياغة JavaScript سليمة (node --check)' ($LASTEXITCODE -eq 0) 'node --check'
    if (Test-Path $mjs) {
        $out = (& node $mjs (Join-Path $root 'punch.html') (Join-Path $root 'index.html') 2>&1 | Out-String)
        $last = ($out.Trim() -split "`r?`n" | Where-Object { $_ } | Select-Object -Last 1)
        Check 'الكتل البرمجية داخل الصفحتين سليمة' ($out -match 'ALL SCRIPTS OK') $last
    }
} else {
    Write-Host '[SKIP] node غير متاح — تخطّي فحص الصياغة'
}

# ---------- 5) الخدمة الحيّة ----------
$listening = Get-NetTCPConnection -LocalPort 5000 -State Listen -ErrorAction SilentlyContinue
if ($listening) {
    $markers = @{
        '/punch.html'      = @('id="rail"', 'assets/shell.js', 'id="chartDays"', 'id="chartOvertime"', 'id="chartCompliance"', 'data-screen="cleanup"')
        '/index.html'      = @('id="rail"', 'assets/shell.js', 'id="chartTypes"', 'id="chartEmp"', 'data-screen="cleanup"')
        '/assets/shell.js' = @('TIME', 'function shell(', 'function paint(')
        '/assets/shell.css'= @('.rail{', '.screen.off', '.chartcard')
    }
    foreach ($u in $markers.Keys) {
        try {
            $r = Invoke-WebRequest ($srv + $u) -UseBasicParsing
            $miss = @($markers[$u] | Where-Object { -not $r.Content.Contains($_) })
            Check "الخدمة: $u" (($r.StatusCode -eq 200) -and ($miss.Count -eq 0) -and ($r.RawContentLength -gt 0)) `
                ("HTTP=$($r.StatusCode) | bytes=$($r.RawContentLength)" + (Detail ($miss.Count -gt 0) ("ناقص=" + ($miss -join ','))))
        } catch {
            Check "الخدمة: $u" $false $_.Exception.Message
        }
    }
    foreach ($api in @('/api/v1/punch/summary', '/api/v1/punch/staging-summary',
                       '/api/v1/maintenance/data-status',
                       '/api/v1/departures/review-summary',
                       '/api/v1/departures/compliance-by-administration?byMainDepartment=true&minEmployees=3')) {
        try {
            $r = Invoke-WebRequest ($srv + $api) -UseBasicParsing
            Check "واجهة تغذّي اللوحة: $api" ($r.StatusCode -eq 200) ("HTTP=$($r.StatusCode) | bytes=$($r.RawContentLength)")
        } catch {
            Check "واجهة تغذّي اللوحة: $api" $false $_.Exception.Message
        }
    }
} else {
    Write-Host '[SKIP] الخدمة غير مستمعة على المنفذ 5000 — تخطّي فحوص الخدمة الحيّة'
}

# ---------- 6) اختبار البكسل للمخططات ثلاثية الأبعاد (متصفح بلا واجهة + canvas) ----------
$chrome = @(
    'C:\Program Files\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',
    'C:\Program Files\Microsoft\Edge\Application\msedge.exe'
) | Where-Object { Test-Path $_ } | Select-Object -First 1
$selfTest = Join-Path $root '_selftest3d.html'
$selOut = Join-Path $dataDir '_dom_selftest.html'
if ($chrome -and (Test-Path $selfTest) -and $listening -and (Get-Command node -ErrorAction SilentlyContinue)) {
    $ud = Join-Path $env:TEMP ('time-selftest-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $ud | Out-Null
    $cmd = '"' + $chrome + '" --headless=new --disable-gpu --no-sandbox --window-size=1400,1200 ' +
           '--virtual-time-budget=5000 --user-data-dir="' + $ud + '" --dump-dom ' +
           'http://localhost:5000/_selftest3d.html > "' + $selOut + '" 2>NUL'
    cmd /c $cmd | Out-Null
    if (Test-Path $selOut) {
        $res = (& node 'd:\TIME\data\check-selftest.mjs' $selOut 2>&1 | Out-String)
        foreach ($line in ($res -split "`r?`n" | Where-Object { $_ -match '^\[' })) {
            $okLine = $line.StartsWith('[PASS]')
            Check ('محرّك ثلاثي الأبعاد: ' + ($line -replace '^\[(PASS|FAIL)\]\s*', '').Split('—')[0].Trim()) `
                $okLine ($line -replace '^\[(PASS|FAIL)\]\s*', '')
        }
    } else {
        Write-Host '[SKIP] تعذّر إنشاء لقطة DOM للاختبار الذاتي'
    }
} else {
    Write-Host '[SKIP] اختبار البكسل ثلاثي الأبعاد (يتطلب متصفحاً + الخدمة + node)'
}

# ---------- 7) فحص DOM بعد التنفيذ الفعلي في متصفح بلا واجهة (شاشة واحدة + قائمة + مخططات) ----------
$renderJs = 'd:\TIME\data\check-render.mjs'
if ($chrome -and $listening -and (Test-Path $renderJs) -and (Get-Command node -ErrorAction SilentlyContinue)) {
    $pages = @(
        @{ key = 'punch'; url = 'http://localhost:5000/punch.html'; out = (Join-Path $dataDir '_dom_punch.html') },
        @{ key = 'index'; url = 'http://localhost:5000/index.html#/home'; out = (Join-Path $dataDir '_dom_index.html') }
    )
    foreach ($p in $pages) {
        $ud = Join-Path $env:TEMP ('time-render-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $ud | Out-Null
        $cmd = '"' + $chrome + '" --headless=new --disable-gpu --no-sandbox --hide-scrollbars ' +
               '--window-size=1440,1200 --virtual-time-budget=7000 --user-data-dir="' + $ud +
               '" --dump-dom ' + $p.url + ' > "' + $p.out + '" 2>NUL'
        cmd /c $cmd | Out-Null
        if (Test-Path $p.out) {
            $res = (& node $renderJs $p.out $p.key 2>&1 | Out-String)
            foreach ($line in ($res -split "`r?`n" | Where-Object { $_ -match '^\[' })) {
                $txt = $line -replace '^\[(PASS|FAIL)\]\s*', ''
                Check ($p.key + '.html (متصفح): ' + $txt.Split('—')[0].Trim()) $line.StartsWith('[PASS]') $txt
            }
        } else {
            Write-Host ("[SKIP] تعذّر إنشاء لقطة DOM لـ " + $p.key)
        }
    }
} else {
    Write-Host '[SKIP] فحص DOM الفعلي (يتطلب متصفحاً + الخدمة + node)'
}

# ---------- 8) الروابط المباشرة (deep links) تفتح الشاشة المطلوبة ولا يُلغيها التحميل التلقائي ----------
$linkJs = 'd:\TIME\data\check-active.mjs'
if ($chrome -and $listening -and (Test-Path $linkJs) -and (Get-Command node -ErrorAction SilentlyContinue)) {
    $links = @(
        @{ url = 'http://localhost:5000/punch.html#reportsSection'; id = 'reportsSection'; out = '_dom_link1.html' },
        @{ url = 'http://localhost:5000/punch.html#workTimeSection'; id = 'workTimeSection'; out = '_dom_link2.html' },
        @{ url = 'http://localhost:5000/index.html#/chart'; id = 'chart'; out = '_dom_link3.html' },
        @{ url = 'http://localhost:5000/index.html'; id = 'home'; out = '_dom_link4.html' },
        @{ url = 'http://localhost:5000/punch.html#/cleanup'; id = 'cleanup'; out = '_dom_link5.html' }
    )
    foreach ($l in $links) {
        $out = Join-Path $dataDir $l.out
        $ud = Join-Path $env:TEMP ('time-link-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
        New-Item -ItemType Directory -Force -Path $ud | Out-Null
        $cmd = '"' + $chrome + '" --headless=new --disable-gpu --no-sandbox --window-size=1400,1000 ' +
               '--virtual-time-budget=6000 --user-data-dir="' + $ud + '" --dump-dom "' + $l.url + '" > "' + $out + '" 2>NUL'
        cmd /c $cmd | Out-Null
        $res = ''
        if (Test-Path $out) { $res = (& node $linkJs $out $l.id 2>&1 | Out-String) }
        Check ("رابط مباشر: " + $l.url) ($res -match 'RESULT=PASS') `
            (($res -split "`r?`n" | Where-Object { $_ -match 'ACTIVE_ID=' } | Select-Object -First 1).Trim())
    }
} else {
    Write-Host '[SKIP] فحص الروابط المباشرة (يتطلب متصفحاً + الخدمة + node)'
}

# ---------- النتيجة ----------
Write-Host ''
Write-Host ("=" * 74)
Write-Host ("النتيجة النهائية لإعادة تصميم اللوحة المركزية: PASS={0} | FAIL={1}" -f $pass, $fails.Count)
if ($fails.Count -gt 0) {
    Write-Host 'الفحوص الفاشلة:'
    foreach ($f in $fails) { Write-Host ("  ✗ " + $f) }
    exit 1
}
Write-Host 'كل فحوص اللوحة المركزية ناجحة ✅'
exit 0
