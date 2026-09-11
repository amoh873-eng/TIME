# ============================================================
#  Start-AttendanceApi.ps1
#  ملف التشغيل يستخدمه اختصار سطح المكتب:
#   1) يبدأ خادم AttendanceApi على المنفذ 5000 (إن لم يكن يعمل)
#   2) ينتظر حتى يصبح جاهزاً
#   3) يفتح الصفحة في المتصفح الافتراضي
# ============================================================

param(
    [string]$Port = "5000",
    [string]$Url = "http://localhost:5000/"
)

$ErrorActionPreference = 'Stop'
$project = "d:\TIME\AttendanceApi"

# 1) إن لم يكن الخادم يستمع على المنفذ، شغّله في نافذة مستقلة
$listening = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if (-not $listening) {
    Start-Process -FilePath "dotnet" `
        -ArgumentList @("run", "--project", "`"$project`"", "--launch-profile", "http") `
        -WorkingDirectory $project `
        -WindowStyle Minimized
}

# 2) انتظار حتى يبدأ الاستماع (حتى 60 ثانية كحد أقصى)
$deadline = (Get-Date).AddSeconds(60)
while (-not (Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 400
}

# 3) فتح الصفحة في المتصفح الافتراضي
Start-Process $Url