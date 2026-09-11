/* تحقّق من DOM بعد التنفيذ الفعلي في متصفح بلا واجهة (Chrome --headless --dump-dom)
   الاستخدام: node data/check-render.mjs <dumped.html> <page>        */
import fs from "node:fs";

const [file, page] = process.argv.slice(2);
const html = fs.readFileSync(file, "utf8");
let pass = 0, fail = 0;

function check(title, ok, detail) {
  console.log(`[${ok ? "PASS" : "FAIL"}] ${title} — ${detail}`);
  ok ? pass++ : fail++;
}
const count = re => (html.match(re) || []).length;

const expected = {
  punch: {
    railItems: 13, screens: 11, title: "لوحة المؤشرات",
    canvases: ["chartDays", "chartOvertime", "chartCompliance"],
    kpiMarker: "دقائق العمل الإضافي المحتسبة"
  },
  index: {
    railItems: 9, screens: 6, title: "لوحة المؤشرات",
    canvases: ["chartTypes", "chartEmp"],
    kpiMarker: "الطلبات المعالجة"
  }
}[page];

check("صفحة DOM مُنفَّذة غير فارغة", html.length > 5000, `${html.length} bytes`);

const items = count(/class="rail-item/g);
check("رُسمت القائمة اليمنى بكل عناصرها", items === expected.railItems, `عناصر=${items} (متوقع ${expected.railItems})`);

const active = count(/class="rail-item active"/g);
check("عنصر قائمة نشط واحد فقط", active === 1, `نشط=${active}`);

const off = count(/class="[^"]*\bscreen\b[^"]*\boff\b/g);
check("شاشة واحدة ظاهرة والبقية مخفية", off === expected.screens - 1, `مخفية=${off} (متوقع ${expected.screens - 1})`);

const canSized = expected.canvases.filter(id => {
  const m = html.match(new RegExp(`<canvas[^>]*id="${id}"[^>]*>`));
  if (!m) return false;
  const w = m[0].match(/width="(\d+)"/), h = m[0].match(/height="(\d+)"/);
  return !!w && !!h && Number(w[1]) >= 280 && Number(h[1]) >= 200;
});
check("رُسمت المخططات ثلاثية الأبعاد على كل لوحة (أبعاد فعلية كافية)", canSized.length === expected.canvases.length,
  "الأبعاد=" + expected.canvases.map(id => {
    const m = html.match(new RegExp(`<canvas[^>]*id="${id}"[^>]*>`));
    const w = m && m[0].match(/width="(\d+)"/);
    return id + ":" + (w ? w[1] : "?") + "px";
  }).join(" | "));

const rows = count(/class="lg-row"/g);
check("مفاتيح المخططات (legend) مبنية من البيانات", rows >= 2, `صفوف المفتاح=${rows}`);

check("بطاقة KPI للوحة مبنية من بيانات الخدمة", html.includes(expected.kpiMarker), expected.kpiMarker);

/* عنوان الشريط العلوي يجب أن يطابق عنصر القائمة النشط (تنتقل الصفحة تلقائياً للنتيجة عند توفر بيانات محفوظة) */
const act = html.match(/class="rail-item active"[\s\S]{0,400}?<span class="lb">([^<]+)<\/span>/);
const title = html.match(/id="tbTitle">([^<]*)</);
check("عنوان الشريط العلوي مطابق للقائمة النشطة",
  !!(act && title && act[1].trim() === title[1].trim()),
  `الشاشة النشطة="${act ? act[1] : "?"}" | العنوان="${title ? title[1] : "?"}"`);
check("متن المسار (breadcrumb) مبني", /id="crumb">/.test(html) && html.includes("<b>"), "crumb");
check("الساعة والتاريخ يعملان", /id="clock">[\s\S]{5,80}?\d{2}:\d{2}/.test(html), "clock");

const badges = count(/class="bdg/g);
check("شارات القائمة مبنية من الملخص", page === "index" ? true : badges > 0, `شارات=${badges}`);
check("لوحة الأوامر مضافة (Ctrl+K)", html.includes('class="palette"') && html.includes('id="palInput"'), "palette");

console.log("=".repeat(70));
console.log(`${page}: PASS=${pass} | FAIL=${fail}`);
process.exit(fail ? 1 : 0);
