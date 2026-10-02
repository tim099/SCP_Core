// 區塊職責：雕刻**渲染設定檔**（render profile）—— 長期保存、可多組、可切換（TASK-0377，Tim 2026-10-02）。
// 物理意義：兩層，各自可以有很多組、各自記住「現在用哪一組」：
//            · 共用：<資料根>/Sculpture/render_profiles/<名>.json ＋ Sculpture/render_settings.json（{"active":名}）
//            · 個人：<信件夾>/<P>/sculpture/render_profiles/<名>.json ＋ <P>/sculpture/render_settings.json
//          一次渲染的參數由下往上疊（**只蓋有寫的欄位**，沒寫的沿用下一層）：
//            內建預設（SCP_SculptRenderParams 的初值）→ 共用作用中 → persona 作用中 →（呼叫端再疊：展品預設 → CLI）
//          ⇒ 一份個人設定可以只寫「換一張天空」，其餘跟共用走。
// 數值影響：⚠ 設定檔裡**不認得的鍵 ＝ 錯誤**（不是略過）—— `"yaww": 90` 被略過時，畫出來的圖跟「設定生效了」
//          只差在角度，而沒有任何一層會叫。同理數值越界、型別不對都是錯誤，⛔ 不夾回合法範圍。
//          ⚠ skybox 的相對路徑以 `<資料根>/Sculpture/skyboxes/` 為基準（設定檔跨專案搬也成立）；
//          "builtin" ＝ 渲染器內建天空、"none" ＝ 純色背景。
//          ⚠ 地板貼圖的相對路徑以 `<資料根>/Sculpture/floors/` 為基準；"builtin" ＝ 內建量尺網格；檔案不存在 ⇒ 錯誤。
// 設定檔格式（全部選填）：
//   { "camera": { "projection": "orthographic|perspective", "yaw": 45, "pitch": 30, "roll": 0,
//                 "target": [x,y,z] | null, "eye": [x,y,z] | null, "distance": d | null, "fov": 45, "zoom": z | null,
//                 "fit_upscale": false },
//     "lights": [ { "dir": [-1,-1,-1], "color": "#ffffff", "intensity": 1, "shadow": true }, … ],
//     "ambient": 0.4, "ao": true, "shadow": true,
//     "skybox": { "path": "belfast_sunset_puresky_2k.jpg" | "builtin" | "none" | "<絕對路徑>", "yaw": 0, "tilt": 0 },
//     "floor": { "enabled": true, "z": 0, "full_grid": false, "margin": 24,
//                "texture": "builtin" | "stone_tiles_02_diff_2k.jpg" | "<絕對路徑>", "tile_size": 16, "color": "#ffffff", "fade": 0.15 },
//     "background": "#0f172a", "width": 1024, "height": 1024 }
//   ⚠ camera 的 target／eye／distance／zoom 寫 null ＝ **明確改回自動**（蓋掉下層的值），不寫 ＝ 沿用下層。
//   ⚠ floor 的疊法：每個欄位各自沿用下層；**沒寫 enabled 的層不改開關**（例：共用層開了地板、個人層只寫 texture
//     ⇒ 照樣開著、換了貼圖；反過來共用層沒開、個人層只寫 texture ⇒ 照樣沒有地板，但貼圖記住了，再上一層開了就用它）。
//     內建預設 ＝ 沒有地板。enabled=false ⇒ Floor＝null。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Sculpture
{
    public enum SCP_SculptProfileScope { Shared, Persona }

    public static class SCP_SculptRenderProfiles
    {
        public const string DefaultName = "default";
        public const string ProfilesDirName = "render_profiles";
        public const string SettingsFileName = "render_settings.json";
        public const string SkyboxesDirName = "skyboxes";
        public const string SkyboxBuiltin = "builtin";
        public const string FloorsDirName = "floors";
        public const string FloorBuiltin = "builtin";

        // ───────────────────────────── 路徑 ─────────────────────────────

        public static string SculptureDir(SCP_DataRoot iData) => iData.Value.TrimEnd('/', '\\').Replace('\\', '/') + "/Sculpture";
        public static string SkyboxesDir(SCP_DataRoot iData) => SculptureDir(iData) + "/" + SkyboxesDirName;
        public static string FloorsDir(SCP_DataRoot iData) => SculptureDir(iData) + "/" + FloorsDirName;

        /// <summary>skyboxes/ 的兄弟資料夾 floors/（TryApply 只拿到 skybox 基準時用）。</summary>
        static string FloorsDirFromSkyBase(string iSkyBase)
        {
            string? aParent = Path.GetDirectoryName(iSkyBase.TrimEnd('/', '\\'));
            return ((aParent ?? "") + "/" + FloorsDirName).Replace('\\', '/');
        }

        /// <summary>某一層的根目錄（共用 ＝ Sculpture/；個人 ＝ letters/&lt;P&gt;/sculpture/）。</summary>
        public static string ScopeDir(SCP_SculptProfileScope iScope, SCP_DataRoot iData, SCP_LettersRoot? iLetters, string? iPersona)
        {
            if (iScope == SCP_SculptProfileScope.Shared) return SculptureDir(iData);
            if (iLetters == null || !SCP_RegisteredMail.IsValidPersonaName(iPersona))
                throw new ArgumentException("個人設定要 persona 與信件夾根");
            return SCP_LettersPaths.PersonaDir(iLetters.Value, iPersona!.Trim()) + "/sculpture";
        }

        public static string ProfilePath(string iScopeDir, string iName) => iScopeDir + "/" + ProfilesDirName + "/" + iName + ".json";
        public static string SettingsPath(string iScopeDir) => iScopeDir + "/" + SettingsFileName;

        /// <summary>設定名：小寫英數、底線、連字號，1..40 字（當檔名用，⛔ 不准帶路徑字元）。</summary>
        public static bool IsValidName(string? iName)
        {
            if (string.IsNullOrEmpty(iName) || iName!.Length > 40) return false;
            foreach (char c in iName)
                if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            return true;
        }

        // ───────────────────────────── 讀寫 ─────────────────────────────

        public static List<string> List(string iScopeDir)
        {
            var aOut = new List<string>();
            string aDir = iScopeDir + "/" + ProfilesDirName;
            if (!Directory.Exists(aDir)) return aOut;
            foreach (string f in Directory.GetFiles(aDir, "*.json"))
                aOut.Add(Path.GetFileNameWithoutExtension(f));
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        /// <summary>讀一份設定；不存在 ⇒ false 且 oError 空；壞檔 ⇒ false 且 oError 說原因。</summary>
        public static bool TryLoad(string iScopeDir, string iName, out SCP_JsonData oProfile, out string oError)
        {
            oProfile = SCP_JsonData.NewObject(); oError = "";
            string aPath = ProfilePath(iScopeDir, iName);
            if (!File.Exists(aPath)) return false;
            try { oProfile = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8)); }
            catch (Exception e) { oError = "設定檔讀不了（" + aPath + "）：" + e.Message; return false; }
            if (oProfile.Type != SCP_JsonType.Object) { oError = "設定檔不是 JSON 物件：" + aPath; return false; }
            return true;
        }

        public static void Save(string iScopeDir, string iName, SCP_JsonData iProfile)
            => SCP_CmdPayload.WriteAtomic(ProfilePath(iScopeDir, iName), iProfile.ToJson(true) + "\n");

        public static bool Delete(string iScopeDir, string iName)
        {
            string aPath = ProfilePath(iScopeDir, iName);
            if (!File.Exists(aPath)) return false;
            File.Delete(aPath);
            return true;
        }

        /// <summary>這一層現在用哪一組（沒設 ⇒ null）。</summary>
        public static string? GetActive(string iScopeDir)
        {
            string aPath = SettingsPath(iScopeDir);
            if (!File.Exists(aPath)) return null;
            try
            {
                var aJ = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                return aJ.TryGetString("active", out string a) && a.Length > 0 ? a : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>設定這一層用哪一組；null ＝ 不用（個人層 ⇒ 全跟共用走）。</summary>
        public static void SetActive(string iScopeDir, string? iName)
        {
            var aJ = SCP_JsonData.NewObject();
            aJ.Set("active", iName == null ? SCP_JsonData.NewNull() : SCP_JsonData.NewString(iName));
            SCP_CmdPayload.WriteAtomic(SettingsPath(iScopeDir), aJ.ToJson(true) + "\n");
        }

        // ───────────────────────────── 疊加 ─────────────────────────────

        /// <summary>
        /// 由內建預設開始，疊上共用作用中、再疊上 persona 作用中（有給 persona 才疊）。
        /// <paramref name="oLayers"/> 是給人看的出處（印在回傳裡：這張圖用的是哪幾層）。
        /// <para>⚠ 作用中指向一份**不存在**的設定 ⇒ 錯誤（⛔ 不默默當成沒設）。共用層完全沒設 ⇒ 只用內建預設，不是錯。</para>
        /// </summary>
        public static bool TryResolve(SCP_DataRoot iData, SCP_LettersRoot? iLetters, string? iPersona,
                                      out SCP_SculptRenderParams oParams, out List<string> oLayers, out string oError)
        {
            oParams = new SCP_SculptRenderParams();
            oLayers = new List<string> { "內建預設" };
            oError = "";
            string aSkyBase = SkyboxesDir(iData);

            string aShared = SculptureDir(iData);
            string? aSharedActive = GetActive(aShared);
            if (aSharedActive != null && !ApplyNamed(aShared, aSharedActive, "共用", oParams, aSkyBase, oLayers, out oError)) return false;

            if (iLetters != null && SCP_RegisteredMail.IsValidPersonaName(iPersona))
            {
                string aMine = ScopeDir(SCP_SculptProfileScope.Persona, iData, iLetters, iPersona);
                string? aMineActive = GetActive(aMine);
                if (aMineActive != null && !ApplyNamed(aMine, aMineActive, iPersona!.Trim() + " 個人", oParams, aSkyBase, oLayers, out oError)) return false;
            }
            return true;
        }

        static bool ApplyNamed(string iScopeDir, string iName, string iWho, SCP_SculptRenderParams ioP, string iSkyBase,
                               List<string> ioLayers, out string oError)
        {
            if (!TryLoad(iScopeDir, iName, out SCP_JsonData aProfile, out oError))
            {
                if (oError.Length == 0)
                    oError = iWho + "作用中的渲染設定 `" + iName + "` 不存在（" + ProfilePath(iScopeDir, iName) + "）"
                             + " ⇒ 先 `op=render-profile sub=use` 換一組，或 `sub=set` 建它";
                return false;
            }
            if (!TryApply(aProfile, ioP, iSkyBase, out oError)) { oError = iWho + "設定 `" + iName + "`：" + oError; return false; }
            ioLayers.Add(iWho + "設定 `" + iName + "`");
            return true;
        }

        static readonly HashSet<string> s_TopKeys = new HashSet<string>(StringComparer.Ordinal)
        { "camera", "lights", "ambient", "ao", "shadow", "skybox", "floor", "background", "width", "height" };
        static readonly HashSet<string> s_CameraKeys = new HashSet<string>(StringComparer.Ordinal)
        { "projection", "yaw", "pitch", "roll", "target", "eye", "distance", "fov", "zoom", "fit_upscale" };
        static readonly HashSet<string> s_FloorKeys = new HashSet<string>(StringComparer.Ordinal)
        { "enabled", "z", "full_grid", "margin", "margin_ratio", "texture", "tile_size", "color", "fade" };
        /// <summary>CLI 的扁平地板鍵 → 設定檔 floor 物件裡的鍵。</summary>
        static readonly Dictionary<string, string> s_FloorFlat = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["floor"] = "enabled", ["floor_z"] = "z", ["floor_full_grid"] = "full_grid", ["floor_margin"] = "margin", ["floor_margin_ratio"] = "margin_ratio",
            ["floor_texture"] = "texture", ["floor_tile"] = "tile_size", ["floor_color"] = "color", ["floor_fade"] = "fade",
        };
        /// <summary>CLI 認得的扁平地板鍵（`render-profile set` 與 view 的一次性參數同一份）。</summary>
        public static IReadOnlyCollection<string> FloorFlatKeys => s_FloorFlat.Keys;
        public const double FloorZMin = -64, FloorZMax = 320, FloorMarginMax = 256, FloorMarginRatioMax = 4, FloorTileMin = 0.25, FloorTileMax = 4096, FloorFadeMax = 0.5;
        static readonly HashSet<string> s_LightKeys = new HashSet<string>(StringComparer.Ordinal)
        { "dir", "color", "intensity", "shadow" };
        static readonly HashSet<string> s_SkyKeys = new HashSet<string>(StringComparer.Ordinal) { "path", "yaw", "tilt" };

        /// <summary>把一份設定疊到 <paramref name="ioP"/> 上（只蓋有寫的欄位）。驗證失敗 ⇒ false，ioP 可能已被部分修改。
        /// 地板貼圖的相對路徑以 skybox 基準的兄弟資料夾 `floors/` 為準。</summary>
        public static bool TryApply(SCP_JsonData iProfile, SCP_SculptRenderParams ioP, string iSkyBase, out string oError)
            => TryApply(iProfile, ioP, iSkyBase, FloorsDirFromSkyBase(iSkyBase), out oError);

        public static bool TryApply(SCP_JsonData iProfile, SCP_SculptRenderParams ioP, string iSkyBase, string iFloorBase, out string oError)
        {
            oError = "";
            if (!CheckKeys(iProfile, s_TopKeys, "", out oError)) return false;
            try
            {
                var c = iProfile["camera"];
                if (c.Exists)
                {
                    if (c.Type != SCP_JsonType.Object) { oError = "camera 要是物件"; return false; }
                    if (!CheckKeys(c, s_CameraKeys, "camera.", out oError)) return false;
                    if (c["projection"].Exists)
                    {
                        string aProj = c["projection"].AsString().Trim().ToLowerInvariant();
                        if (aProj == "orthographic" || aProj == "ortho") ioP.Projection = SCP_SculptProjection.Orthographic;
                        else if (aProj == "perspective" || aProj == "persp") ioP.Projection = SCP_SculptProjection.Perspective;
                        else { oError = "camera.projection 只認 orthographic／perspective：" + aProj; return false; }
                    }
                    if (c["yaw"].Exists) ioP.YawDeg = Finite(c["yaw"], "camera.yaw");
                    if (c["pitch"].Exists) ioP.PitchDeg = Range(c["pitch"], "camera.pitch", -90, 90);
                    if (c["roll"].Exists) ioP.RollDeg = Finite(c["roll"], "camera.roll");
                    if (c["fov"].Exists) ioP.FovDeg = Range(c["fov"], "camera.fov", 1, 170);
                    if (c["target"].Exists)
                    {
                        if (c["target"].IsNull) { ioP.TargetX = ioP.TargetY = ioP.TargetZ = null; }
                        else { double[] t = Vec3(c["target"], "camera.target"); ioP.TargetX = t[0]; ioP.TargetY = t[1]; ioP.TargetZ = t[2]; }
                    }
                    if (c["eye"].Exists)
                    {
                        if (c["eye"].IsNull) { ioP.EyeX = ioP.EyeY = ioP.EyeZ = null; }
                        else { double[] e = Vec3(c["eye"], "camera.eye"); ioP.EyeX = e[0]; ioP.EyeY = e[1]; ioP.EyeZ = e[2]; }
                    }
                    if (c["distance"].Exists) ioP.Distance = c["distance"].IsNull ? (double?)null : Positive(c["distance"], "camera.distance");
                    if (c["zoom"].Exists) ioP.Zoom = c["zoom"].IsNull ? (double?)null : Positive(c["zoom"], "camera.zoom");
                    if (c["fit_upscale"].Exists) ioP.FitUpscale = c["fit_upscale"].AsBool();
                }

                var l = iProfile["lights"];
                if (l.Exists)
                {
                    if (l.Type != SCP_JsonType.Array) { oError = "lights 要是陣列"; return false; }
                    var aLights = new List<SCP_SculptLight>();
                    int i = 0;
                    foreach (SCP_JsonData aL in l)
                    {
                        string aAt = "lights[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                        if (aL.Type != SCP_JsonType.Object) { oError = aAt + " 要是物件"; return false; }
                        if (!CheckKeys(aL, s_LightKeys, aAt + ".", out oError)) return false;
                        var aLight = new SCP_SculptLight();
                        if (aL["dir"].Exists)
                        {
                            double[] d = Vec3(aL["dir"], aAt + ".dir");
                            if (d[0] * d[0] + d[1] * d[1] + d[2] * d[2] < 1e-12) { oError = aAt + ".dir 長度是 0"; return false; }
                            aLight.DirX = d[0]; aLight.DirY = d[1]; aLight.DirZ = d[2];
                        }
                        if (aL["color"].Exists)
                        {
                            if (!TryHexColor(aL["color"].AsString(), out byte r, out byte g, out byte b))
                            { oError = aAt + ".color 要 #RRGGBB：" + aL["color"].AsString(); return false; }
                            aLight.R = r; aLight.G = g; aLight.B = b;
                        }
                        if (aL["intensity"].Exists) aLight.Intensity = Range(aL["intensity"], aAt + ".intensity", 0, 100);
                        if (aL["shadow"].Exists) aLight.CastShadow = aL["shadow"].AsBool();
                        aLights.Add(aLight);
                        i++;
                    }
                    if (aLights.Count > SCP_SculptRenderParams.MaxLights)
                    { oError = "lights 最多 " + SCP_SculptRenderParams.MaxLights + " 盞（給了 " + aLights.Count + "）"; return false; }
                    ioP.Lights = aLights;
                }

                if (iProfile["ambient"].Exists) ioP.Ambient = Range(iProfile["ambient"], "ambient", 0, 1);
                if (iProfile["ao"].Exists) ioP.AmbientOcclusion = iProfile["ao"].AsBool();
                if (iProfile["shadow"].Exists) ioP.Shadow = iProfile["shadow"].AsBool();
                if (iProfile["width"].Exists) ioP.Width = (int)Range(iProfile["width"], "width", 16, 8192);
                if (iProfile["height"].Exists) ioP.Height = (int)Range(iProfile["height"], "height", 16, 8192);
                if (iProfile["background"].Exists)
                {
                    if (!TryHexColor(iProfile["background"].AsString(), out byte r, out byte g, out byte b))
                    { oError = "background 要 #RRGGBB：" + iProfile["background"].AsString(); return false; }
                    ioP.BgR = r; ioP.BgG = g; ioP.BgB = b;
                }

                var s = iProfile["skybox"];
                if (s.Exists)
                {
                    if (s.Type != SCP_JsonType.Object) { oError = "skybox 要是物件（{\"path\":…,\"yaw\":…}）"; return false; }
                    if (!CheckKeys(s, s_SkyKeys, "skybox.", out oError)) return false;
                    if (s["path"].Exists && !TryResolveSkybox(s["path"].AsString(), iSkyBase, out ioP.Skybox, out oError)) return false;
                    if (s["yaw"].Exists) ioP.SkyboxYawDeg = Finite(s["yaw"], "skybox.yaw");
                    if (s["tilt"].Exists) ioP.SkyboxTiltDeg = Range(s["tilt"], "skybox.tilt", -89, 89);
                }

                var fl = iProfile["floor"];
                if (fl.Exists && !TryApplyFloor(fl, ioP, iFloorBase, out oError)) return false;
            }
            catch (BadValue e) { oError = e.Message; return false; }
            catch (SCP_JsonTypeException e) { oError = "型別不對：" + e.Message; return false; }
            return true;
        }

        /// <summary>
        /// skybox 字串 → 渲染參數：builtin ⇒ null、none ⇒ <see cref="SCP_SculptRenderParams.SkyboxNone"/>、
        /// 相對路徑以 skyboxes/ 為基準。檔案不存在 ⇒ 錯誤（⛔ 不默默退回內建天空）。
        /// </summary>
        public static bool TryResolveSkybox(string iValue, string iSkyBase, out string? oSkybox, out string oError)
        {
            oSkybox = null; oError = "";
            string v = (iValue ?? "").Trim();
            if (v.Length == 0 || v.Equals(SkyboxBuiltin, StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals(SCP_SculptRenderParams.SkyboxNone, StringComparison.OrdinalIgnoreCase)) { oSkybox = SCP_SculptRenderParams.SkyboxNone; return true; }
            string aPath = Path.IsPathRooted(v) ? v : Path.Combine(iSkyBase, v);
            aPath = aPath.Replace('\\', '/');
            if (!File.Exists(aPath)) { oError = "skybox 圖不存在：" + aPath; return false; }
            oSkybox = aPath;
            return true;
        }

        // ───────────────────────────── 地板（疊層草稿） ─────────────────────────────
        //
        // 區塊職責：地板欄位的逐層疊加。
        // 物理意義：契約上 Floor＝null ＝ 沒有地板，但「這層只換貼圖、開關沿用下層」需要在**關著**的時候也記得欄位值
        //          （共用層記了貼圖、個人層才開）⇒ 每個參數物件旁掛一份草稿（ConditionalWeakTable：跟著參數物件生滅、不改契約形狀）。
        //          草稿 ＝ 開關 ＋ 一份完整欄位；每層只改有寫的欄位，最後 Floor ＝ 開 ? 草稿的複本 : null。
        // 數值影響：有人繞過本檔直接改了 ioP.Floor（例：呼叫端自己 new 一份）⇒ 下一次疊加以那一份為準（不讓舊草稿蓋掉它）。

        sealed class FloorDraft
        {
            public bool Enabled;
            public SCP_SculptFloor Values = new SCP_SculptFloor();
            /// <summary>上一次寫回 ioP.Floor 的那一個物件（用來判斷有沒有人在外面改過）。</summary>
            public SCP_SculptFloor? Materialized;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SCP_SculptRenderParams, FloorDraft> s_FloorDrafts
            = new System.Runtime.CompilerServices.ConditionalWeakTable<SCP_SculptRenderParams, FloorDraft>();

        static SCP_SculptFloor CloneFloor(SCP_SculptFloor f) => new SCP_SculptFloor
        {
            Z = f.Z, FullGrid = f.FullGrid, Margin = f.Margin, MarginRatio = f.MarginRatio, Texture = f.Texture, TileSize = f.TileSize,
            R = f.R, G = f.G, B = f.B, Fade = f.Fade,
        };

        static FloorDraft DraftOf(SCP_SculptRenderParams iP)
        {
            if (s_FloorDrafts.TryGetValue(iP, out FloorDraft? d) && d != null)
            {
                if (!ReferenceEquals(iP.Floor, d.Materialized))
                {
                    if (iP.Floor != null) { d.Enabled = true; d.Values = CloneFloor(iP.Floor); }
                    else d.Enabled = false;
                    d.Materialized = iP.Floor;
                }
                return d;
            }
            d = new FloorDraft
            {
                Enabled = iP.Floor != null,
                Values = iP.Floor != null ? CloneFloor(iP.Floor) : new SCP_SculptFloor(),
                Materialized = iP.Floor,
            };
            s_FloorDrafts.Add(iP, d);
            return d;
        }

        /// <summary>
        /// 目前這份參數的地板（含**關著時記住的欄位**）→ 設定檔同形的 JSON（`show`／回傳檔的「實際生效值」用）。
        /// texture：內建網格寫 "builtin"，否則是解析後的絕對路徑。
        /// </summary>
        public static SCP_JsonData DescribeFloor(SCP_SculptRenderParams iP)
        {
            FloorDraft d = DraftOf(iP);
            SCP_SculptFloor f = d.Values;
            var o = SCP_JsonData.NewObject();
            o.Set("enabled", d.Enabled);
            o.Set("z", f.Z);
            o.Set("full_grid", f.FullGrid);
            o.Set("margin", f.Margin);
            o.Set("margin_ratio", f.MarginRatio);
            o.Set("texture", f.Texture ?? FloorBuiltin);
            o.Set("tile_size", f.TileSize);
            o.Set("color", "#" + f.R.ToString("x2") + f.G.ToString("x2") + f.B.ToString("x2"));
            o.Set("fade", f.Fade);
            return o;
        }

        static bool TryApplyFloor(SCP_JsonData iFloor, SCP_SculptRenderParams ioP, string iFloorBase, out string oError)
        {
            oError = "";
            if (iFloor.Type != SCP_JsonType.Object) { oError = "floor 要是物件（{\"enabled\":true,…}）"; return false; }
            if (!CheckKeys(iFloor, s_FloorKeys, "floor.", out oError)) return false;
            FloorDraft d = DraftOf(ioP);
            // 先在複本上改，全部驗過才寫回（失敗 ⇒ 草稿不動）
            bool aEnabled = d.Enabled;
            SCP_SculptFloor v = CloneFloor(d.Values);
            if (iFloor["enabled"].Exists) aEnabled = iFloor["enabled"].AsBool();
            if (iFloor["z"].Exists) v.Z = Range(iFloor["z"], "floor.z", FloorZMin, FloorZMax);
            if (iFloor["full_grid"].Exists) v.FullGrid = iFloor["full_grid"].AsBool();
            if (iFloor["margin"].Exists) v.Margin = Range(iFloor["margin"], "floor.margin", 0, FloorMarginMax);
            if (iFloor["margin_ratio"].Exists) v.MarginRatio = Range(iFloor["margin_ratio"], "floor.margin_ratio", 0, FloorMarginRatioMax);
            if (iFloor["tile_size"].Exists) v.TileSize = Range(iFloor["tile_size"], "floor.tile_size", FloorTileMin, FloorTileMax);
            if (iFloor["fade"].Exists) v.Fade = Range(iFloor["fade"], "floor.fade", 0, FloorFadeMax);
            if (iFloor["color"].Exists)
            {
                if (!TryHexColor(iFloor["color"].AsString(), out byte r, out byte g, out byte b))
                { oError = "floor.color 要 #RRGGBB：" + iFloor["color"].AsString(); return false; }
                v.R = r; v.G = g; v.B = b;
            }
            if (iFloor["texture"].Exists)
            {
                if (!TryResolveFloorTexture(iFloor["texture"].AsString(), iFloorBase, out string? aTex, out oError)) return false;
                v.Texture = aTex;
            }
            d.Enabled = aEnabled;
            d.Values = v;
            ioP.Floor = aEnabled ? CloneFloor(v) : null;
            d.Materialized = ioP.Floor;
            return true;
        }

        /// <summary>
        /// 地板貼圖字串 → 渲染參數：builtin（或空）⇒ null（內建量尺網格）、相對路徑以 floors/ 為基準、絕對路徑照用。
        /// 檔案不存在 ⇒ 錯誤（⛔ 不默默退回內建網格）。
        /// </summary>
        public static bool TryResolveFloorTexture(string iValue, string iFloorBase, out string? oTexture, out string oError)
        {
            oTexture = null; oError = "";
            string v = (iValue ?? "").Trim();
            if (v.Length == 0 || v.Equals(FloorBuiltin, StringComparison.OrdinalIgnoreCase)) return true;
            string aPath = Path.IsPathRooted(v) ? v : Path.Combine(iFloorBase, v);
            aPath = aPath.Replace('\\', '/');
            if (!File.Exists(aPath)) { oError = "地板貼圖不存在：" + aPath; return false; }
            oTexture = aPath;
            return true;
        }

        // ───────────────────────────── 編輯（CLI 的 sub=set） ─────────────────────────────

        /// <summary>
        /// 把扁平的 CLI 參數改進一份設定（就地改 <paramref name="ioProfile"/>）。
        /// 認得的鍵：projection yaw pitch roll target eye distance fov zoom ambient ao shadow skybox skybox_yaw skybox_tilt background
        /// width height fit_upscale、lights（整組 JSON 陣列取代）、light_add（`x,y,z[;#rrggbb[;強度[;shadow 0|1]]]`，可用 `|` 串多盞）、
        /// light_clear=1、地板 floor（on|off）floor_z floor_full_grid floor_margin floor_margin_ratio floor_texture floor_tile floor_color floor_fade、
        /// unset（逗號分隔的鍵 ⇒ 從設定裡拿掉，回到沿用下層；`floor` ＝ 整個地板物件，floor_* ＝ 其中一格）。
        /// <para>target／eye／distance／zoom 給 <c>auto</c> ⇒ 寫 null（明確改回自動）。</para>
        /// </summary>
        public static bool TryEdit(SCP_JsonData ioProfile, IReadOnlyDictionary<string, string> iArgs, List<string> oChanged, out string oError)
        {
            oError = "";
            SCP_JsonData Cam()
            {
                if (!ioProfile["camera"].Exists || ioProfile["camera"].Type != SCP_JsonType.Object) ioProfile.Set("camera", SCP_JsonData.NewObject());
                return ioProfile["camera"];
            }
            SCP_JsonData Sky()
            {
                if (!ioProfile["skybox"].Exists || ioProfile["skybox"].Type != SCP_JsonType.Object) ioProfile.Set("skybox", SCP_JsonData.NewObject());
                return ioProfile["skybox"];
            }
            SCP_JsonData Floor()
            {
                if (!ioProfile["floor"].Exists || ioProfile["floor"].Type != SCP_JsonType.Object) ioProfile.Set("floor", SCP_JsonData.NewObject());
                return ioProfile["floor"];
            }
            try
            {
                foreach (var kv in iArgs)
                {
                    string k = kv.Key, v = kv.Value.Trim();
                    switch (k)
                    {
                        case "projection": Cam().Set("projection", v); break;
                        case "yaw": case "pitch": case "roll": case "fov": Cam().Set(k, Num(v, k)); break;
                        case "target": case "eye":
                            Cam().Set(k, v.Equals("auto", StringComparison.OrdinalIgnoreCase) ? SCP_JsonData.NewNull() : Vec3Json(v, k)); break;
                        case "distance": case "zoom":
                            Cam().Set(k, v.Equals("auto", StringComparison.OrdinalIgnoreCase) ? SCP_JsonData.NewNull() : SCP_JsonData.NewNumber(Num(v, k))); break;
                        case "ambient": ioProfile.Set("ambient", Num(v, k)); break;
                        case "width": case "height": ioProfile.Set(k, (long)Num(v, k)); break;
                        case "ao": case "shadow": ioProfile.Set(k, Bool(v, k)); break;
                        case "background": ioProfile.Set("background", v); break;
                        case "skybox": Sky().Set("path", v); break;
                        case "skybox_yaw": Sky().Set("yaw", Num(v, k)); break;
                        case "skybox_tilt": Sky().Set("tilt", Num(v, k)); break;
                        case "fit_upscale": Cam().Set("fit_upscale", Bool(v, k)); break;
                        case "floor": Floor().Set("enabled", Bool(v, k)); break;
                        case "floor_full_grid": Floor().Set("full_grid", Bool(v, k)); break;
                        case "floor_z": case "floor_margin": case "floor_margin_ratio": case "floor_tile": case "floor_fade": Floor().Set(s_FloorFlat[k], Num(v, k)); break;
                        case "floor_texture": case "floor_color": Floor().Set(s_FloorFlat[k], v); break;
                        case "lights":
                        {
                            SCP_JsonData aArr = SCP_JsonData.Parse(v);
                            if (aArr.Type != SCP_JsonType.Array) throw new BadValue("lights 要是 JSON 陣列");
                            ioProfile.Set("lights", aArr);
                            break;
                        }
                        case "light_clear":
                            if (Bool(v, k)) ioProfile.Set("lights", SCP_JsonData.NewArray());
                            break;
                        default: continue;
                    }
                    oChanged.Add(k);
                }
                // light_add 排在 light_clear 之後處理（同一次給兩個 ⇒ 先清再加）
                if (iArgs.TryGetValue("light_add", out string? aAdd) && aAdd.Trim().Length > 0)
                {
                    if (!ioProfile["lights"].Exists || ioProfile["lights"].Type != SCP_JsonType.Array)
                        ioProfile.Set("lights", SCP_JsonData.NewArray());
                    foreach (string aOne in aAdd.Split('|'))
                        ioProfile["lights"].Add(ParseLight(aOne.Trim()));
                    oChanged.Add("light_add");
                }
                if (iArgs.TryGetValue("unset", out string? aUnset) && aUnset.Trim().Length > 0)
                {
                    foreach (string aKey in aUnset.Split(','))
                    {
                        string u = aKey.Trim();
                        if (u.Length == 0) continue;
                        bool aDone = s_CameraKeys.Contains(u) ? (ioProfile["camera"].Exists && ioProfile["camera"].Remove(u))
                                   : u == "skybox_yaw" ? (ioProfile["skybox"].Exists && ioProfile["skybox"].Remove("yaw"))
                                   : u == "skybox_tilt" ? (ioProfile["skybox"].Exists && ioProfile["skybox"].Remove("tilt"))
                                   : u != "floor" && s_FloorFlat.TryGetValue(u, out string? aFk) ? (ioProfile["floor"].Exists && ioProfile["floor"].Remove(aFk!))
                                   : s_TopKeys.Contains(u) ? ioProfile.Remove(u)
                                   : throw new BadValue("unset 不認得的鍵：" + u);
                        oChanged.Add("unset " + u + (aDone ? "" : "（本來就沒寫）"));
                    }
                }
            }
            catch (BadValue e) { oError = e.Message; return false; }
            catch (SCP_JsonParseException e) { oError = "lights JSON 解析失敗：" + e.Message; return false; }
            return true;
        }

        static SCP_JsonData ParseLight(string iSpec)
        {
            // x,y,z[;#rrggbb[;強度[;shadow]]]
            string[] p = iSpec.Split(';');
            var aL = SCP_JsonData.NewObject();
            aL.Set("dir", Vec3Json(p[0], "light_add.dir"));
            if (p.Length > 1 && p[1].Trim().Length > 0) aL.Set("color", p[1].Trim());
            if (p.Length > 2 && p[2].Trim().Length > 0) aL.Set("intensity", Num(p[2], "light_add.intensity"));
            if (p.Length > 3 && p[3].Trim().Length > 0) aL.Set("shadow", Bool(p[3], "light_add.shadow"));
            if (p.Length > 4) throw new BadValue("light_add 最多四段（方向;顏色;強度;shadow）：" + iSpec);
            return aL;
        }

        // ───────────────────────────── 小工具 ─────────────────────────────

        sealed class BadValue : Exception { public BadValue(string m) : base(m) { } }

        static bool CheckKeys(SCP_JsonData iObj, HashSet<string> iAllowed, string iPrefix, out string oError)
        {
            oError = "";
            foreach (string k in iObj.Keys)
                if (!iAllowed.Contains(k))
                {
                    oError = "不認得的設定鍵 `" + iPrefix + k + "`（認得：" + string.Join("、", Sorted(iAllowed)) + "）";
                    return false;
                }
            return true;
        }

        static List<string> Sorted(HashSet<string> iSet) { var a = new List<string>(iSet); a.Sort(StringComparer.Ordinal); return a; }

        static double Finite(SCP_JsonData iV, string iName)
        {
            double d = iV.AsDouble();
            if (double.IsNaN(d) || double.IsInfinity(d)) throw new BadValue(iName + " 不是有限數字");
            return d;
        }

        static double Range(SCP_JsonData iV, string iName, double iMin, double iMax)
        {
            double d = Finite(iV, iName);
            if (d < iMin || d > iMax) throw new BadValue(iName + " 要在 " + iMin.ToString(CultureInfo.InvariantCulture) + ".."
                                                        + iMax.ToString(CultureInfo.InvariantCulture) + "：" + d.ToString(CultureInfo.InvariantCulture));
            return d;
        }

        static double Positive(SCP_JsonData iV, string iName)
        {
            double d = Finite(iV, iName);
            if (d <= 0) throw new BadValue(iName + " 要 > 0：" + d.ToString(CultureInfo.InvariantCulture));
            return d;
        }

        static double[] Vec3(SCP_JsonData iV, string iName)
        {
            if (iV.Type != SCP_JsonType.Array) throw new BadValue(iName + " 要是 [x,y,z]");
            var a = new List<double>();
            foreach (SCP_JsonData e in iV) a.Add(Finite(e, iName));
            if (a.Count != 3) throw new BadValue(iName + " 要剛好三個數（給了 " + a.Count + "）");
            return a.ToArray();
        }

        static double Num(string iText, string iName)
        {
            if (!double.TryParse(iText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                throw new BadValue(iName + " 不是數字：" + iText);
            return d;
        }

        static bool Bool(string iText, string iName)
        {
            string t = iText.Trim().ToLowerInvariant();
            if (t == "1" || t == "true" || t == "on" || t == "yes") return true;
            if (t == "0" || t == "false" || t == "off" || t == "no") return false;
            throw new BadValue(iName + " 要 0／1：" + iText);
        }

        static SCP_JsonData Vec3Json(string iText, string iName)
        {
            string[] p = iText.Split(',');
            if (p.Length != 3) throw new BadValue(iName + " 要 x,y,z 三個數：" + iText);
            var a = SCP_JsonData.NewArray();
            foreach (string s in p) a.Add(Num(s, iName));
            return a;
        }

        public static bool TryHexColor(string iText, out byte oR, out byte oG, out byte oB)
        {
            oR = oG = oB = 0;
            string s = (iText ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            oR = (byte)((v >> 16) & 255); oG = (byte)((v >> 8) & 255); oB = (byte)(v & 255);
            return true;
        }
    }
}
