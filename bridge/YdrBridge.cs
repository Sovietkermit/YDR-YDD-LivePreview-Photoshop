/**
 * YdrBridge.cs
 * Mode watch (fichiers) + serveur HTTP + CLI : convertit un .ydr/.ydd en .glb
 */

using System;
using System.IO;
using System.Numerics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using CodeWalker.GameFiles;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

using SDXVector2 = SharpDX.Vector2;
using SDXVector3 = SharpDX.Vector3;
using NumVector2 = System.Numerics.Vector2;
using NumVector3 = System.Numerics.Vector3;
using NumVector4 = System.Numerics.Vector4;
using CWTexture = CodeWalker.GameFiles.Texture;

class YdrBridge
{
    static string CwDir = AppDomain.CurrentDomain.BaseDirectory;
    static bool KeysLoaded = false;
    const int ViewerPort = 8743;

    static readonly Dictionary<string, (byte[] glb, DateTime ts)> GlbCache = new();
    static readonly Dictionary<string, (YtdFile ytd, DateTime ts)> YtdCache = new();
    static readonly object CacheLock = new();

    static int Main(string[] args)
    {
        // Mode CLI explicite (debug manuel, garde compat) : YdrBridge.exe <input> <output.glb> [cwdir]
        if (args.Length >= 2 && !args[0].StartsWith("--"))
        {
            string ydrPath = args[0];
            string glbPath = args[1];
            CwDir = args.Length >= 3 ? args[2] : AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                byte[] glbBytes = ConvertToGlb(ydrPath);
                var dir = Path.GetDirectoryName(glbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(glbPath, glbBytes);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERREUR : {ex.Message}");
                return 5;
            }
        }

        // Mode normal : pas d'argument requis — on lit bridge_config.json à côté
        // de l'exe. C'est le plugin qui écrit ce fichier avant de lancer le bridge,
        // ce qui évite tout passage d'arguments en ligne de commande (UXP ne peut
        // pas lancer un .exe avec des arguments personnalisés).
        string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bridge_config.json");

        if (!File.Exists(configPath))
        {
            Console.WriteLine("[YdrBridge] bridge_config.json introuvable — création d'un template.");
            File.WriteAllText(configPath, "{\n  \"exchangeDir\": \"\",\n  \"cwDir\": \"\"\n}");
            Console.WriteLine($"[YdrBridge] Édite ce fichier ou configure-le depuis le plugin : {configPath}");
            Console.WriteLine("[YdrBridge] Relance YdrBridge.exe une fois rempli.");
            Console.WriteLine("\nAppuie sur une touche pour fermer...");
            Console.ReadKey();
            return 1;
        }

        string watchDir, cwDirFromConfig;
        try
        {
            string json = File.ReadAllText(configPath);
            watchDir = ExtractJsonStringField(json, "exchangeDir");
            cwDirFromConfig = ExtractJsonStringField(json, "cwDir");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[YdrBridge] Erreur lecture bridge_config.json : {ex.Message}");
            Console.ReadKey();
            return 1;
        }

        if (string.IsNullOrWhiteSpace(watchDir))
        {
            Console.Error.WriteLine("[YdrBridge] 'exchangeDir' vide dans bridge_config.json — configure-le depuis le plugin (⚙ Configuration) puis relance.");
            Console.ReadKey();
            return 1;
        }

        CwDir = string.IsNullOrWhiteSpace(cwDirFromConfig)
            ? AppDomain.CurrentDomain.BaseDirectory
            : cwDirFromConfig;

        if (string.IsNullOrWhiteSpace(cwDirFromConfig))
            Console.WriteLine("[YdrBridge] ATTENTION : 'cwDir' (chemin CodeWalker) vide — les .ytd/.ydr chiffrés vanilla échoueront. Configure-le si besoin.");

        return RunWatch(watchDir);
    }

