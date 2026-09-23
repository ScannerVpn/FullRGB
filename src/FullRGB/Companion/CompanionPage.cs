namespace FullRGB.Automation;

/// <summary>
/// The mobile companion page, embedded so the exe stays a single file. Served by
/// <see cref="ControlHub"/> at <c>http://&lt;pc-ip&gt;:9372/</c>. The page asks for the PIN
/// shown in Settings → Mobile companion, exchanges it for a session token (localStorage)
/// and then drives the same command set as every other transport.
/// </summary>
public static class CompanionPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<title>FullRGB Companion</title>
<style>
:root{--bg:#080a0f;--card:#121822;--line:#243040;--txt:#eaf1f8;--mut:#8593a4;--acc:#a487ef;--ok:#3ddc97;--warn:#ffb454}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--txt);font:15px/1.45 "Segoe UI",system-ui,sans-serif;min-height:100vh;padding:14px;max-width:560px;margin:0 auto}
h1{font-size:19px;letter-spacing:.3px;display:flex;align-items:center;gap:9px}
h1 .dot{width:9px;height:9px;border-radius:50%;background:var(--mut)}
h1 .dot.on{background:var(--ok);box-shadow:0 0 9px var(--ok)}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px;margin-top:12px}
.row{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.kv{display:flex;justify-content:space-between;gap:8px;padding:4px 0;font-size:13.5px}
.kv b{font-weight:600}
.kv span{color:var(--mut)}
button{background:#1a2331;border:1px solid var(--line);color:var(--txt);border-radius:10px;padding:9px 13px;font-size:14px;cursor:pointer;transition:.15s}
button:active{transform:scale(.97)}
button.on{background:var(--acc);border-color:var(--acc);color:#160f22;font-weight:600}
button.wide{width:100%;margin-top:8px}
button.danger{border-color:#5c2733;color:#ff8f9f}
input[type=range]{width:100%;accent-color:var(--acc)}
input[type=color]{width:44px;height:34px;border:1px solid var(--line);border-radius:9px;background:var(--card);padding:2px}
select{width:100%;background:#1a2331;color:var(--txt);border:1px solid var(--line);border-radius:10px;padding:9px;font-size:14px}
label{color:var(--mut);font-size:12.5px;display:block;margin:10px 0 5px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(96px,1fr));gap:8px}
.muted{color:var(--mut);font-size:12.5px}
#pin{position:fixed;inset:0;background:rgba(4,6,10,.82);display:flex;align-items:center;justify-content:center;padding:20px;backdrop-filter:blur(4px)}
#pin .box{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:20px;max-width:330px;width:100%}
#pin input{width:100%;letter-spacing:8px;text-align:center;font-size:24px;background:#0d111a;color:var(--txt);border:1px solid var(--line);border-radius:12px;padding:11px}
#toast{position:fixed;bottom:16px;left:50%;transform:translateX(-50%);background:#1a2331;border:1px solid var(--line);border-radius:20px;padding:8px 16px;font-size:13px;opacity:0;transition:.3s;pointer-events:none}
#toast.show{opacity:1}
.event{display:flex;gap:7px;margin-top:9px}
.event button{flex:1;padding:8px 4px;font-size:12.5px}
</style>
</head>
<body>
<h1><span class="dot" id="dot"></span>FullRGB <span class="muted">companion</span>
  <span style="margin-inline-start:auto;cursor:pointer;font-size:13px" onclick="toggleLang()">&#127760;</span></h1>

<div id="pin"><div class="box">
  <b id="pinTitle">Enter the PIN shown in the app</b>
  <p class="muted" style="margin:8px 0 12px" id="pinHint">Settings &rarr; Mobile companion &rarr; PIN</p>
  <input id="pinInput" inputmode="numeric" maxlength="6" autocomplete="off">
  <button class="wide" onclick="auth()" id="pinBtn">Connect</button>
</div></div>

<div class="card">
  <div class="kv"><span id="l_profile">Profile</span><b id="s_profile">—</b></div>
  <div class="kv"><span id="l_effect">Effect</span><b id="s_effect">—</b></div>
  <div class="kv"><span id="l_devices">Devices</span><b id="s_devices">—</b></div>
  <div class="kv"><span id="l_engine">Engine</span><b id="s_engine">—</b></div>
</div>

<div class="card">
  <label id="l_profilesHdr">Profiles</label>
  <div class="grid" id="profiles"></div>
</div>

<div class="card">
  <div class="row">
    <button id="powerBtn" onclick="togglePower()" style="flex:1">Stop effects</button>
    <button class="danger" onclick="blackout()" id="blackoutBtn">Blackout</button>
  </div>
</div>

<div class="card">
  <label id="l_effectHdr">Effect</label>
  <select id="fx" onchange="setFx()"></select>
  <label id="l_color">Color</label>
  <div class="row">
    <input type="color" id="c1" oninput="setColor()">
    <input type="color" id="c2" oninput="setColor()">
    <span class="muted" id="c1l">primary</span>
  </div>
  <label id="l_bright">Brightness <span id="bv" class="muted"></span></label>
  <input type="range" id="bri" min="0" max="100" oninput="setBri()">
  <label id="l_speed">Speed <span id="sv" class="muted"></span></label>
  <input type="range" id="spd" min="0" max="100" oninput="setSpd()">
</div>

<div class="card">
  <label id="l_events">Game events (test the GamePulse effect)</label>
  <div class="event">
    <button onclick="ev('hit')">Hit</button>
    <button onclick="ev('death')">Death</button>
    <button onclick="ev('heal')">Heal</button>
  </div>
  <label id="l_hp">HP</label>
  <input type="range" id="hp" min="0" max="100" value="80" oninput="ev('hp', this.value/100)">
</div>

<p class="muted" style="margin:14px 4px" id="foot">Powered by FullRGB — local API only.</p>
<div id="toast"></div>

<script>
const FX = {solid:"Solid",gradient:"Gradient",rainbow:"Rainbow",cycle:"Color cycle",breathing:"Breathing",
  wave:"Wave",comet:"Comet",blink:"Blink",fire:"Fire",temp:"Temperature",audio:"Music",custom:"Custom",
  spectrum:"Spectrum",scanner:"Scanner",sparkle:"Sparkle",plasma:"Plasma",ambient:"Ambient",gaming:"Gaming",
  gamepulse:"GamePulse"};
let token = localStorage.getItem("frgb_token") || "";
let lang = localStorage.getItem("frgb_lang") || "en";
let powerOn = true, lastEffect = "", quiet = 0;

const T = {
  en:{pinTitle:"Enter the PIN shown in the app",pinHint:"Settings → Mobile companion → PIN",connect:"Connect",
      profile:"Profile",effect:"Effect",devices:"Devices",engine:"Engine",profiles:"Profiles",
      stop:"Stop effects",start:"Start effects",blackout:"Blackout",effectHdr:"Effect",color:"Color",
      bright:"Brightness",speed:"Speed",events:"Game events (test the GamePulse effect)",
      hp:"HP",foot:"Powered by FullRGB — local API only.",ok:"Done",err:"Failed"},
  fa:{pinTitle:"پین نمایش‌داده‌شده در برنامه را وارد کنید",pinHint:"تنظیمات ← همراه موبایل ← پین",connect:"اتصال",
      profile:"پروفایل",effect:"افکت",devices:"دستگاه‌ها",engine:"موتور",profiles:"پروفایل‌ها",
      stop:"توقف افکت‌ها",start:"شروع افکت‌ها",blackout:"خاموشی کامل",effectHdr:"افکت",color:"رنگ",
      bright:"روشنایی",speed:"سرعت",events:"رویدادهای بازی (تست GamePulse)",
      hp:"سلامتی",foot:"FullRGB — فقط API محلی.",ok:"انجام شد",err:"ناموفق"}
};

function t(k){ return (T[lang]||T.en)[k] || T.en[k]; }
function applyLang(){
  document.documentElement.lang = lang;
  document.documentElement.dir = lang==="fa" ? "rtl" : "ltr";
  const m = T[lang]||T.en;
  for (const [id,key] of [["pinTitle","pinTitle"],["pinHint","pinHint"],["pinBtn","connect"],
      ["l_profile","profile"],["l_effect","effect"],["l_devices","devices"],["l_engine","engine"],
      ["l_profilesHdr","profiles"],["blackoutBtn","blackout"],["l_effectHdr","effectHdr"],
      ["l_color","color"],["l_bright","bright"],["l_speed","speed"],["l_events","events"],
      ["l_hp","hp"],["foot","foot"]]) { const el=document.getElementById(id); if(el) el.textContent=m[key]; }
  document.querySelector("#pin .box > b").textContent = t("pinTitle");
}
function toggleLang(){ lang = lang==="en"?"fa":"en"; localStorage.setItem("frgb_lang",lang); applyLang(); }

function toast(msg){
  const el = document.getElementById("toast");
  el.textContent = msg; el.classList.add("show");
  clearTimeout(el._h); el._h = setTimeout(()=>el.classList.remove("show"), 1600);
}

async function api(path, body){
  const sep = path.includes("?") ? "&" : "?";
  const res = await fetch("/api/" + path + sep + "token=" + encodeURIComponent(token), {
    method: body===undefined ? "GET" : "POST",
    headers: {"Content-Type":"application/json"},
    body: body===undefined ? undefined : JSON.stringify(body)
  });
  if (res.status === 401) { localStorage.removeItem("frgb_token"); token=""; showPin(); throw new Error("auth"); }
  const j = await res.json().catch(()=>({}));
  return j;
}

function showPin(){ document.getElementById("pin").style.display = "flex"; }
async function auth(){
  const pin = document.getElementById("pinInput").value.trim();
  document.getElementById("pinBtn").disabled = true;
  try {
    const res = await fetch("/api/auth", {method:"POST", headers:{"Content-Type":"application/json"}, body:JSON.stringify({pin})});
    const j = await res.json();
    if (j.ok && j.token) { token = j.token; localStorage.setItem("frgb_token", token);
      document.getElementById("pin").style.display = "none"; refresh(); toast(t("ok")); }
    else toast(t("err"));
  } catch {} finally { document.getElementById("pinBtn").disabled = false; }
}
document.getElementById("pinInput").addEventListener("keydown", e=>{ if(e.key==="Enter") auth(); });

const FXKEYS = Object.keys(FX);
function buildFx(){
  const sel = document.getElementById("fx");
  sel.innerHTML = "";
  for (const k of FXKEYS) {
    const o = document.createElement("option");
    o.value = k; o.textContent = FX[k];
    sel.appendChild(o);
  }
}
function buildProfiles(names, active){
  const box = document.getElementById("profiles");
  box.innerHTML = "";
  for (const n of names) {
    const b = document.createElement("button");
    b.textContent = n;
    if (n === active) b.classList.add("on");
    b.onclick = async ()=>{ await api("profile",{name:n}); refresh(); };
    box.appendChild(b);
  }
}
async function refresh(){
  try {
    const j = await api("status");
    if (!j.ok) return;
    document.getElementById("dot").classList.toggle("on", j.engine === "connected");
    document.getElementById("s_profile").textContent = j.profile || "—";
    document.getElementById("s_effect").textContent = FX[j.effect] || j.effect || "—";
    document.getElementById("s_devices").textContent = (j.devices ?? "0") + " · " + (j.leds ?? 0) + " LED";
    document.getElementById("s_engine").textContent = j.engine || "—";
    buildProfiles(j.profiles || [], j.profile);
    powerOn = j.power !== false;
    document.getElementById("powerBtn").textContent = powerOn ? t("stop") : t("start");
    if (quiet <= 0) {
      if (j.effect && FXKEYS.includes(j.effect)) document.getElementById("fx").value = j.effect;
      document.getElementById("bri").value = Math.round((j.brightness ?? 0.8) * 100);
      document.getElementById("spd").value = Math.round((j.speed ?? 0.5) * 100);
      if (j.color) document.getElementById("c1").value = j.color;
      if (j.color2) document.getElementById("c2").value = j.color2;
    }
    document.getElementById("bv").textContent = document.getElementById("bri").value + "%";
    document.getElementById("sv").textContent = document.getElementById("spd").value + "%";
  } catch {}
}
async function togglePower(){ await api("power",{on:!powerOn}); refresh(); }
async function blackout(){ await api("blackout",{}); toast(t("ok")); }
async function setFx(){ await api("effect",{type:document.getElementById("fx").value}); refresh(); }
let colorTimer;
async function setColor(){
  clearTimeout(colorTimer);
  colorTimer = setTimeout(async ()=>{
    await api("color",{hex:document.getElementById("c1").value});
    await api("color",{hex:document.getElementById("c2").value, secondary:true});
  }, 250);
}
let briTimer;
async function setBri(){
  document.getElementById("bv").textContent = document.getElementById("bri").value + "%";
  clearTimeout(briTimer);
  briTimer = setTimeout(async ()=>{ await api("brightness",{value:document.getElementById("bri").value/100}); }, 300);
}
let spdTimer;
async function setSpd(){
  document.getElementById("sv").textContent = document.getElementById("spd").value + "%";
  clearTimeout(spdTimer);
  spdTimer = setTimeout(async ()=>{ await api("speed",{value:document.getElementById("spd").value/100}); }, 300);
}
async function ev(name, value){ await api("event",{event:name, value:value===undefined?1:value}); }

buildFx(); applyLang();
if (token) { document.getElementById("pin").style.display = "none"; refresh(); }
setInterval(refresh, 3000);
</script>
</body>
</html>
""";
}
