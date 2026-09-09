# ⏱ Time — تطبيق ويب كامل بـ ASP.NET Core + Razor + Blazor

تطبيق إدارة مهام (To-Do) بواجهة عربية RTL، مبني على **ASP.NET Core (.NET 10)** ويجمع بين:

- **صفحات Razor** المخدومة من الخادم (`Components/Pages/*.razor`) لإخراج HTML سريع.
- **مكوّنات Blazor تفاعلية** (`@rendermode InteractiveServer`) لتحديث الواجهة دون إعادة تحميل الصفحة.
- **حفظ تلقائي** للمهام في ملف JSON محلي (`data/tasks.json`).

---

## المتطلبات

- **.NET SDK 10.0** أو أحدث — تحقق بالأمر:

```bash
dotnet --version
```

> إذا لم يكن مثبتاً، ثبّته عبر: `winget install Microsoft.DotNet.SDK.10`

---

## التشغيل

من داخل مجلد المشروع:

```bash
dotnet run
```

ثم افتح المتصفح على: **http://localhost:5268**

- صفحة المهام: **http://localhost:5268/tasks**
- التشغيل عبر HTTPS (شهادة تطوير محلية): `dotnet run --profile https`

---

## البناء والتحقق

```bash
dotnet build
```

---

## الميزات

- 📋 صفحة مهام كاملة: إضافة، تبديل حالة الإنجاز، حذف.
- 🧮 عدّادات فورية (الإجمالي / المكتملة / المتبقية).
- 🕐 مكوّن ساعة تفاعلي يعرض الوقت والتاريخ بالعربية.
- 💾 حفظ تلقائي في `data/tasks.json` وإعادة تحميل عند التشغيل.
- 🎨 تصميم Bootstrap متجاوب مع اتجاه RTL.

---

## بنية المشروع

```
time/
├── Program.cs                 # نقطة الدخول (WebApplication)
├── time.csproj                # ملف المشروع (.NET 10)
├── Components/
│   ├── App.razor              # مستند HTML الجذر (RTL / العربية)
│   ├── Routes.razor           # إعدادات الموجّه (Router)
│   ├── Clock.razor            # مكوّن Blazor تفاعلي: الساعة
│   ├── TaskRow.razor          # صف مهمة تفاعلي (معامل TodoItem)
│   ├── Layout/
│   │   ├── MainLayout.razor   # التخطيط الرئيسي
│   │   └── NavMenu.razor      # قائمة التنقل
│   └── Pages/
│       ├── Home.razor         # الصفحة الرئيسية
│       ├── Tasks.razor        # صفحة المهام (تفاعلية)
│       ├── Error.razor
│       └── NotFound.razor
├── Models/
│   └── TodoItem.cs            # سجلّ المهمة (record)
├── Services/
│   └── TaskRepository.cs      # مخزن ملفات JSON (قراءة/كتابة يدوية)
├── Properties/
│   └── launchSettings.json    # إعدادات التشغيل المحلي
└── wwwroot/                   # الملفات الثابتة (CSS بـootstrap…)
```

---

## أوامر مفيدة

| الأمر | الوصف |
| --- | --- |
| `dotnet run` | تشغيل التطبيق (HTTP) |
| `dotnet run --profile https` | تشغيل التطبيق (HTTPS) |
| `dotnet build` | بناء المشروع |
| `dotnet clean` | تنظيف مخرجات البناء |