    static int RunWatch(string watchDir)
    {
        Directory.CreateDirectory(watchDir);
        Console.WriteLine($"[YdrBridge] Mode watch actif sur : {watchDir}");
        Console.WriteLine($"[YdrBridge] CWDIR : {CwDir}");
        Console.WriteLine($"[YdrBridge] Viewer HTTP : http://127.0.0.1:{ViewerPort}/viewer");

        new Thread(() => RunServer(ViewerPort)) { IsBackground = true }.Start();

        var processed = new HashSet<string>();
        while (true)
        {
            try
            {
                var requestFiles = Directory.GetFiles(watchDir, "request_*.json");
                foreach (var reqFile in requestFiles)
                {
                    if (processed.Contains(reqFile)) continue;
                    string id, ydrPath;
                    try { (id, ydrPath) = ParseRequestJson(File.ReadAllText(reqFile)); }
                    catch { processed.Add(reqFile); continue; }

                    processed.Add(reqFile);
                    string responseJsonPath = Path.Combine(watchDir, $"response_{id}.json");
                    string ext = Path.GetExtension(ydrPath).ToLowerInvariant();

                    try
                    {
                        if (ext == ".ytd")
                        {
                            // .ytd : pas de modèle 3D — on liste juste les textures disponibles,
                            // le plugin les récupérera ensuite via /ytd-pixels (réseau HTTP direct).
                            if (!KeysLoaded) { try { GTA5Keys.LoadFromPath(CwDir); KeysLoaded = true; } catch { } }
                            byte[] ytdData = File.ReadAllBytes(ydrPath);
                            var ytd = new YtdFile();
                            ytd.Load(ytdData);
                            if (ytd.TextureDict?.Textures == null || ytd.TextureDict.Textures.Count == 0)
                                throw new Exception("YTD sans textures");

                            lock (CacheLock) { YtdCache[id] = (ytd, DateTime.UtcNow); }

                            var names = new List<string>();
                            var texItems = ytd.TextureDict.Textures.data_items;
                            if (texItems != null)
                            {
                                foreach (var tex in texItems)
                                    if (tex != null) names.Add(tex.Name ?? "(sans nom)");
                            }

                            string namesJson = "[" + string.Join(",", names.Select(n => $"\"{EscapeJson(n)}\"")) + "]";
                            File.WriteAllText(responseJsonPath, $"{{\"status\":\"ok\",\"kind\":\"ytd\",\"ytdId\":\"{EscapeJson(id)}\",\"textures\":{namesJson}}}");
                            Console.WriteLine($"[YdrBridge] OK #{id} -> YTD avec {names.Count} texture(s)");
                        }
                        else
                        {
                            byte[] glb = ConvertToGlb(ydrPath);
                            lock (CacheLock) { GlbCache[id] = (glb, DateTime.UtcNow); }
                            string viewerUrl = $"http://localhost:{ViewerPort}/viewer?id={id}";
                            File.WriteAllText(responseJsonPath, $"{{\"status\":\"ok\",\"kind\":\"model\",\"viewer\":\"{EscapeJson(viewerUrl)}\"}}");
                            Console.WriteLine($"[YdrBridge] OK #{id} -> {viewerUrl}");
                        }
                    }
                    catch (Exception ex)
                    {
                        string errMsg = ex.GetType().Name + ": " + ex.Message;
                        File.WriteAllText(responseJsonPath, $"{{\"status\":\"error\",\"message\":\"{EscapeJson(errMsg)}\"}}");
                        Console.WriteLine($"[YdrBridge] ERREUR #{id} ({ext}) : {ex.GetType().FullName} - {ex.Message}");
                        Console.WriteLine($"[YdrBridge] StackTrace : {ex.StackTrace}");
                        if (ex.InnerException != null)
                            Console.WriteLine($"[YdrBridge] InnerException : {ex.InnerException.GetType().FullName} - {ex.InnerException.Message}");
                    }
                    try { File.Delete(reqFile); } catch { }
                }
                if (processed.Count > 500) processed.Clear();
            }
            catch { }
            Thread.Sleep(500);
        }
    }

