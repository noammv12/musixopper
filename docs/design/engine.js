(function () {
  // Palon engine. Motion approach adapted from bloub (MIT, (c) jeremy-prt, github.com/jeremy-prt/bloub):
  // pure sample(t), radial-profile body, eyes on a sphere, easeOutQuint morphs, loopNoise gaze drift, blink schedule.
  var TAU = Math.PI * 2, N = 64;
  var clamp = function (v, a, b) { a = a == null ? 0 : a; b = b == null ? 1 : b; return v < a ? a : v > b ? b : v; };
  var lerp = function (a, b, t) { return a + (b - a) * t; };
  var ease = function (t) { return 1 - Math.pow(1 - t, 5); };
  var r2 = function (v) { return Math.round(v * 100) / 100; };
  var noise = function (t, p, s) { var a = t / p * TAU; return 0.55 * Math.sin(a + s) + 0.3 * Math.sin(2 * a + s * 1.7 + 1.1) + 0.15 * Math.sin(3 * a + s * 2.3 + 2.4); };
  var COS = [], SIN = [];
  for (var i = 0; i < N; i++) { COS.push(Math.cos(i / N * TAU)); SIN.push(Math.sin(i / N * TAU)); }
  // blink schedule, deterministic (mulberry32)
  var BL = (function () { var a = 0x5eed, rnd = function () { a = (a + 0x6d2b79f5) >>> 0; var t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
    var o = [], t = 1.2; while (t < 4000) { o.push(t); t += 2.2 + rnd() * 2.8; if (rnd() < 0.18) { o.push(t); t += 0.26; } } return o; })();
  function blinkLid(t) {
    var p = ((t % 4000) + 4000) % 4000, lo = 0, hi = BL.length - 1;
    while (lo < hi) { var m = (lo + hi + 1) >> 1; if (BL[m] <= p) lo = m; else hi = m - 1; }
    var k = (p - BL[lo]) / 0.19; if (k < 0 || k > 1) return 1;
    return k < 0.42 ? 1 - k / 0.42 : ease((k - 0.42) / 0.58);
  }
  var eye = function (w, h, o) { o = o || {}; return { w: w, h: h, open: o.open == null ? 1 : o.open, tilt: o.tilt || 0, bend: o.bend || 0 }; };
  // each mood: head orientation (degrees), eye split on sphere, two eyes, body squash and live motion
  var MOODS = {
    idle:   { gaze: [-1, -5.5, -13], split: 15.5, eyes: [eye(.186, .412), eye(.186, .412)], wander: 1 },
    listen: { gaze: [1, -1, -6], split: 16, eyes: [eye(.215, .46), eye(.215, .46)], wander: .45, sy: 1.012 },
    think:  { gaze: [-21, 12, -5], split: 15, eyes: [eye(.19, .25, { tilt: -6 }), eye(.19, .31, { tilt: 3 })], wander: .5 },
    talk:   { gaze: [0, -4, -11], split: 15.5, eyes: [eye(.19, .4), eye(.19, .4)], wander: .6 },
    happy:  { gaze: [0, -3, -9], split: 17, eyes: [eye(.26, .1, { bend: .06 }), eye(.26, .1, { bend: .06 })], wander: .35 }
  };
  var MORPH = 0.5;
  function mood(id, t) {
    var m = MOODS[id], g = m.gaze;
    var p = { yaw: g[0], pitch: g[1], roll: g[2], split: m.split, eyes: m.eyes.map(function (e) { return { w: e.w, h: e.h, open: e.open, tilt: e.tilt, bend: e.bend }; }),
      sx: 1, sy: m.sy || 1, offY: 0, wander: m.wander, hair: 0 };
    if (id === 'talk') {
      // syllable rhythm: layered sines, never repeats visibly
      var s = 0.5 + 0.5 * Math.sin(t * 11.3) * (0.6 + 0.4 * Math.sin(t * 2.7));
      var s2 = Math.max(0, Math.sin(t * 5.9 + 1));
      p.eyes.forEach(function (e) { e.h *= 1 + 0.07 * s; e.w *= 1 - 0.025 * s; });
      p.sy *= 1 + 0.016 * s2; p.sx *= 1 - 0.008 * s2; p.offY = -0.014 * s2; p.pitch += 2.5 * s2; p.hair = 3 * s2;
    }
    if (id === 'happy') {
      var c = (t % 0.95) / 0.95, hop = Math.sin(Math.PI * c); hop = hop * hop;
      p.offY = -0.07 * hop; p.sy *= 1 + 0.025 * hop - 0.03 * Math.pow(1 - hop, 6); p.sx *= 1 - 0.015 * hop + 0.025 * Math.pow(1 - hop, 6);
      p.hair = 10 * Math.cos(TAU * c) * -1; p.roll += 4 * Math.sin(t * 2.1);
    }
    if (id === 'think') { p.yaw += 5 * Math.sin(t * 0.9); p.pitch += 3 * Math.sin(t * 0.6 + 1); }
    if (id === 'listen') { p.roll += 2 * Math.sin(t * 1.1); var nod = Math.max(0, Math.sin(t * 2.2)); p.pitch -= 3 * nod * nod; }
    return p;
  }
  function blendPose(a, b, k) {
    var o = {}; ['yaw', 'pitch', 'roll', 'split', 'sx', 'sy', 'offY', 'wander', 'hair'].forEach(function (f) { o[f] = lerp(a[f], b[f], k); });
    o.eyes = a.eyes.map(function (e, i) { var f = b.eyes[i]; return { w: lerp(e.w, f.w, k), h: lerp(e.h, f.h, k), open: lerp(e.open, f.open, k), tilt: lerp(e.tilt, f.tilt, k), bend: lerp(e.bend, f.bend, k) }; });
    return o;
  }
  function create(id, now) { return { cur: id, prev: null, tCur: now || 0, tPrev: 0, frozen: null, blinkAt: -10, seed: 0 }; }
  function composed(s, now) {
    var p = mood(s.cur, now - s.tCur), since = now - s.tCur;
    if (since >= MORPH || (!s.prev && !s.frozen)) return p;
    var from = s.frozen || mood(s.prev, now - s.tPrev);
    return blendPose(from, p, ease(clamp(since / MORPH)));
  }
  function setMood(s, id, now) {
    if (id === s.cur || !MOODS[id]) return;
    s.frozen = (s.prev && now - s.tCur < MORPH) ? composed(s, now) : null;
    s.prev = s.cur; s.tPrev = s.tCur; s.cur = id; s.tCur = now; s.blinkAt = now;
  }
  // tangent frames of two eyes on a unit sphere (bloub face.ts eyePoses)
  function spin(u, v, a) { var c = Math.cos(a), s = Math.sin(a); return [[u[0] * c + v[0] * s, u[1] * c + v[1] * s, u[2] * c + v[2] * s], [v[0] * c - u[0] * s, v[1] * c - u[1] * s, v[2] * c - u[2] * s]]; }
  function eyePoses(yaw, pitch, roll, split) {
    var d = Math.PI / 180, f = [0, 0, 1], r = [1, 0, 0], dn = [0, 1, 0], q;
    q = spin(f, r, yaw * d); f = q[0]; r = q[1];
    q = spin(dn, f, pitch * d); dn = q[0]; f = q[1];
    q = spin(r, dn, roll * d); r = q[0]; dn = q[1];
    return [-1, 1].map(function (side) { var e = spin(f, r, split * side * d); return { x: e[0][0], y: e[0][1], a: e[1][0], b: e[1][1], c: dn[0], d: dn[1], z: e[0][2] }; });
  }
  function catmull(P) {
    var n = P.length, d = 'M' + r2(P[0][0]) + ' ' + r2(P[0][1]);
    for (var i = 0; i < n; i++) {
      var p0 = P[(i - 1 + n) % n], p1 = P[i], p2 = P[(i + 1) % n], p3 = P[(i + 2) % n];
      d += 'C' + r2(p1[0] + (p2[0] - p0[0]) / 6) + ' ' + r2(p1[1] + (p2[1] - p0[1]) / 6) + ' ' + r2(p2[0] - (p3[0] - p1[0]) / 6) + ' ' + r2(p2[1] - (p3[1] - p1[1]) / 6) + ' ' + r2(p2[0]) + ' ' + r2(p2[1]);
    }
    return d + 'Z';
  }
  // eye outline: capsule sampled at EP points evenly by arc length, starting at top centre, so any two
  // eye shapes have matching points and morph by plain interpolation (same idea as bloub's radial profiles)
  var EP = 44;
  function eyeOutline(w, h, bend) {
    var hw = Math.max(w, .01) / 2, hh = Math.max(h, .01) / 2, r = Math.min(hw, hh), ex = hw - r, ey = hh - r;
    var segs = [[0, ex], [1, Math.PI / 2 * r], [0, 2 * ey], [1, Math.PI / 2 * r], [0, 2 * ex], [1, Math.PI / 2 * r], [0, 2 * ey], [1, Math.PI / 2 * r], [0, ex]];
    var total = 0; segs.forEach(function (s) { total += s[1]; });
    var out = [];
    for (var i = 0; i < EP; i++) {
      var d = i / EP * total, k = 0;
      while (k < segs.length - 1 && d > segs[k][1]) { d -= segs[k][1]; k++; }
      var f = segs[k][1] > 0 ? d / segs[k][1] : 0, x, y, a;
      // walk clockwise: top edge -> TR corner -> right edge -> BR -> bottom -> BL -> left -> TL -> top
      switch (k) {
        case 0: x = f * ex; y = -hh; break;
        case 1: a = -Math.PI / 2 + f * Math.PI / 2; x = ex + r * Math.cos(a); y = -ey + r * Math.sin(a); break;
        case 2: x = hw; y = -ey + f * 2 * ey; break;
        case 3: a = f * Math.PI / 2; x = ex + r * Math.cos(a); y = ey + r * Math.sin(a); break;
        case 4: x = ex - f * 2 * ex; y = hh; break;
        case 5: a = Math.PI / 2 + f * Math.PI / 2; x = -ex + r * Math.cos(a); y = ey + r * Math.sin(a); break;
        case 6: x = -hw; y = ey - f * 2 * ey; break;
        case 7: a = Math.PI + f * Math.PI / 2; x = -ex + r * Math.cos(a); y = -ey + r * Math.sin(a); break;
        default: x = -ex + f * ex; y = -hh;
      }
      var u = x / hw; y -= bend * (1 - u * u) * 1.7; out.push([x, y]);
    }
    return out;
  }
  function sample(s, now, R) {
    R = R || 100;
    var p = composed(s, now), w = p.wander;
    var lid = blinkLid(now + s.seed);
    var fk = clamp((now - s.blinkAt) / 0.22); if (fk < 1) lid = Math.min(lid, Math.abs(fk * 2 - 1));
    var yaw = p.yaw + (noise(now, 11.3, .4) * 5.5 + noise(now, 3.7, 2.1) * 1.6) * w;
    var pitch = p.pitch + (noise(now, 9.1, 1.3) * 4 + noise(now, 4.3, .7) * 1.2) * w;
    var roll = p.roll + noise(now, 13.7, 3.2) * 2.2 * w;
    var breath = 1 + Math.sin(now / 3.6 * TAU) * 0.006;
    var ox = noise(now, 7.9, 1.9) * 0.006, oy = p.offY + noise(now, 5.3, .3) * 0.007;
    var sx = p.sx, sy = p.sy * breath;
    // body: radial profile, a circle whose squash is anchored at the bottom (feels grounded)
    var B = [];
    for (var i = 0; i < N; i++) { var x = COS[i] * sx, y = SIN[i] * sy + (1 - sy); B.push([(x + ox) * R, (y + oy) * R]); }
    // eyes
    var E = eyePoses(yaw, pitch, roll, p.split), eyes = [];
    for (var k = 0; k < 2; k++) {
      var e = E[k], cfg = p.eyes[k], ph = cfg.tilt * Math.PI / 180, cp = Math.cos(ph), sp = Math.sin(ph);
      var ax = e.a * cp + e.c * sp, ay = e.b * cp + e.d * sp, bx = -e.a * sp + e.c * cp, by = -e.b * sp + e.d * cp;
      var sq = 0.06 + 0.94 * clamp(Math.min(lid, cfg.open)) ;
      if (cfg.bend > 0.02) sq = 1 - (1 - sq) * 0.35; // happy arcs barely blink
      var cx = (e.x * sx + ox) * R, cy = (e.y * sy + (1 - sy) + oy) * R;
      var pts = eyeOutline(cfg.w * R, cfg.h * R, cfg.bend * R).map(function (q) { return [cx + q[0] * ax + q[1] * bx, cy + (q[0] * ay + q[1] * by) * sq]; });
      eyes.push(catmull(pts));
    }
    // hair tuft: three tapered strands rooted just inside the top of the body, lagging sway
    var sway = (noise(now, 4.7, .9) * 6 + noise(now, 2.3, 2.2) * 2) + p.hair - roll * 0.6 + yaw * 0.12;
    var tx = (ox + 0.04 + Math.sin(yaw * Math.PI / 180) * 0.12) * R, ty = (-sy + (1 - sy) + oy + 0.16) * R;
    var H = [[-16, .28, .17, 1], [6, .41, .19, 1], [28, .26, .15, 1]].map(function (st, j) {
      var ang = (st[0] + sway * (0.8 + j * 0.25)) * Math.PI / 180, L = st[1] * R, wd = st[2] * R, cv = st[3];
      var dx = Math.sin(ang), dy = -Math.cos(ang), nx = -dy, ny = dx, bx0 = tx + (j - 1) * 0.045 * R;
      var tipx = bx0 + dx * L + nx * cv * 0.42 * L, tipy = ty + dy * L + ny * cv * 0.42 * L;
      var l1x = bx0 - nx * wd / 2, l1y = ty - ny * wd / 2, r1x = bx0 + nx * wd / 2, r1y = ty + ny * wd / 2;
      var mx = bx0 + dx * L * 0.55 + nx * cv * 0.2 * L, my = ty + dy * L * 0.55 + ny * cv * 0.2 * L;
      return 'M' + r2(l1x) + ' ' + r2(l1y) + 'Q' + r2(mx - nx * wd * .45) + ' ' + r2(my - ny * wd * .45) + ' ' + r2(tipx) + ' ' + r2(tipy) +
        'Q' + r2(mx + nx * wd * .55) + ' ' + r2(my + ny * wd * .55) + ' ' + r2(r1x) + ' ' + r2(r1y) + 'Z';
    }).join('');
    return { body: catmull(B), eyeL: eyes[0], eyeR: eyes[1], hair: H };
  }
  return { create: create, setMood: setMood, sample: sample, moods: Object.keys(MOODS), MORPH: MORPH };
})()
