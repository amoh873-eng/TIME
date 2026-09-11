/* ============================================================================
   shell.js — نواة اللوحة المركزية (TIME Attendance System)
   ----------------------------------------------------------------------------
   نسخة واحدة مشتركة بين punch.html و index.html — بلا أي مكتبات خارجية:
     1) TIME.shell(cfg)   : شريط تنقّل رئيسي على اليمين + شاشة واحدة فقط في كل مرة
                            + موجّه (hash router) + لوحة أوامر (Ctrl+K) + شريط علوي.
     2) TIME.chart(ref,cfg): مخطط دائري (قرص) أو حلقي ثلاثي الأبعاد على canvas
                            بعمق مُجسَّم وظل أرضي ولمعة زجاجية وتفاعل تحويم.
     3) TIME.toast(...)   : تنبيهات سريعة.
   ========================================================================== */
window.TIME = (function () {
  "use strict";

  /* ------------------------------ أدوات عامة ------------------------------ */
  var $ = function (id) { return document.getElementById(id); };
  function on(el, ev, fn) { if (el) el.addEventListener(ev, fn); }

  function esc(s) {
    return String(s === null || s === undefined ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }
  function fmt(n, dec) {
    if (n === null || n === undefined || n === "") return "—";
    var v = Number(n);
    if (isNaN(v)) return "—";
    return v.toLocaleString("en-US", { maximumFractionDigits: dec === undefined ? 2 : dec });
  }
  function trim(s, max) {
    s = String(s === null || s === undefined ? "" : s);
    return s.length > max ? s.slice(0, max - 1) + "…" : s;
  }

  /* ألوان: نفس منطق لوحة المغادرات (تفتيح/تغميق) لضمان اتساق الشكل بين الواجهات */
  function parseColor(c) {
    if (Array.isArray(c)) return c;
    var s = String(c || "#38bdf8").trim();
    if (s.charAt(0) === "#") {
      if (s.length === 4) s = "#" + s[1] + s[1] + s[2] + s[2] + s[3] + s[3];
      return [parseInt(s.substr(1, 2), 16), parseInt(s.substr(3, 2), 16), parseInt(s.substr(5, 2), 16)];
    }
    var m = s.match(/rgba?\(([^)]+)\)/i);
    if (m) {
      var p = m[1].split(",");
      return [parseInt(p[0], 10) || 0, parseInt(p[1], 10) || 0, parseInt(p[2], 10) || 0];
    }
    return [56, 189, 248];
  }
  function shade(c, k) {
    var a = parseColor(c);
    return [Math.max(0, Math.min(255, Math.round(a[0] * k))),
            Math.max(0, Math.min(255, Math.round(a[1] * k))),
            Math.max(0, Math.min(255, Math.round(a[2] * k)))];
  }
  function css(c, alpha) {
    var a = parseColor(c);
    return alpha === undefined ? "rgb(" + a[0] + "," + a[1] + "," + a[2] + ")"
      : "rgba(" + a[0] + "," + a[1] + "," + a[2] + "," + alpha + ")";
  }
  var PALETTE = { ok: "#22c55e", bad: "#f87171", warn: "#fbbf24", info: "#38bdf8",
    purple: "#a78bfa", gray: "#64748b", teal: "#2dd4bf", pink: "#f472b6", gold: "#eab308" };

  /* ------------------------------- تنبيهات ------------------------------- */
  function toastsHost() {
    var h = document.querySelector(".toasts");
    if (!h) { h = document.createElement("div"); h.className = "toasts"; document.body.appendChild(h); }
    return h;
  }
  function toast(msg, kind, ms) {
    var h = toastsHost(), d = document.createElement("div");
    d.className = "toast " + (kind || "");
    d.innerHTML = msg;
    h.appendChild(d);
    setTimeout(function () {
      d.style.transition = "opacity .3s ease"; d.style.opacity = "0";
      setTimeout(function () { if (d.parentNode) d.parentNode.removeChild(d); }, 320);
    }, ms || 4200);
  }

  /* --------------------------- سجلّ المخططات ----------------------------- */
  var charts = [];

  function legendHtml(slices, unit, dec) {
    return slices.map(function (s, i) {
      var pct = (s.pct === undefined || s.pct === null) ? "" : fmt(s.pct, dec) + "%";
      return '<div class="lg-row" data-i="' + i + '">' +
        '<span class="sw" style="background:' + css(s.color) + '"></span>' +
        '<span><span class="lname">' + esc(s.label) + '</span><span class="lp">' +
        (pct ? pct + " — " : "") + fmt(s.value, dec) + (unit ? " " + esc(unit) : "") +
        '</span></span><span class="lv">' + fmt(s.share, 1) + '%</span></div>';
    }).join("");
  }

  /* TIME.chart('#dashDays', {...}) — يرسم المخطط ويربط المفتاح (legend) والتلميح
     إعادة النداء على نفس اللوحة تُحدّث البيانات فقط (بلا تكرار مستمعي الأحداث). */
  function chart(ref, cfg) {
    var cv = typeof ref === "string" ? document.querySelector(ref) : ref;
    if (!cv) return null;
    cfg = cfg || {};

    var card = cv.closest ? cv.closest(".chartcard") : null;
    var legHost = card ? card.querySelector("[data-legend]") : null;
    var tip = card ? card.querySelector(".tipbox") : null;

    var rec = null;
    charts.forEach(function (r) { if (r.canvas === cv) rec = r; });

    if (rec) {
      rec.cfg = cfg; rec.hover = -1; rec.legend = legHost; rec.tip = tip;
      refreshLegend(rec);
      if (cfg.animate === false) { rec.progress = 1; paint(rec); }
      else animateIn(rec);
      return rec;
    }

    rec = { canvas: cv, cfg: cfg, legend: legHost, tip: tip, progress: 0, hover: -1, _raf: 0 };
    charts.push(rec);
    wire(rec);
    refreshLegend(rec);
    if (cfg.animate === false) { rec.progress = 1; paint(rec); }
    else animateIn(rec);
    return rec;
  }

  function refreshLegend(rec) {
    if (!rec.legend) return;
    var slices = (rec.cfg.slices || []).filter(function (s) { return +s.value > 0; });
    rec.legend.innerHTML = slices.length ? legendHtml(slices, rec.cfg.unit, rec.cfg.decimals)
      : '<div class="hint">' + esc(rec.cfg.emptyText || "لا توجد بيانات لعرضها بعد") + "</div>";
  }

  /* ---------------------- الإسقاط البيضوي (هندسة المنظور) ----------------- */
  function geom(rec) {
    var cv = rec.canvas, cfg = rec.cfg;
    var dpr = Math.max(1, Math.min(3, window.devicePixelRatio || 1));
    var W = Math.max(240, cv.clientWidth || 360);
    var H = cfg.height || (W < 420 ? 252 : 296);
    if (cv.width !== Math.round(W * dpr) || cv.height !== Math.round(H * dpr)) {
      cv.width = Math.round(W * dpr); cv.height = Math.round(H * dpr);
      cv.style.height = H + "px";
    }
    var ctx = cv.getContext("2d");
    cv.__cssW = W; cv.__cssH = H;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, W, H);

    var ky = cfg.tilt === undefined ? 0.56 : cfg.tilt;
    var cx = W / 2, cy = H * 0.44;
    var rxOut = Math.min(W * 0.40, cfg.maxRadius || 152), ryOut = rxOut * ky;
    var donut = (cfg.mode === "donut");
    var rIn = donut ? rxOut * (cfg.hole === undefined ? 0.5 : cfg.hole) : 0, ryIn = rIn * ky;
    var depth = Math.round(rxOut * (cfg.depth === undefined ? 0.19 : cfg.depth));
    return { ctx: ctx, W: W, H: H, cx: cx, cy: cy, ky: ky, rxOut: rxOut, ryOut: ryOut,
             rIn: rIn, ryIn: ryIn, depth: depth, donut: donut };
  }

  function easeOut(t) { return 1 - Math.pow(1 - t, 3); }

  function animateIn(rec) {
    if (rec._raf) cancelAnimationFrame(rec._raf);
    var t0 = performance.now(), dur = rec.cfg.duration || 760;
    function tick(now) {
      var t = Math.min(1, (now - t0) / dur);
      rec.progress = easeOut(t);
      paint(rec);
      if (t < 1) rec._raf = requestAnimationFrame(tick); else rec._raf = 0;
    }
    rec._raf = requestAnimationFrame(tick);
  }

  /* ------------------- الرسم: عمق مُجسَّم + ظل + لمعة زجاجية ----------------- */
  function paint(rec) {
    var g = geom(rec), ctx = g.ctx, cfg = rec.cfg;
    var slices = (cfg.slices || []).filter(function (s) { return +s.value > 0; });
    rec.geo = null;
    if (!slices.length) {
      ctx.fillStyle = "#64748b";
      ctx.font = '600 13px "Segoe UI",Tahoma,sans-serif';
      ctx.textAlign = "center"; ctx.textBaseline = "middle";
      ctx.fillText(cfg.emptyText || "لا توجد بيانات لعرضها بعد", g.W / 2, g.cy);
      return;
    }

    var totalVal = slices.reduce(function (a, s) { return a + (+s.value || 0); }, 0) || 1;
    var prog = rec.progress === undefined ? 1 : rec.progress;
    var acc = -Math.PI / 2, angles = [];
    slices.forEach(function (s) {
      var a0 = acc;
      acc += ((+s.value || 0) / totalVal) * Math.PI * 2 * prog;
      angles.push([a0, acc, (a0 + acc) / 2]);
    });

    /* قطاع (قرص كامل أو حلقة) مُزاح للأسفل بمقدار العمق = الجدار المجسَّم */
    function sector(i, off, extra) {
      var a0 = angles[i][0], a1 = angles[i][1];
      if (a1 - a0 < 0.0008) return false;
      var d = g.depth + (extra || 0);
      var ox = off ? off.x : 0, oy = off ? off.y : 0;
      ctx.beginPath();
      ctx.ellipse(g.cx + ox, g.cy + d + oy, g.rxOut, g.ryOut, 0, a0, a1);
      if (g.donut) ctx.ellipse(g.cx + ox, g.cy + d + oy, g.rIn, g.ryIn, 0, a1, a0, true);
      else ctx.lineTo(g.cx + ox, g.cy + d + oy);
      ctx.closePath();
      return true;
    }

    rec.geo = { cx: g.cx, cy: g.cy, rxOut: g.rxOut, ryOut: g.ryOut, rIn: g.rIn, depth: g.depth, angles: angles };

    /* الترتيب من الخلف إلى الأمام لضمان التغطية الصحيحة في المنظور المائل */
    var order = slices.map(function (s, i) { return i; })
      .sort(function (a, b) { return Math.sin(angles[a][2]) - Math.sin(angles[b][2]); });
    var hover = rec.hover, hov = (hover >= 0 && hover < slices.length) ? slices[hover] : null;
    var noDim = (hover >= 0);

    /* 1) ظل أرضي ناعم يمنح الإحساس بالعمق */
    var sh = ctx.createRadialGradient(g.cx, g.cy + g.depth + 12, 3, g.cx, g.cy + g.depth + 12, g.rxOut * 1.22);
    sh.addColorStop(0, "rgba(2,6,23,.62)"); sh.addColorStop(1, "rgba(2,6,23,0)");
    ctx.fillStyle = sh;
    ctx.beginPath();
    ctx.ellipse(g.cx, g.cy + g.depth + 12, g.rxOut * 1.08, g.ryOut * 1.06, 0, 0, Math.PI * 2);
    ctx.fill();

    /* 2) جدران الشرائح المجسّمة (نسخة داكنة مُزاحة للأسفل) */
    order.forEach(function (i) {
      var off = explodeOffset(rec, angles, i);
      ctx.fillStyle = css(shade(slices[i].color, noDim && i !== hover ? 0.40 : 0.52));
      if (sector(i, off, 0)) ctx.fill();
    });

    /* 3) جدار الفتحة الوسطية للمخطط الحلقي (تجويف مظلَّل) */
    if (g.donut) {
      var wg = ctx.createLinearGradient(0, g.cy, 0, g.cy + g.depth + g.ryIn);
      wg.addColorStop(0, "#1b2a45"); wg.addColorStop(1, "#070e1b");
      ctx.fillStyle = wg;
      ctx.beginPath();
      ctx.ellipse(g.cx, g.cy + g.depth, g.rIn, g.ryIn, 0, 0, Math.PI * 2);
      ctx.fill();
    }
    /* 4) الوجه العلوي: لمعة زجاجية + تعتيم بقية الشرائح عند التحويم */
    order.forEach(function (i) {
      var s = slices[i], off = explodeOffset(rec, angles, i), hi = (hover === i);
      var lg = ctx.createLinearGradient(0, g.cy - g.ryOut, 0, g.cy + g.ryOut);
      lg.addColorStop(0, css(shade(s.color, hi ? 1.58 : 1.30)));
      lg.addColorStop(0.5, css(shade(s.color, hi ? 1.14 : 1)));
      lg.addColorStop(1, css(shade(s.color, 0.72)));
      if (!sector(i, off, 0)) return;
      if (hi) { ctx.save(); ctx.shadowColor = css(s.color, 0.85); ctx.shadowBlur = 16; }
      ctx.fillStyle = lg; ctx.fill();
      if (hi) ctx.restore();
      if (noDim && !hi) { ctx.fillStyle = "rgba(11,18,32,.5)"; ctx.fill(); }
      ctx.lineWidth = 1; ctx.strokeStyle = "rgba(10,17,31,.85)"; ctx.stroke();
      if (hi) { ctx.lineWidth = 1.7; ctx.strokeStyle = "rgba(226,232,240,.9)"; ctx.stroke(); }
    });

    /* 5) تفريغ فتحة الوسط (شفافة فعلياً لخلفية البطاقة) */
    if (g.donut) {
      ctx.globalCompositeOperation = "destination-out";
      ctx.beginPath();
      ctx.ellipse(g.cx, g.cy, g.rIn, g.ryIn, 0, 0, Math.PI * 2);
      ctx.fill();
      ctx.globalCompositeOperation = "source-over";
    }

    /* 6) بطاقات النسب على الشرائح الواسعة فقط (مع إبراز الشريحة المحوَّم عليها) */
    if (cfg.labels !== false && prog > 0.995) {
      ctx.textAlign = "center"; ctx.textBaseline = "middle";
      ctx.font = '700 12px "Segoe UI",Tahoma,sans-serif';
      var minA = cfg.labelMinAngle === undefined ? 0.42 : cfg.labelMinAngle;
      order.forEach(function (i) {
        if (angles[i][1] - angles[i][0] < minA) return;
        if (hover >= 0 && i !== hover) return;
        var s = slices[i], off = explodeOffset(rec, angles, i);
        var mid = angles[i][2];
        var rm = g.donut ? (g.rIn + g.rxOut) / 2 : g.rxOut * 0.62;
        var x = g.cx + Math.cos(mid) * rm + (off ? off.x : 0);
        var y = g.cy + Math.sin(mid) * rm * g.ky + (off ? off.y : 0);
        var txt = fmt(s.pct === undefined ? (s.share === undefined ? (100 * s.value / totalVal) : s.share) : s.pct,
          cfg.decimals === undefined ? 1 : cfg.decimals) + "%";
        ctx.fillStyle = "rgba(6,13,24,.72)"; ctx.fillText(txt, x + 1, y + 1);
        ctx.fillStyle = "#f8fafc"; ctx.fillText(txt, x, y);
      });
    }

    /* 7) متن وسط المخطط: القيمة المختارة أو الشريحة المحوَّم عليها */
    if (g.donut) {
      var centerV = hov
        ? fmt(hov.pct === undefined ? hov.share : hov.pct, cfg.decimals === undefined ? 1 : cfg.decimals) + "%"
        : cfg.centerValue;
      var centerL = hov ? hov.label : (cfg.centerLabel || "");
      ctx.textAlign = "center"; ctx.textBaseline = "middle";
      if (centerV !== undefined && centerV !== null && centerV !== "") {
        ctx.fillStyle = hov ? "#e2e8f0" : (cfg.centerColor || "#38bdf8");
        ctx.font = '800 21px "Segoe UI",Tahoma,sans-serif';
        ctx.fillText(centerV, g.cx, g.cy - (centerL ? 7 : 1));
      }
      if (centerL) {
        ctx.fillStyle = "#94a3b8";
        ctx.font = '600 11px "Segoe UI",Tahoma,sans-serif';
        ctx.fillText(trim(centerL, 26), g.cx, g.cy + 14);
      }
    }
  }

  /* إزاحة الشريحة المحوَّم عليها (انفجار ثلاثي الأبعاد للأقراص الكاملة فقط) */
  function explodeOffset(rec, angles, i) {
    if (rec.cfg.mode === "donut" || rec.hover !== i || !rec.geo) return null;
    var g = rec.geo, mid = angles[i][2], k = rec.cfg.explode === undefined ? 0.075 : rec.cfg.explode;
    return { x: Math.cos(mid) * g.rxOut * k, y: Math.sin(mid) * g.ryOut * k * 1.7 };
  }

  /* --------------------- التفاعل: تحويم + تلميح + المفتاح ------------------ */
  function localPoint(rec, clientX, clientY) {
    var r = rec.canvas.getBoundingClientRect();
    var W = rec.canvas.__cssW || r.width || 1, H = rec.canvas.__cssH || r.height || 1;
    return { x: (clientX - r.left) * (W / (r.width || W)), y: (clientY - r.top) * (H / (r.height || H)) };
  }

  function hitChart(rec, x, y) {
    var g = rec.geo;
    if (!g) return -1;
    var dx = (x - g.cx) / g.rxOut, dy = (y - g.cy) / g.ryOut;
    var rr = Math.sqrt(dx * dx + dy * dy);
    if (rr > 1.03) return -1;
    if (g.rIn > 0 && rr < (g.rIn / g.rxOut) * 0.94) return -1;
    var ang = Math.atan2(dy, dx);
    if (ang < -Math.PI / 2) ang += Math.PI * 2;
    for (var i = 0; i < g.angles.length; i++) {
      if (ang >= g.angles[i][0] && ang < g.angles[i][1]) return i;
    }
    return -1;
  }

  function markLegend(rec, idx) {
    if (!rec.legend) return;
    var rows = rec.legend.querySelectorAll(".lg-row[data-i]");
    Array.prototype.forEach.call(rows, function (r) {
      r.classList.toggle("hl", parseInt(r.getAttribute("data-i"), 10) === idx);
    });
  }

  function showTip(rec, clientX, clientY, idx) {
    if (!rec.tip) return;
    var slices = (rec.cfg.slices || []).filter(function (s) { return +s.value > 0; });
    if (idx < 0 || !slices[idx]) { rec.tip.style.display = "none"; return; }
    var s = slices[idx], box = rec.tip.parentElement.getBoundingClientRect();
    var total = slices.reduce(function (a, x) { return a + (+x.value || 0); }, 0) || 1;
    var pct = s.pct === undefined ? (s.share === undefined ? (100 * s.value / total) : s.share) : s.pct;
    rec.tip.innerHTML = "<b>" + esc(s.label) + "</b>" +
      esc(rec.cfg.tipLabel || "القيمة") + ": <b>" + fmt(s.value, rec.cfg.decimals) +
      (rec.cfg.unit ? " " + esc(rec.cfg.unit) : "") + "</b><br>النسبة: " + fmt(pct, 1) + "%" +
      (s.note ? "<br>" + esc(s.note) : "");
    rec.tip.style.display = "block";
    var w = rec.tip.offsetWidth, h = rec.tip.offsetHeight;
    var left = clientX - box.left - w / 2, top = clientY - box.top - h - 12;
    left = Math.max(6, Math.min(box.width - w - 6, left));
    if (top < 6) top = clientY - box.top + 16;
    rec.tip.style.left = left + "px";
    rec.tip.style.top = top + "px";
  }

  function setHover(rec, idx, cx, cy) {
    if (rec.hover === idx) { if (idx >= 0) showTip(rec, cx, cy, idx); return; }
    rec.hover = idx;
    markLegend(rec, idx);
    paint(rec);
    if (typeof rec.cfg.onHover === "function") rec.cfg.onHover(idx, (rec.cfg.slices || [])[idx]);
    if (idx >= 0) showTip(rec, cx, cy, idx); else if (rec.tip) rec.tip.style.display = "none";
  }

  function wire(rec) {
    var cv = rec.canvas;
    cv.__cssW = 0; cv.__cssH = 0;

    cv.addEventListener("mousemove", function (e) {
      var p = localPoint(rec, e.clientX, e.clientY);
      setHover(rec, hitChart(rec, p.x, p.y), e.clientX, e.clientY);
    });
    cv.addEventListener("mouseleave", function () {
      if (rec.hover !== -1) { rec.hover = -1; markLegend(rec, -1); paint(rec); setHover(rec, -1); }
      if (rec.tip) rec.tip.style.display = "none";
    });
    cv.addEventListener("touchstart", function (e) {
      if (!e.touches || !e.touches.length) return;
      var t = e.touches[0], p = localPoint(rec, t.clientX, t.clientY);
      var i = hitChart(rec, p.x, p.y);
      if (i >= 0) { e.preventDefault(); setHover(rec, i, t.clientX, t.clientY); }
    }, { passive: false });
    cv.addEventListener("touchend", function () {
      if (rec.hover !== -1) { rec.hover = -1; markLegend(rec, -1); paint(rec); }
      if (rec.tip) rec.tip.style.display = "none";
    });

    if (rec.legend) {
      rec.legend.addEventListener("mousemove", function (e) {
        var row = e.target.closest ? e.target.closest(".lg-row[data-i]") : null;
        setHover(rec, row ? parseInt(row.getAttribute("data-i"), 10) : -1, 0, 0);
      });
      rec.legend.addEventListener("mouseleave", function () { setHover(rec, -1); });
    }
  }

  /* إعادة رسم كل المخططات الظاهرة (بعد تغيير الشاشة أو حجم النافذة) */
  function refreshAll() {
    charts.forEach(function (rec) {
      if (rec.canvas.clientWidth > 0) {
        if (rec.cfg.animate === false) { rec.progress = 1; paint(rec); }
        else animateIn(rec);
      }
    });
  }
  /* ===================== اللوحة المركزية: التنقّل والشاشات ================== */
  function railHtml(cfg) {
    var b = cfg.brand || {};
    var h = '<div class="rail-head"><div class="rail-logo">' + (b.icon || "⏱") + "</div>" +
      '<div><div class="rb-t">' + esc(b.title || "TIME") + '</div>' +
      '<div class="rb-s">' + esc(b.sub || "") + "</div></div></div>";
    h += '<div class="rail-search"><button type="button" id="railSearch" title="بحث سريع في القائمة">' +
      "<span>🔍</span><span>ابحث في القائمة…</span><span class=\"kbd\">Ctrl K</span></button></div>";
    h += '<nav class="rail-nav" id="railNav" aria-label="القائمة الرئيسية">';
    (cfg.groups || []).forEach(function (g) {
      h += '<div class="rail-grp"><h4>' + esc(g.title) + "</h4>";
      (g.items || []).forEach(function (it) {
        h += '<button type="button" class="rail-item' + (it.href || it.act ? " link" : "") + '"' +
          ' data-goto="' + esc(it.id) + '"' + (it.href ? ' data-href="' + esc(it.href) + '"' : "") +
          (it.act ? ' data-act="' + esc(it.act) + '"' : "") +
          (it.hint ? ' title="' + esc(it.hint) + '"' : "") + ">" +
          '<span class="ico">' + (it.icon || "•") + '</span>' +
          '<span class="lb">' + esc(it.label) + "</span>" +
          (it.badge ? '<span class="bdg ' + esc(it.badgeKind || "") + '">' + esc(it.badge) + "</span>" : "") +
          "</button>";
      });
      h += "</div>";
    });
    h += "</nav>";
    h += '<div class="rail-foot"><button type="button" class="rail-collapse" id="railToggle">' +
      "<span>⇤</span><span>طيّ القائمة</span></button>" +
      '<div class="rf-note">' + esc(cfg.footNote || "الواجهة تعمل محلياً دون إنترنت") + "</div></div>";
    return h;
  }

  function shell(cfg) {
    cfg = cfg || {};
    var flat = [];
    (cfg.groups || []).forEach(function (g) {
      (g.items || []).forEach(function (it) { it.groupTitle = g.title; flat.push(it); });
    });
    var byId = {};
    flat.forEach(function (it) { byId[it.id] = it; });

    var rail = $("rail");
    if (rail) rail.innerHTML = railHtml(cfg);
    var state = { id: null, mini: false };
    try { state.mini = localStorage.getItem("time.rail.mini") === "1"; } catch (e) { }
    if (state.mini) document.body.classList.add("rail-mini");

    var homeId = cfg.home || (flat.length ? flat[0].id : null);

    function railButtons() {
      return rail ? Array.prototype.slice.call(rail.querySelectorAll(".rail-item[data-goto]")) : [];
    }

    /* الانتقال بين الشاشات: تظهر شاشة واحدة فقط، والبقية مخفية */
    function go(id, opts) {
      opts = opts || {};
      var wanted = id, els = document.querySelectorAll("[data-screen]"), found = false, i;
      for (i = 0; i < els.length; i++) if (els[i].getAttribute("data-screen") === wanted) found = true;
      if (!found) wanted = homeId;
      for (i = 0; i < els.length; i++) {
        els[i].classList.toggle("off", els[i].getAttribute("data-screen") !== wanted);
      }
      var it = byId[wanted] || {};
      state.id = wanted;

      railButtons().forEach(function (b) {
        var act = b.getAttribute("data-goto") === wanted;
        b.classList.toggle("active", act);
        if (act) b.setAttribute("aria-current", "page"); else b.removeAttribute("aria-current");
      });

      var tt = $("tbTitle"), ts = $("tbSub"), crumb = $("crumb");
      if (tt) tt.textContent = it.label || (cfg.brand && cfg.brand.title) || "";
      if (ts && (it.hint || cfg.brand)) ts.textContent = it.hint || (cfg.brand && cfg.brand.sub) || "";
      if (crumb) {
        crumb.innerHTML = (cfg.brand ? "<span>" + esc(cfg.brand.title) + "</span><span>/</span>" : "") +
          '<b>' + esc(it.label || wanted) + "</b>";
      }

      document.body.classList.remove("rail-open");
      if (!opts.keepScroll) window.scrollTo({ top: 0, behavior: opts.smooth ? "smooth" : "auto" });
      if (!opts.silent) {
        var next = "#/" + wanted;
        if (location.hash !== next) { try { location.hash = next; } catch (e2) { } }
      }
      refreshAll();
      document.dispatchEvent(new CustomEvent("time:screen", { detail: { id: wanted } }));
      if (typeof cfg.onScreen === "function") cfg.onScreen(wanted, it);
    }

    function hashId() {
      var h = String(location.hash || "");
      if (!h || h === "#") return null;
      var id = h.charAt(1) === "/" ? h.slice(2) : h.slice(1);
      try { id = decodeURIComponent(id); } catch (e) { }
      return id || null;
    }

    /* نقرات القائمة */
    railButtons().forEach(function (b) {
      b.addEventListener("click", function () {
        var href = b.getAttribute("data-href");
        if (href) { window.location.href = href; return; }
        var act = b.getAttribute("data-act");
        if (act) {
          if (typeof cfg.onAction === "function") cfg.onAction(act, byId[b.getAttribute("data-goto")]);
          return;
        }
        go(b.getAttribute("data-goto"));
      });
    });
    /* الشريط العلوي: زر القائمة للجوّال + السحب للإغلاق */
    on($("burger"), "click", function () { document.body.classList.toggle("rail-open"); });
    on($("scrim"), "click", function () { document.body.classList.remove("rail-open"); });
    on($("railToggle"), "click", function () {
      state.mini = !state.mini;
      document.body.classList.toggle("rail-mini", state.mini);
      try { localStorage.setItem("time.rail.mini", state.mini ? "1" : "0"); } catch (e) { }
      setTimeout(refreshAll, 240);
    });

    window.addEventListener("hashchange", function () {
      var id = hashId();
      if (id && id !== state.id) go(id, { silent: true });
    });

    /* فتح الشاشة الابتدائية (تستقبل أيضاً الروابط القديمة مثل #reportsSection) */
    var initial = hashId() || homeId;
    go(initial, { silent: true });
    try { history.replaceState(null, "", "#/" + (state.id || homeId)); } catch (e) { }
    /* ضبط أبعاد المخططات بعد استقرار التخطيط (الخطوط + القائمة) */
    setTimeout(refreshAll, 350);

    /* --------------------------- لوحة الأوامر (Ctrl+K) --------------------------- */
    var pal = document.createElement("div");
    pal.className = "palette";
    pal.innerHTML = '<div class="pbox"><input type="text" id="palInput" aria-label="بحث في القائمة" ' +
      'placeholder="اكتب للبحث… مثال: الورديات، العطل، تقارير، دوام مرن" />' +
      '<div class="plist" id="palList"></div></div>';
    document.body.appendChild(pal);
    var palInput = pal.querySelector("#palInput"), palList = pal.querySelector("#palList");
    var palSel = 0, palRows = [];

    function palClose() { pal.classList.remove("open"); }
    function palOpen() { pal.classList.add("open"); palInput.value = ""; palFilter(""); palInput.focus(); }
    function palFilter(q) {
      q = String(q || "").trim().toLowerCase();
      palRows = flat.filter(function (it) {
        if (!q) return true;
        return (it.label + " " + (it.groupTitle || "") + " " + (it.keywords || "")).toLowerCase().indexOf(q) >= 0;
      });
      palSel = palRows.length ? 0 : -1;
      palList.innerHTML = palRows.length ? palRows.map(function (it, i) {
        return '<button type="button" data-p="' + i + '"' + (i === 0 ? ' class="on"' : "") + ">" +
          '<span class="ico">' + (it.icon || "•") + "</span><span>" + esc(it.label) + "</span>" +
          '<span class="pgrp">' + esc(it.groupTitle || "") + "</span></button>";
      }).join("") : '<div class="pempty">لا نتائج مطابقة</div>';
    }
    function palMove(d) {
      if (!palRows.length) return;
      palSel = (palSel + d + palRows.length) % palRows.length;
      Array.prototype.forEach.call(palList.querySelectorAll("button[data-p]"), function (b) {
        b.classList.toggle("on", parseInt(b.getAttribute("data-p"), 10) === palSel);
      });
      var cur = palList.querySelector("button.on");
      if (cur && cur.scrollIntoView) cur.scrollIntoView({ block: "nearest" });
    }
    function palActivate() {
      var it = palRows[palSel];
      if (!it) return;
      palClose();
      if (it.href) window.location.href = it.href; else go(it.id);
    }
    on(palInput, "input", function () { palFilter(palInput.value); });
    on(palList, "click", function (e) {
      var b = e.target.closest ? e.target.closest("button[data-p]") : null;
      if (!b) return;
      palSel = parseInt(b.getAttribute("data-p"), 10); palActivate();
    });
    on(pal, "click", function (e) { if (e.target === pal) palClose(); });
    on($("railSearch"), "click", palOpen);

    /* --------------------------- الساعة والتاريخ ---------------------------- */
    function tickClock() {
      var c = $("clock");
      if (!c) return;
      var d = new Date(), date, time;
      try { date = d.toLocaleDateString("ar-EG-u-nu-latn", { weekday: "long", day: "numeric", month: "long", year: "numeric" }); }
      catch (e) { date = d.toLocaleDateString("en-GB"); }
      time = d.toLocaleTimeString("en-GB", { hour: "2-digit", minute: "2-digit" });
      c.innerHTML = "<span>🗓</span><span>" + esc(date) + "</span><b>" + time + "</b>";
    }
    tickClock();
    setInterval(tickClock, 20000);

    /* ------------------------- الاختصارات وشارات القائمة --------------------- */
    function gotoStep(step) {
      var list = flat.filter(function (it) { return !it.href; });
      var i = 0;
      list.forEach(function (it, k) { if (it.id === state.id) i = k; });
      var next = list[(i + step + list.length) % list.length];
      if (next) go(next.id);
    }
    document.addEventListener("keydown", function (e) {
      var k = e.key || "";
      if ((e.ctrlKey || e.metaKey) && (k === "k" || k === "K")) {
        e.preventDefault();
        if (pal.classList.contains("open")) palClose(); else palOpen();
        return;
      }
      if (pal.classList.contains("open")) {
        if (k === "Escape") { palClose(); return; }
        if (k === "ArrowDown" || k === "ArrowUp") { e.preventDefault(); palMove(k === "ArrowDown" ? 1 : -1); return; }
        if (k === "Enter") { e.preventDefault(); palActivate(); return; }
        return;
      }
      if (k === "Escape") { document.body.classList.remove("rail-open"); return; }
      if (e.altKey && e.shiftKey === false && (k === "ArrowDown" || k === "ArrowUp")) {
        e.preventDefault(); gotoStep(k === "ArrowDown" ? 1 : -1);
      }
    });

    function setBadge(id, text, kind) {
      var b = rail ? rail.querySelector('.rail-item[data-goto="' + id + '"]') : null;
      if (!b) return;
      var d = b.querySelector(".bdg");
      if (!text) { if (d) d.parentNode.removeChild(d); return; }
      if (!d) { d = document.createElement("span"); b.appendChild(d); }
      d.className = "bdg " + (kind || "");
      d.textContent = text;
    }

    document.addEventListener("time:screen", function () { setTimeout(refreshAll, 60); });

    return { go: go, items: flat, state: state, refresh: refreshAll, setBadge: setBadge,
             openPalette: palOpen, closePalette: palClose };
  }

  /* ==========================================================================
     منطقة تنظيف البيانات (Danger zone) — منطق واحد مشترك بين اللوحتين:
     قراءة حالة الجداول من الخدمة، عرض ما سيُحذف لكل جدول وما سيُحفظ، ثم تنفيذ
     التنظيف بتأكيد نصّي («حذف»). التهيئة: TIME.maintenance({ root:'cleanup', onDone })
     ========================================================================== */
  function maintenance(cfg) {
    cfg = cfg || {};
    var api = cfg.api || "/api/v1/maintenance";
    var root = typeof cfg.root === "string" ? $(cfg.root) : cfg.root;
    if (!root) return null;

    var scopeInputs = root.querySelectorAll('input[name="dzScope"]');
    var opts = root.querySelectorAll(".dz-opt");
    var elRule = $("dzRule"), elRefresh = $("dzRefresh"), elSum = $("dzSum");
    var elBody = $("dzTbody"), elConfirm = $("dzConfirm"), elRun = $("dzRun");
    var elErr = $("dzErr"), elDone = $("dzResult"), elDry = $("dzDry");

    var state = { busy: false, loading: false, status: null, result: null };
    var WORDS = ["حذف", "تنظيف", "delete", "reset"];

    function scope() {
      for (var i = 0; i < scopeInputs.length; i++) { if (scopeInputs[i].checked) return scopeInputs[i].value; }
      return "data";
    }
    function typed() { return elConfirm && elConfirm.value ? elConfirm.value.trim() : ""; }
    function confirmed() {
      var w = typed().toLowerCase();
      for (var i = 0; i < WORDS.length; i++) { if (w === WORDS[i]) return true; }
      return false;
    }
    function ready() { return !state.busy && confirmed(); }
    function gate() { if (elRun) elRun.disabled = !ready(); }
    function markOpts() {
      for (var i = 0; i < opts.length; i++) {
        var r = opts[i].querySelector("input");
        opts[i].classList.toggle("on", !!(r && r.checked));
      }
    }
    function updateSum() {
      if (!elSum) return;
      var s = state.status;
      if (!s) { elSum.textContent = ""; return; }
      var all = scope() === "all";
      var rows = all ? s.allRows : s.dataRows;
      var tables = all ? s.allTables : s.dataTables;
      var kept = all ? 0 : s.referenceTables;
      elSum.innerHTML = "سيُفرَّغ <b>" + tables + "</b> جدولاً بها <span class=\"r\">" + fmt(rows, 0) + "</span> صفاً" +
        (kept ? " — ويُحفظ <b>" + kept + "</b> جدولاً مرجعياً + إعدادات المنظومة" : " — بلا استثناءات") +
        (s.lastAnalyzedAtUtc ? " — وتُصفَّر بصمة آخر تحليل." : ".");
    }
    function render() {
      if (!elBody) return;
      var s = state.status;
      if (!s) { elBody.innerHTML = '<tr><td colspan="3" class="hint">لم تُقرأ حالة قاعدة البيانات بعد.</td></tr>'; return; }
      var all = scope() === "all", html = [];
      (s.groups || []).forEach(function (g) {
        var clear = all || g.scope === "data";
        html.push('<tr class="grp"><td colspan="3">' + esc(g.title) + ' <span class="hint">' + esc(g.note || "") + "</span></td></tr>");
        (g.tables || []).forEach(function (t) {
          html.push('<tr class="' + (clear ? "clear" : "keep") + '">' +
            '<td><span class="tname">' + esc(t.table) + '</span><span class="tlabel">' + esc(t.label) + "</span></td>" +
            '<td class="n">' + fmt(t.rows, 0) + "</td>" +
            "<td>" + (clear ? '<span class="dz-tag del">سيُحذف</span>' : '<span class="dz-tag keep">يُحفظ</span>') + "</td></tr>");
        });
      });
      elBody.innerHTML = html.join("") || '<tr><td colspan="3" class="hint">لا توجد جداول في قاعدة البيانات.</td></tr>';
    }

    function fail(e) {
      if (elErr) {
        elErr.hidden = false;
        elErr.textContent = "تعذّر تنفيذ العملية: " + (e && e.message ? e.message : e);
      }
    }
    function load() {
      if (state.loading) return;
      state.loading = true;
      if (elSum) elSum.textContent = "جارٍ قراءة حالة قاعدة البيانات…";
      fetch(api + "/data-status", { cache: "no-store" })
        .then(function (r) { if (!r.ok) throw new Error("HTTP " + r.status); return r.json(); })
        .then(function (s) { state.status = s; render(); updateSum(); })
        .catch(function (e) {
          if (elSum) elSum.textContent = "تعذّرت قراءة حالة البيانات — تأكد من تشغيل الخدمة واتصال قاعدة البيانات.";
          fail(e);
        })
        .then(function () { state.loading = false; });
    }
    function showResult(res) {
      if (!elDone || !res) return;
      var rows = (res.cleared || []).map(function (t) {
        return "<li><code>" + esc(t.table) + "</code> — " + esc(t.label) + ": <b>" + fmt(t.rows, 0) + "</b> صفاً</li>";
      }).join("");
      var msg;
      if (res.dryRun) {
        msg = "كان سيُحذف <b>" + fmt(res.rowsDeleted, 0) + "</b> صفاً من <b>" + res.tablesCleared + "</b> جدولاً في " +
          res.elapsedSeconds + " ثانية — أُلغيت المعاملة ولم يتغيّر أي شيء.";
      } else {
        msg = res.rowsDeleted === 0
          ? "لم تكن هناك صفوف لحذفها — قاعدة البيانات فارغة أصلاً."
          : "حُذفت <b>" + fmt(res.rowsDeleted, 0) + "</b> صفاً من <b>" + res.tablesCleared + "</b> جدولاً في " + res.elapsedSeconds + " ثانية.";
      }
      elDone.className = "dz-done" + (res.dryRun ? " warn" : "");
      elDone.innerHTML =
        "<b>" + (res.dryRun ? "🔍 معاينة بلا حذف (لم يتغيّر شيء)" : "✅ تم تنظيف البيانات") + "</b><br>" + msg +
        '<br><span class="hint">النطاق: ' + (res.scope === "all" ? "مسح شامل (بيانات + مراجع)" : "بيانات التشغيل والتحليل") + "</span>" +
        (!res.dryRun && res.analysisMarkCleared ? '<br><span class="hint">صُفِّرت بصمة آخر تحليل — لوحة المؤشرات عادت إلى حالة «لا نتائج بعد».</span>' : "") +
        (res.notes && res.notes.length ? "<ul>" + res.notes.map(function (n) { return "<li>" + esc(n) + "</li>"; }).join("") + "</ul>" : "") +
        (rows ? '<details><summary class="hint">' + (res.dryRun ? "الجداول المشمولة بالتنظيف" : "تفاصيل الجداول المُفرَّغة") +
          " (" + res.tablesCleared + ")</summary><ul>" + rows + "</ul></details>" : "");
      elDone.hidden = false;
    }
    var RUN_LABEL = "🧹 تنظيف كافة البيانات الآن";
    var DRY_LABEL = "🔍 معاينة بلا حذف";

    function run(dry) {
      if (!ready()) return;
      dry = !!dry;
      state.busy = true; gate();
      if (elErr) { elErr.hidden = true; elErr.textContent = ""; }
      if (elDone) { elDone.hidden = true; elDone.innerHTML = ""; }
      if (elRun) elRun.textContent = dry ? "⏳ جارٍ الفحص…" : "⏳ جارٍ التنظيف… لا تغلق الصفحة";
      if (elDry) elDry.disabled = true;

      fetch(api + "/reset" + (dry ? "?dryRun=true" : ""), {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ scope: scope(), confirm: typed(), resetWeeklyRule: !!(elRule && elRule.checked) })
      })
        .then(function (r) {
          return r.json().catch(function () { return {}; }).then(function (b) { return { ok: r.ok, status: r.status, body: b }; });
        })
        .then(function (res) {
          state.busy = false;
          if (elRun) elRun.textContent = RUN_LABEL;
          if (elDry) elDry.disabled = false;
          gate();
          if (!res.ok) throw new Error((res.body && res.body.message) || ("فشل التنفيذ (HTTP " + res.status + ")"));
          state.result = res.body.result || null;
          if (state.result && state.result.status) state.status = state.result.status;
          render(); updateSum(); showResult(state.result);
          toast((dry ? "🔍 " : "🧹 ") + ((res.body && res.body.message) || "تم التنفيذ بنجاح."), dry ? "warn" : "ok", 7000);
          if (!dry && typeof cfg.onDone === "function") cfg.onDone(state.result);
        })
        .catch(function (e) {
          state.busy = false;
          if (elRun) elRun.textContent = RUN_LABEL;
          if (elDry) elDry.disabled = false;
          gate(); fail(e);
        });
    }

    for (var k = 0; k < scopeInputs.length; k++) {
      on(scopeInputs[k], "change", function () { markOpts(); render(); updateSum(); });
    }
    on(elConfirm, "input", gate);
    on(elConfirm, "keydown", function (e) { if (e.key === "Enter" && ready()) { e.preventDefault(); run(false); } });
    on(elRefresh, "click", load);
    on(elRun, "click", function () { run(false); });
    on(elDry, "click", function () { run(true); });
    markOpts(); gate(); render();

    return { load: load, refresh: load, run: run, state: state, scope: scope, render: render };
  }




  /* ------------------- إعادة الرسم عند تغيّر حجم النافذة ------------------- */
  var _rz = 0;
  window.addEventListener("resize", function () {
    clearTimeout(_rz);
    _rz = setTimeout(refreshAll, 170);
  });
  /* بعد اكتمال تحميل الصفحة (الخطوط والتخطيط النهائي) يُعاد ضبط أبعاد المخططات */
  window.addEventListener("load", function () { setTimeout(refreshAll, 80); });

  /* ------------------------------ الواجهة البرمجية ------------------------- */
  return {
    version: "1.0",
    $: $, on: on,
    esc: esc, fmt: fmt, trim: trim, toast: toast,
    css: css, shade: shade, colors: PALETTE,
    chart: chart, shell: shell,
    maintenance: maintenance
  };
})();

