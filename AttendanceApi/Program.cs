using AttendanceApi.Audit;
using AttendanceApi.Data;
using AttendanceApi.Services;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ---- Configuration ----
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddControllers();

// ---- EF Core ----
builder.Services.AddDbContext<AttendanceDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(300)));

// ---- AuditEngine options ----
var auditOptions = new AuditEngineOptions();
builder.Configuration.GetSection(AuditEngineOptions.SectionName).Bind(auditOptions);
builder.Services.AddSingleton(auditOptions);

// ---- PunchAnalysis options (تحليل بصمات الحضور) ----
var punchOptions = new PunchAnalysisOptions();
builder.Configuration.GetSection(PunchAnalysisOptions.SectionName).Bind(punchOptions);
builder.Services.AddSingleton(punchOptions);

// ---- محرك المراجعة: الطابور والحالة والخدمة الخلفية ----
var channelCapacity = auditOptions.ChannelCapacity;
builder.Services.AddSingleton(new AuditJobQueue(channelCapacity));
builder.Services.AddSingleton<AuditJobState>();
builder.Services.AddHostedService<AuditEngineHostedService>();

// ---- خدمات الأعمال ----
builder.Services.AddScoped<BulkUploadService>();
builder.Services.AddScoped<RolloverService>();
builder.Services.AddScoped<ExcelExportService>();

// ---- تهيئة قاعدة البيانات ومراجعة تقرير المغادرات ----
builder.Services.AddScoped<DatabaseInitializer>();
builder.Services.AddScoped<DeparturesReportService>();

// ---- تحليل بصمات الحضور والانصراف (تقرير الحضور الخام) ----
builder.Services.AddScoped<PunchReportService>();

// ---- صيانة البيانات: تنظيف الجداول لاستقبال بيانات جديدة (بيانات التشغيل أو مسح شامل) ----
builder.Services.AddScoped<DatabaseResetService>();


// ---- إعدادات الربط المباشر بقاعدة البيانات (حفظ الإعدادات + القراءة من المصدر) ----
builder.Services.AddSingleton<DirectSourceSettingsStore>();
builder.Services.AddScoped<DirectSourceService>();

// ---- إعدادات المادة 118/ج المرنة (الحدّ الأسبوعي، السقف، أيام الخصم) — قابلة للتغيير في وقت التشغيل ----
builder.Services.AddSingleton<PunchWeeklyRuleStore>();

WebApplication app = builder.Build();

// ---- تهيئة قاعدة البيانات عند الإقلاع (إنشاء المخطط + Views + الإجراءات المخزّنة) ----
using (var scope = app.Services.CreateScope())
{
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    try
    {
        await initializer.InitializeAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "تعذّرت تهيئة قاعدة البيانات — سيتم متابعة التشغيل.");
    }
}

// ---- Pipeline ----
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler();
}
app.MapOpenApi();

// ---- ملفات ثابتة: صفحة لوحة الاختبار في wwwroot ----
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

app.Run();