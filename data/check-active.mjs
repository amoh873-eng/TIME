/* فحص الشاشة المفتوحة في DOM مُنفَّذ: node check-active.mjs <dump.html> [expectedScreenId]
   يُخرج علامة ASCII (RESULT=PASS/FAIL) لتفادي مشاكل ترميز مخرجات الطرفية */
import fs from "node:fs";
const [file, expected] = process.argv.slice(2);
const html = fs.readFileSync(file, "utf8");

const active = html.match(/class="rail-item active"[\s\S]{0,400}?data-goto="([^"]+)"/);
const title = html.match(/id="tbTitle">([^<]*)</);
const hidden = (html.match(/class="[^"]*\bscreen\b[^"]*\boff\b/g) || []).length;
const id = active ? active[1] : "?";
const ok = expected ? id === expected : !!active;

console.log(`ACTIVE_ID=${id} | HIDDEN=${hidden} | TITLE_LEN=${title ? title[1].trim().length : 0}`);
if (expected) console.log(`RESULT=${ok ? "PASS" : "FAIL"} | expected=${expected}`);
process.exit(expected && !ok ? 1 : 0);