    static (string id, string path) ParseRequestJson(string json)
    {
        string id = ExtractJsonStringField(json, "id");
        string path = ExtractJsonStringField(json, "path");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(path)) throw new Exception("JSON invalide");
        return (id, path);
    }

    static string ExtractJsonStringField(string json, string field)
    {
        string pattern = $"\"{field}\"";
        int idx = json.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        idx = json.IndexOf(':', idx + pattern.Length);
        if (idx < 0) return null;
        idx = json.IndexOf('"', idx);
        if (idx < 0) return null;
        int end = idx + 1;
        var sb = new StringBuilder();
        while (end < json.Length && json[end] != '"')
        {
            if (json[end] == '\\' && end + 1 < json.Length) { end++; sb.Append(json[end]); }
            else { sb.Append(json[end]); }
            end++;
        }
        return sb.ToString();
    }

    static string EscapeJson(string s) => s?.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") ?? "";

    static int RunServer(int port)
    {
        var listener = new HttpListener();
        string prefix = $"http://localhost:{port}/";
        listener.Prefixes.Add(prefix);
        try { listener.Start(); } catch (Exception ex) { Console.Error.WriteLine($"ERREUR serveur: {ex.Message}"); return 10; }
        Console.WriteLine($"[YdrBridge] Serveur démarré : {prefix}");

        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); } catch { continue; }
            ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
        }
    }

    static void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        resp.AppendHeader("Access-Control-Allow-Origin", "*");

        try
        {
            string path = req.Url.AbsolutePath.TrimEnd('/');

            // Servir les fichiers JS locaux (Three.min.js, GLTFLoader.js)
            if (path.EndsWith(".js"))
            {
                string filename = Path.GetFileName(path);
                string filepath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
                if (File.Exists(filepath))
                {
                    byte[] bytes = File.ReadAllBytes(filepath);
                    resp.ContentType = "application/javascript";
                    resp.StatusCode = 200;
                    resp.ContentLength64 = bytes.Length;
                    resp.OutputStream.Write(bytes, 0, bytes.Length);
                    resp.OutputStream.Close();
                    return;
                }
            }

            if (path == "/ping") { WriteText(resp, 200, "OK"); return; }

            if (path.StartsWith("/model/"))
            {
                string id = path.Substring("/model/".Length);
                byte[] glb = null;
                lock (CacheLock) { if (GlbCache.TryGetValue(id, out var entry)) glb = entry.glb; }
                if (glb == null) { WriteText(resp, 404, "Introuvable"); return; }
                resp.ContentType = "model/gltf-binary";
                resp.StatusCode = 200;
                resp.ContentLength64 = glb.Length;
                resp.OutputStream.Write(glb, 0, glb.Length);
                resp.OutputStream.Close();
                return;
            }

            if (path == "/viewer")
            {
                string id = GetQueryParam(req.Url.Query, "id") ?? "";
                string html = BuildViewerHtml(id);
                byte[] bytes = Encoding.UTF8.GetBytes(html);
                resp.ContentType = "text/html; charset=utf-8";
                resp.StatusCode = 200;
                resp.ContentLength64 = bytes.Length;
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.OutputStream.Close();
                return;
            }

            if (path == "/ytd-pixels")
            {
                string id = GetQueryParam(req.Url.Query, "id") ?? "";
                string texName = GetQueryParam(req.Url.Query, "name") ?? "";
                texName = Uri.UnescapeDataString(texName);

                YtdFile ytd = null;
                lock (CacheLock) { if (YtdCache.TryGetValue(id, out var entry)) ytd = entry.ytd; }
                if (ytd == null) { WriteText(resp, 404, "YTD introuvable (id expiré ou inconnu)"); return; }

                CWTexture foundTex = FindTextureByName(ytd, texName);
                if (foundTex == null) { WriteText(resp, 404, "Texture introuvable : " + texName); return; }

                byte[] pixels;
                try { pixels = CodeWalker.Utils.DDSIO.GetPixels(foundTex, 0); }
                catch (Exception ex)
                {
                    WriteText(resp, 500, $"Erreur décodage DDS (format={foundTex.Format}, {foundTex.Width}x{foundTex.Height}) : {ex.GetType().Name} - {ex.Message}");
                    return;
                }

                resp.AppendHeader("X-Texture-Width",  foundTex.Width.ToString());
                resp.AppendHeader("X-Texture-Height", foundTex.Height.ToString());
                resp.AppendHeader("X-Texture-Comps",  "4"); // GetPixels renvoie du RGBA standard
                resp.ContentType = "application/octet-stream";
                resp.StatusCode = 200;
                resp.ContentLength64 = pixels.Length;
                resp.OutputStream.Write(pixels, 0, pixels.Length);
                resp.OutputStream.Close();
                return;
            }

            if (path == "/ytd-dds")
            {
                string id = GetQueryParam(req.Url.Query, "id") ?? "";
                string texName = GetQueryParam(req.Url.Query, "name") ?? "";
                texName = Uri.UnescapeDataString(texName);

                YtdFile ytd = null;
                lock (CacheLock) { if (YtdCache.TryGetValue(id, out var entry)) ytd = entry.ytd; }
                if (ytd == null) { WriteText(resp, 404, "YTD introuvable (id expiré ou inconnu)"); return; }

                CWTexture foundTex = FindTextureByName(ytd, texName);
                if (foundTex == null) { WriteText(resp, 404, "Texture introuvable : " + texName); return; }

                byte[] ddsBytes;
                try { ddsBytes = CodeWalker.Utils.DDSIO.GetDDSFile(foundTex); }
                catch (Exception ex)
                {
                    WriteText(resp, 500, $"Erreur génération DDS : {ex.GetType().Name} - {ex.Message}");
                    return;
                }

                resp.ContentType = "application/octet-stream";
                resp.StatusCode = 200;
                resp.ContentLength64 = ddsBytes.Length;
                resp.OutputStream.Write(ddsBytes, 0, ddsBytes.Length);
                resp.OutputStream.Close();
                return;
            }

            WriteText(resp, 404, "Inconnu");
        }
        catch (Exception ex) { try { WriteText(resp, 500, ex.Message); } catch { } }
    }

    static void WriteText(HttpListenerResponse resp, int code, string text)
    {
        resp.StatusCode = code;
        resp.ContentType = "text/plain; charset=utf-8";
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        resp.ContentLength64 = bytes.Length;
        resp.OutputStream.Write(bytes, 0, bytes.Length);
        resp.OutputStream.Close();
    }

    static string GetQueryParam(string query, string key)
    {
        if (string.IsNullOrEmpty(query)) return null;
        query = query.TrimStart('?');
        foreach (var pair in query.Split('&'))
        {
            var idx = pair.IndexOf('=');
            if (idx < 0) continue;
            if (string.Equals(pair.Substring(0, idx), key, StringComparison.OrdinalIgnoreCase)) return pair.Substring(idx + 1);
        }
        return null;
    }

    static CWTexture FindTextureByName(YtdFile ytd, string texName)
    {
        var texItemsList = ytd.TextureDict?.Textures?.data_items;
        if (texItemsList == null) return null;
        foreach (var tex in texItemsList)
        {
            if (tex != null && tex.Name == texName) return tex;
        }
        return null;
    }

    static string BuildViewerHtml(string modelId)
    {
        return $@"<!DOCTYPE html>
<html lang=""fr"">
<head>
<meta charset=""UTF-8""/>
<title>Prop Viewer</title>
<style>
* {{ margin:0; padding:0; box-sizing:border-box; }}
body {{ background:#1a1a1a; overflow:hidden; }}
canvas {{ display:block; width:100vw; height:100vh; }}
#info {{ position:fixed; top:8px; left:8px; color:#888; font:11px monospace; pointer-events:none; }}
</style>
</head>
<body>
<canvas id=""c""></canvas>
<div id=""info"">Prop Viewer</div>
<script src=""/three.min.js""></script>
<script src=""/GLTFLoader.js""></script>
<script>
const MODEL_ID = ""{modelId}"";
const MODEL_URL = ""/model/"" + MODEL_ID;

function sendLog(msg, level) {{
  try {{
    if (window.uxpHost && window.uxpHost.postMessage) {{
      window.uxpHost.postMessage({{ type: ""log"", msg: msg, level: level || 'info' }});
    }} else if (window.parent) {{
      window.parent.postMessage({{ type: ""log"", msg: msg, level: level || 'info' }}, ""*"");
    }}
  }} catch(e){{}}
}}

sendLog('Iframe chargée. ID: ' + MODEL_ID, 'info');

try {{
  const canvas = document.getElementById(""c"");
  const renderer = new THREE.WebGLRenderer({{ canvas, antialias: true }});
  renderer.setPixelRatio(devicePixelRatio);
  renderer.outputEncoding = THREE.sRGBEncoding;

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x1e1e1e);
  scene.add(new THREE.AmbientLight(0xffffff, 0.6));
  const sun = new THREE.DirectionalLight(0xffffff, 1.2);
  sun.position.set(2, 4, 3);
  scene.add(sun);
  scene.add(new THREE.GridHelper(20, 20, 0x333333, 0x282828));

  const camera = new THREE.PerspectiveCamera(45, 1, 0.01, 2000);

  let isDragging = false, lastX = 0, lastY = 0;
  let spherical = {{ theta: 0.3, phi: 1.0, radius: 3 }};
  function updateCamera() {{
    camera.position.set(
      spherical.radius * Math.sin(spherical.phi) * Math.sin(spherical.theta),
      spherical.radius * Math.cos(spherical.phi),
      spherical.radius * Math.sin(spherical.phi) * Math.cos(spherical.theta)
    );
    camera.lookAt(0, 0, 0);
  }}
  canvas.addEventListener(""mousedown"", e => {{ isDragging = true; lastX = e.clientX; lastY = e.clientY; }});
  canvas.addEventListener(""mouseup"", () => isDragging = false);
  canvas.addEventListener(""mousemove"", e => {{
    if (!isDragging) return;
    spherical.theta -= (e.clientX - lastX) * 0.01;
    spherical.phi = Math.max(0.1, Math.min(Math.PI - 0.1, spherical.phi + (e.clientY - lastY) * 0.01));
    lastX = e.clientX; lastY = e.clientY;
    updateCamera();
  }});
  canvas.addEventListener(""wheel"", e => {{
    spherical.radius = Math.max(0.5, Math.min(50, spherical.radius + e.deltaY * 0.01));
    updateCamera();
  }});

  function resize() {{
    renderer.setSize(innerWidth, innerHeight, false);
    camera.aspect = innerWidth / innerHeight;
    camera.updateProjectionMatrix();
  }}
  window.addEventListener(""resize"", resize);
  resize();

  sendLog('Three.js initialisé', 'ok');

  let currentModel = null;
  new THREE.GLTFLoader().load(MODEL_URL, 
    gltf => {{
      currentModel = gltf.scene;
      const box = new THREE.Box3().setFromObject(currentModel);
      const center = box.getCenter(new THREE.Vector3());
      const size = box.getSize(new THREE.Vector3()).length();
      currentModel.position.sub(center);
      spherical.radius = size * 1.4;
      updateCamera();
      scene.add(currentModel);
      sendLog('Modèle GLB chargé', 'ok');
    }},
    null,
    err => {{ sendLog('Erreur chargement GLB: ' + err.message, 'err'); }}
  );

  function base64ToBytes(base64) {{
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
  }}

  function applyRawPixelsAsTexture(msg) {{
    if (!currentModel) {{ sendLog('Texture reçue mais aucun modèle chargé encore', 'warn'); return; }}

    try {{
      const src = base64ToBytes(msg.base64);
      const w = msg.width, h = msg.height, comps = msg.comps || 4;

      const cvs = document.createElement(""canvas"");
      cvs.width = w; cvs.height = h;
      const ctx = cvs.getContext(""2d"");
      const img = ctx.createImageData(w, h);
      const dst = img.data;

      if (comps === 4) {{
        dst.set(src);
      }} else if (comps === 3) {{
        for (let i = 0, j = 0; i < src.length; i += 3, j += 4) {{
          dst[j] = src[i]; dst[j+1] = src[i+1]; dst[j+2] = src[i+2]; dst[j+3] = 255;
        }}
      }} else if (comps === 1) {{
        for (let i = 0, j = 0; i < src.length; i += 1, j += 4) {{
          dst[j] = dst[j+1] = dst[j+2] = src[i]; dst[j+3] = 255;
        }}
      }} else {{
        sendLog('Composants inattendus: ' + comps, 'err');
        return;
      }}

      ctx.putImageData(img, 0, 0);

      const tex = new THREE.CanvasTexture(cvs);
      tex.encoding = THREE.sRGBEncoding;
      tex.flipY = false;
      tex.needsUpdate = true;

      let applied = 0;
      currentModel.traverse(obj => {{
        if (obj.isMesh) {{
          const mats = Array.isArray(obj.material) ? obj.material : [obj.material];
          mats.forEach(m => {{ if (m.name === ""textured"") {{ m.map = tex; m.needsUpdate = true; applied++; }} }});
        }}
      }});
      sendLog('Texture appliquée (' + w + 'x' + h + ', ' + comps + ' canaux) sur ' + applied + ' matériau(x)', 'ok');
    }} catch(e) {{
      sendLog('Erreur applyRawPixelsAsTexture: ' + e.message, 'err');
    }}
  }}

  window.addEventListener(""message"", (event) => {{
    if (event.data && event.data.type === ""texture-raw"" && event.data.base64) {{
      applyRawPixelsAsTexture(event.data);
    }}
  }});

  (function animate() {{
    requestAnimationFrame(animate);
    renderer.render(scene, camera);
  }})();
}} catch(e) {{
  sendLog('Erreur fatale: ' + e.message, 'err');
}}
</script>
</body>
</html>";
    }

    static byte[] ConvertToGlb(string ydrPath)
    {
        if (!File.Exists(ydrPath)) throw new Exception($"Fichier introuvable : {ydrPath}");
        if (!KeysLoaded) { try { GTA5Keys.LoadFromPath(CwDir); KeysLoaded = true; } catch { } }

        string ext = Path.GetExtension(ydrPath).ToLowerInvariant();
        List<Drawable> drawables = new List<Drawable>();
        List<FragDrawable> fragDrawables = new List<FragDrawable>();
        byte[] data = File.ReadAllBytes(ydrPath);

        if (ext == ".ydd")
        {
            var ydd = new YddFile(); ydd.Load(data);
            if (ydd.Drawables == null || ydd.Drawables.Length == 0) throw new Exception("YDD vide");
            foreach (var d in ydd.Drawables) if (d != null) drawables.Add(d);
        }
        else if (ext == ".yft")
        {
            var yft = new YftFile(); yft.Load(data);
            var frag = yft.Fragment;
            if (frag == null) throw new Exception("YFT invalide (Fragment null)");

            // Drawable principal (ex: carrosserie du véhicule)
            if (frag.Drawable != null) fragDrawables.Add(frag.Drawable);

            // Sous-pièces additionnelles (ex: composants visibles séparés)
            if (frag.DrawableArray != null)
            {
                foreach (var d in frag.DrawableArray)
                    if (d != null) fragDrawables.Add(d);
            }

            if (fragDrawables.Count == 0) throw new Exception("YFT sans drawable exploitable");
        }
        else
        {
            var ydr = new YdrFile(); ydr.Load(data);
            if (ydr.Drawable == null) throw new Exception("YDR invalide");
            drawables.Add(ydr.Drawable);
        }

        if (drawables.Count == 0 && fragDrawables.Count == 0) throw new Exception("Aucun drawable");
        var model = BuildGlb(drawables, fragDrawables);

        string tempGlb = Path.Combine(Path.GetTempPath(), $"ydr_{Guid.NewGuid():N}.glb");
        try { model.SaveGLB(tempGlb); return File.ReadAllBytes(tempGlb); }
        finally { try { if (File.Exists(tempGlb)) File.Delete(tempGlb); } catch { } }
    }

    static ModelRoot BuildGlb(List<Drawable> drawables, List<FragDrawable> fragDrawables = null)
    {
        var matTextured = new MaterialBuilder("textured").WithDoubleSide(false).WithMetallicRoughnessShader().WithChannelParam(KnownChannel.BaseColor, new NumVector4(0.85f, 0.85f, 0.85f, 1f));
        var matFlat = new MaterialBuilder("no_uv_flat").WithDoubleSide(false).WithMetallicRoughnessShader().WithChannelParam(KnownChannel.BaseColor, new NumVector4(0.45f, 0.45f, 0.48f, 1f));
        var scene = new SceneBuilder();
        int meshIdx = 0;

        foreach (var drawable in drawables)
        {
            var block = drawable?.DrawableModels;
            if (block == null) continue;
            DrawableModel[] lodItems = (block.High != null && block.High.Length > 0) ? block.High : (block.Med != null && block.Med.Length > 0) ? block.Med : (block.Low != null && block.Low.Length > 0) ? block.Low : block.VLow;
            if (lodItems == null) continue;

            foreach (var drawModel in lodItems)
            {
                if (drawModel?.Geometries == null) continue;
                foreach (var geom in drawModel.Geometries)
                {
                    if (geom == null) continue;
                    try { BuildGeometryMesh(geom, matTextured, matFlat, scene, meshIdx++); } catch { }
                }
            }
        }

        // .yft : FragDrawable a la même structure DrawableModels (DrawableModelsBlock)
        // que Drawable — on réutilise donc exactement la même logique d'extraction.
        if (fragDrawables != null)
        {
            foreach (var fragDrawable in fragDrawables)
            {
                var block = fragDrawable?.DrawableModels;
                if (block == null) continue;
                DrawableModel[] lodItems = (block.High != null && block.High.Length > 0) ? block.High : (block.Med != null && block.Med.Length > 0) ? block.Med : (block.Low != null && block.Low.Length > 0) ? block.Low : block.VLow;
                if (lodItems == null) continue;

                foreach (var drawModel in lodItems)
                {
                    if (drawModel?.Geometries == null) continue;
                    foreach (var geom in drawModel.Geometries)
                    {
                        if (geom == null) continue;
                        try { BuildGeometryMesh(geom, matTextured, matFlat, scene, meshIdx++); } catch { }
                    }
                }
            }
        }

        return scene.ToGltf2();
    }

    static void BuildGeometryMesh(DrawableGeometry geom, MaterialBuilder matTextured, MaterialBuilder matFlat, SceneBuilder scene, int idx)
    {
        var vb = geom.VertexBuffer;
        var ib = geom.IndexBuffer;
        if (vb?.Data1 == null || ib?.Indices == null) return;
        var vdata = vb.Data1;
        var decl = vdata.Info ?? vb.Info;
        if (decl == null) return;

        int posComp = -1, normComp = -1, uvComp = -1;
        for (int c = 0; c < decl.Count; c++)
        {
            var ctype = decl.GetComponentType(c);
            if (c == 0 && posComp < 0) { posComp = c; continue; }
            if (normComp < 0 && (ctype == VertexComponentType.Float3 || ctype == VertexComponentType.FloatUnk)) { normComp = c; continue; }
            if (uvComp < 0 && (ctype == VertexComponentType.Float2 || ctype == VertexComponentType.Half2)) uvComp = c;
        }
        if (posComp < 0) return;

        var mat = uvComp >= 0 ? matTextured : matFlat;
        var mb = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>($"mesh_{idx}");
        var prim = mb.UsePrimitive(mat);
        var indices = ib.Indices;

        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            prim.AddTriangle(
                MakeVertex(vdata, indices[i], posComp, normComp, uvComp),
                MakeVertex(vdata, indices[i + 1], posComp, normComp, uvComp),
                MakeVertex(vdata, indices[i + 2], posComp, normComp, uvComp)
            );
        }
        scene.AddRigidMesh(mb, Matrix4x4.Identity);
    }

    static (VertexPositionNormal, VertexTexture1, VertexEmpty) MakeVertex(VertexData vdata, int vertexIndex, int posComp, int normComp, int uvComp)
    {
        SDXVector3 p = vdata.GetVector3(vertexIndex, posComp);
        var pos = new NumVector3(p.X, p.Z, -p.Y);
        NumVector3 nor = new NumVector3(0, 1, 0);
        if (normComp >= 0) { SDXVector3 n = vdata.GetVector3(vertexIndex, normComp); nor = new NumVector3(n.X, n.Z, -n.Y); }
        NumVector2 uv = NumVector2.Zero;
        if (uvComp >= 0) { SDXVector2 u = vdata.GetVector2(vertexIndex, uvComp); uv = new NumVector2(u.X, u.Y); }
        return (new VertexPositionNormal(pos, nor), new VertexTexture1(uv), default);
    }
}