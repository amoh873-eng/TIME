import fs from "node:fs";
const file = process.argv[2];
const html = fs.readFileSync(file, "utf8");
const m = html.match(/<pre id="out">([\s\S]*?)<\/pre>/);
if (!m) { console.log("NO OUTPUT BLOCK"); process.exit(1); }
const txt = m[1].replace(/&lt;/g, "<").replace(/&gt;/g, ">").replace(/&amp;/g, "&");
console.log(txt.trim());
const fails = (txt.match(/\[FAIL\]/g) || []).length;
const passes = (txt.match(/\[PASS\]/g) || []).length;
console.log(`--- self-test: PASS=${passes} FAIL=${fails} ---`);
process.exit(fails ? 1 : 0);
