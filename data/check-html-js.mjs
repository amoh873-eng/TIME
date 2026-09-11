/* أداة تحقّق: تستخرج كل كتل <script> الداخلية من ملف HTML وتتحقّق من صحة صياغتها.
   الاستخدام:  node data/check-html-js.mjs path\to\page.html                     */
import fs from "node:fs";
import vm from "node:vm";

const files = process.argv.slice(2);
let bad = 0;

for (const file of files) {
  const html = fs.readFileSync(file, "utf8");
  const re = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;
  let m, n = 0;
  while ((m = re.exec(html))) {
    n++;
    try {
      new vm.Script(m[1], { filename: `${file}#script${n}` });
      console.log(`${file} :: script ${n} OK (${m[1].length} chars)`);
    } catch (e) {
      bad++;
      console.log(`${file} :: script ${n} SYNTAX ERROR -> ${e.message}`);
    }
  }
  console.log(`${file} :: blocks=${n}`);
}
console.log(bad ? `FAILED: ${bad} syntax error(s)` : "ALL SCRIPTS OK");
process.exit(bad ? 1 : 0);
