/**
 * YDR Preview – index.js
 * Plugin UXP Photoshop
 * Surveille le document .dds actif → refresh texture Three.js (via postMessage à l'iframe)
 * Charge .ydr via échange de fichiers avec YdrBridge.exe --watch → GLB → Iframe Viewer
 */

const { app, action, core, imaging } = require("photoshop");
const uxp = require("uxp");
const storage = uxp.storage;
const uxpFS = storage.localFileSystem;
const shell   = require("uxp").shell;

// ─────────────────────────────────────────────
// CONFIG
// ─────────────────────────────────────────────
const CONFIG_KEY = "ydrpreview_config";
let config = {
  exchangeDir: "",  // dossier d'échange avec YdrBridge.exe --watch — à définir par l'utilisateur
  bridgePath:  "",  // chemin vers YdrBridge.exe — à définir par l'utilisateur
  cwDir:       "",  // chemin dossier CodeWalker (CodeWalker.Core.dll + clés) — optionnel
};

async function loadConfig() {
  try {
    const folder = await uxpFS.getDataFolder();
    const file   = await folder.getEntry("config.json").catch(() => null);
    if (file) {
      const txt = await file.read({ format: storage.formats.utf8 });
      config = { ...config, ...JSON.parse(txt) };
      dbg("Config chargée", "ok");
    } else {
      dbg("Pas de config.json — defaults utilisés", "warn");
    }
  } catch(e) {
    dbg("loadConfig error: " + e.message, "err");
  }
}

async function saveConfig() {
  try {
    const folder = await uxpFS.getDataFolder();
    let file = await folder.getEntry("config.json").catch(() => null);
    if (!file) file = await folder.createFile("config.json");
    await file.write(JSON.stringify(config, null, 2), { format: storage.formats.utf8 });
    dbg("Config sauvegardée", "ok");
  } catch(e) {
    dbg("saveConfig error: " + e.message, "err");
  }
}

// ─────────────────────────────────────────────
// DEBUG
// ─────────────────────────────────────────────
const debugLog  = document.getElementById("debug-log");
const statusBar = document.getElementById("statusbar");

function dbg(msg, level = "info") {
  const el = document.createElement("div");
  el.className = "log-" + level;
  const ts = new Date().toLocaleTimeString("fr-FR", { hour12: false });
  el.textContent = `[${ts}] ${msg}`;
  debugLog.appendChild(el);
  debugLog.scrollTop = debugLog.scrollHeight;

  statusBar.textContent = msg;
  statusBar.className   = level === "ok" ? "ok" : level === "err" ? "err" : level === "warn" ? "warn" : "";
}

// ─────────────────────────────────────────────
// CONVERSION YDR (Échange de fichiers)
// ─────────────────────────────────────────────
function genRequestId() {
  return Date.now().toString(36) + "_" + Math.random().toString(36).slice(2, 8);
}

async function requestConversion(ydrPath, { timeoutMs = 30000, pollMs = 300 } = {}) {
  dbg("Étape 1/4 : obtention dossier d'échange...", "dim");
  const exDirEntry = await getOrCreateExchangeFolder();
  if (!exDirEntry) throw new Error("Dossier d'échange inaccessible : " + config.exchangeDir);
  dbg("Dossier d'échange OK : " + exDirEntry.nativePath, "ok");

  const id = genRequestId();
  const reqName  = `request_${id}.json`;
  const respJson = `response_${id}.json`;

  dbg("Étape 2/4 : écriture requête...", "dim");
  let reqFile;
  try {
    reqFile = await exDirEntry.createFile(reqName, { overwrite: true });
  } catch (e) {
    throw new Error("createFile a échoué : " + e.message);
  }

  const payload = JSON.stringify({ id, path: ydrPath });
  try {
    await reqFile.write(payload, { format: storage.formats.utf8 });
  } catch (e) {
    throw new Error("write a échoué : " + e.message);
  }
  dbg(`Requête écrite : ${reqName}`, "info");

  dbg("Étape 3/4 : attente réponse du bridge (polling)...", "dim");
  const start = Date.now();
  let pollCount = 0;
  while (Date.now() - start < timeoutMs) {
    pollCount++;
    const jsonEntry = await exDirEntry.getEntry(respJson).catch(() => null);
    if (jsonEntry) {
      dbg(`Réponse JSON détectée après ${pollCount} polls`, "info");
      const txt = await jsonEntry.read({ format: storage.formats.utf8 });
      let parsed;
      try { parsed = JSON.parse(txt); } catch { parsed = null; }

      if (parsed && parsed.status === "ok") {
        dbg("Étape 4/4 : réponse OK reçue", "dim");
        jsonEntry.delete().catch(() => {});
        return parsed; // { status, kind, viewer } OU { status, kind:"ytd", ytdId, textures }
      } else if (parsed && parsed.status === "error") {
        jsonEntry.delete().catch(() => {});
        throw new Error(parsed.message || "Erreur bridge inconnue");
      } else {
        throw new Error("response.json illisible : " + txt);
      }
    }
    await new Promise(r => setTimeout(r, pollMs));
  }

  throw new Error(`Timeout (${timeoutMs}ms) — le bridge --watch tourne-t-il bien ?`);
}

let cachedExchangeFolder = null;
async function getOrCreateExchangeFolder() {
  if (cachedExchangeFolder) return cachedExchangeFolder;
  try {
    dbg("Ouverture du dialog de sélection dossier — choisis : " + config.exchangeDir, "warn");
    const folder = await uxpFS.getFolder();
    if (!folder) return null;
    cachedExchangeFolder = folder;
    return folder;
  } catch (e) {
    return null;
  }
}

// ─────────────────────────────────────────────
// WEBVIEW SETUP (events loadstart/loadstop/loaderror)
// ─────────────────────────────────────────────
const webviewEl = document.getElementById("viewer-frame");

webviewEl.addEventListener("loadstart", (e) => dbg("WebView loadstart : " + e.url, "info"));
webviewEl.addEventListener("loadstop",  (e) => dbg("WebView loadstop : " + e.url, "ok"));
webviewEl.addEventListener("loaderror", (e) => dbg(`WebView loaderror : ${e.url} code:${e.code} msg:${e.message}`, "err"));

// Panels (Config / Lier texture / Texture ytd) sont maintenant inline dans
// le flux normal du document (comme #debug-panel), donc plus de chevauchement
// avec la <webview> native — pas besoin de la retirer/cacher.
function showModal(modalEl) {
  modalEl.classList.remove("hidden");
}

function hideModal(modalEl) {
  modalEl.classList.add("hidden");
}

// ─────────────────────────────────────────────
// IMPORT YDR
// ─────────────────────────────────────────────
document.getElementById("btn-import-ydr").addEventListener("click", async () => {
  try {
    // On affiche tous les fichiers (UXP ne permet pas de regrouper plusieurs
    // extensions sous un seul nom de filtre dans le dialog natif Windows) —
    // on valide donc l'extension nous-mêmes après sélection.
    const file = await uxpFS.getFileForOpening({ types: storage.fileTypes.all });
    if (!file) return;

    const validExts = [".ydr", ".ydd", ".yft"];
    const lowerName = file.name.toLowerCase();
    const isValid = validExts.some(ext => lowerName.endsWith(ext));
    if (!isValid) {
      dbg(`Fichier rejeté : "${file.name}" — extensions acceptées : .ydr, .ydd, .yft`, "err");
      return;
    }

    const ydrPath = file.nativePath;
    document.getElementById("ydr-name").textContent = file.name;
    dbg("Fichier sélectionné : " + ydrPath, "info");
    dbg("Conversion en cours...", "info");

    const result = await requestConversion(ydrPath);
    if (!result.viewer) throw new Error("Réponse inattendue du bridge (pas de viewer)");
    const viewerUrl = result.viewer;

    webviewEl.src = viewerUrl;
    dbg("webview.src assigné à : " + viewerUrl, "info");
    document.getElementById("overlay-msg").classList.add("hidden");
    dbg("Modèle chargé dans la vue 3D", "ok");

  } catch(e) {
    dbg("Import erreur : " + e.message, "err");
    dbg('Vérifie que le bridge tourne : YdrBridge.exe --watch "' + config.exchangeDir + '" "<dossier_codewalker>"', "warn");
  }
});

// ─────────────────────────────────────────────
// WATCH DOCUMENT PHOTOSHOP LIÉ EXPLICITEMENT (Texture Live Update)
// ─────────────────────────────────────────────
let linkedDocId        = null;
let linkedDocName      = null;
let lastTextureFingerprint = null;
let watchInterval      = null;

// Hash FNV-1a 32 bits — rapide, sans dépendance, suffisant pour détecter un
// changement de contenu (pas un usage cryptographique).
function fnv1aHash(bytes) {
  let hash = 0x811c9dc5;
  for (let i = 0; i < bytes.length; i++) {
    hash ^= bytes[i];
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16);
}

async function readActiveDocTexture() {
  try {
    if (linkedDocId == null) return; // rien à lire si aucun doc n'est lié

    // Vérifie que le document lié existe toujours parmi les docs ouverts
    const docs = app.documents;
    const doc = docs.find(d => d.id === linkedDocId);
    if (!doc) {
      dbg("Document lié (" + linkedDocName + ") fermé — déliaison", "warn");
      linkedDocId = null;
      linkedDocName = null;
      return;
    }

    dbg("Lecture pixels du document lié : " + doc.name, "dim");

    const result = await core.executeAsModal(async () => {
      const pixels = await imaging.getPixels({
        documentID: doc.id,
        targetSize: {
          width:  Math.min(doc.width, 1024),
          height: Math.min(doc.height, 1024),
        },
      });

      const componentData = await pixels.imageData.getData({ chunky: true });
      const w = pixels.imageData.width;
      const h = pixels.imageData.height;
      const comps = pixels.imageData.components; // 3 (RGB) ou 4 (RGBA) selon le doc

      // On copie les données AVANT de dispose() le buffer source
      const copy = new Uint8Array(componentData.buffer ? componentData.buffer.slice(0) : componentData.slice(0));
      pixels.imageData.dispose();
      return { data: copy, w, h, comps };
    }, { commandName: "Read texture" });

    // Hash FNV-1a sur l'ensemble du buffer — rapide même sur de grosses textures,
    // et détecte un vrai changement n'importe où dans l'image (pas juste le 1er/dernier octet).
    const fingerprint = `${result.w}x${result.h}x${result.comps}:${result.data.length}:${fnv1aHash(result.data)}`;

    if (fingerprint !== lastTextureFingerprint) {
      lastTextureFingerprint = fingerprint;
      document.getElementById("texture-badge").textContent = "Texture liée : " + doc.name;
      document.getElementById("texture-badge").style.display = "block";
      dbg(`Texture mise à jour : ${doc.name} (${result.w}x${result.h}, ${result.comps} canaux)`, "ok");
      applyTextureToIframe(result);
    } else {
      dbg("Pixels lus mais identiques à la dernière capture (pas de changement)", "dim");
    }
  } catch(e) {
    let errInfo = "inconnu";
    try {
      if (e && e.message) errInfo = e.message;
      else if (e && typeof e.toString === "function" && e.toString() !== "[object Object]") errInfo = e.toString();
      else errInfo = JSON.stringify(e, Object.getOwnPropertyNames(e || {}));
    } catch { /* garde "inconnu" */ }
    dbg("Watch erreur : " + errInfo, "warn");
    console.error("Watch erreur (objet complet) :", e);
  }
}

// Convertit un Uint8Array en base64 — utilisé pour transmettre les pixels bruts
// à la webview de façon sûre via postMessage (string, pas de TypedArray à cloner).
function bytesToBase64(bytes) {
  let binary = "";
  const chunkSize = 0x8000; // éviter d'exploser la pile avec apply() sur de gros buffers
  for (let i = 0; i < bytes.length; i += chunkSize) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunkSize));
  }
  return btoa(binary);
}

function applyTextureToIframe(result) {
  try {
    const base64 = bytesToBase64(new Uint8Array(result.data.buffer || result.data));
    webviewEl.postMessage({
      type: "texture-raw",
      width: result.w,
      height: result.h,
      comps: result.comps,
      base64: base64,
    });
    dbg("Texture (pixels bruts) envoyée à la vue 3D", "ok");
  } catch (e) {
    dbg("Erreur postMessage vers webview : " + e.message, "err");
  }
}

// Debounce simple : évite de relancer 50 lectures par seconde pendant un coup de pinceau continu.
let pendingReadTimer = null;
function scheduleReadActiveDocTexture(delayMs = 250) {
  if (pendingReadTimer) clearTimeout(pendingReadTimer);
  pendingReadTimer = setTimeout(() => {
    pendingReadTimer = null;
    readActiveDocTexture();
  }, delayMs);
}

async function setupPSListeners() {
  let listenerOK = false;
  try {
    // "historyStateChanged" se déclenche à quasi chaque action d'édition
    // (pinceau, gomme, calque, etc.), pas seulement à la sauvegarde.
    await action.addNotificationListener(
      ["historyStateChanged", "save", "saveAs"],
      async (event) => {
        scheduleReadActiveDocTexture(200);
      }
    );
    listenerOK = true;
    dbg("Listeners PS actifs (historyStateChanged / save) — mise à jour en direct", "ok");
  } catch(e) {
    dbg("addNotificationListener indisponible : " + (e && e.message ? e.message : e), "warn");
  }

  // Polling de secours, actif uniquement quand un document est lié — couvre les cas
  // où historyStateChanged ne se déclenche pas pour certains outils/contextes.
  watchInterval = setInterval(() => {
    if (linkedDocId != null) scheduleReadActiveDocTexture(50);
  }, listenerOK ? 1500 : 600);

  if (!listenerOK) dbg("Polling de secours actif (600ms)", "warn");
}

// ─────────────────────────────────────────────
// UI EVENTS
// ─────────────────────────────────────────────

// Dropdowns toolbar (Importer / Outils) — un seul ouvert à la fois,
// se ferme au clic sur un item ou ailleurs dans le document.
const dropdowns = [
  { trigger: "btn-menu-import", menu: "menu-import" },
  { trigger: "btn-menu-tools",  menu: "menu-tools"  },
];

function closeAllDropdowns() {
  dropdowns.forEach(d => document.getElementById(d.menu).classList.add("hidden"));
}

dropdowns.forEach(({ trigger, menu }) => {
  document.getElementById(trigger).addEventListener("click", (e) => {
    e.stopPropagation();
    const menuEl = document.getElementById(menu);
    const isOpen = !menuEl.classList.contains("hidden");
    closeAllDropdowns();
    if (!isOpen) menuEl.classList.remove("hidden");
  });
});

// Ferme tout dropdown ouvert si on clique ailleurs dans le panneau,
// et ferme aussi le menu automatiquement après le clic sur un item interne.
document.addEventListener("click", (e) => {
  const isTrigger = e.target.classList.contains("dropdown-trigger");
  const insideMenu = e.target.closest(".dropdown-menu");
  if (!isTrigger && !insideMenu) closeAllDropdowns();
});
document.querySelectorAll(".dropdown-item").forEach(item => {
  item.addEventListener("click", closeAllDropdowns);
});

const debugPanel = document.getElementById("debug-panel");
document.getElementById("btn-debug").addEventListener("click", () => {
  debugPanel.classList.toggle("hidden");
  document.getElementById("btn-debug").classList.toggle("active");
});
document.getElementById("btn-clear-log").addEventListener("click", () => debugLog.innerHTML = "");


const configModal = document.getElementById("config-modal");
document.getElementById("btn-config").addEventListener("click", () => {
  document.getElementById("cfg-bridgepath").value = config.bridgePath || "";
  document.getElementById("cfg-port").value = config.exchangeDir || "";
  document.getElementById("cfg-cwdir").value = config.cwDir || "";
  showModal(configModal);
});
document.getElementById("btn-cfg-cancel").addEventListener("click", () => hideModal(configModal));
document.getElementById("btn-cfg-save").addEventListener("click", async () => {
  config.bridgePath  = document.getElementById("cfg-bridgepath").value.trim();
  config.exchangeDir = document.getElementById("cfg-port").value.trim();
  config.cwDir       = document.getElementById("cfg-cwdir").value.trim();
  cachedExchangeFolder = null;
  await saveConfig();
  hideModal(configModal);
  dbg("Config sauvegardée", "ok");
});

document.getElementById("btn-browse-bridge").addEventListener("click", async () => {
  try {
    const file = await uxpFS.getFileForOpening({ types: storage.fileTypes.all });
    if (file) document.getElementById("cfg-bridgepath").value = file.nativePath;
  } catch (e) {
    dbg("Sélection bridge annulée/erreur : " + e.message, "warn");
  }
});

document.getElementById("btn-browse-exchange").addEventListener("click", async () => {
  try {
    const folder = await uxpFS.getFolder();
    if (folder) {
      document.getElementById("cfg-port").value = folder.nativePath;
      cachedExchangeFolder = folder; // évite une 2e sélection au prochain import
    }
  } catch (e) {
    dbg("Sélection dossier annulée/erreur : " + e.message, "warn");
  }
});

document.getElementById("btn-browse-cwdir").addEventListener("click", async () => {
  try {
    const folder = await uxpFS.getFolder();
    if (folder) document.getElementById("cfg-cwdir").value = folder.nativePath;
  } catch (e) {
    dbg("Sélection dossier CodeWalker annulée/erreur : " + e.message, "warn");
  }
});

// ─────────────────────────────────────────────
// LANCER LE BRIDGE — écrit bridge_config.json à côté de l'exe (le bridge lit
// ce fichier au démarrage, pas d'argument cmd nécessaire), puis ouvre l'exe.
// ─────────────────────────────────────────────
let cachedBridgeFolder = null;

document.getElementById("btn-launch-bridge").addEventListener("click", async () => {
  if (!config.bridgePath) {
    dbg("Chemin du bridge non configuré — ouvre ⚙ Configuration pour le définir", "warn");
    showModal(configModal);
    return;
  }
  if (!config.exchangeDir) {
    dbg("Dossier d'échange non configuré — ouvre ⚙ Configuration pour le définir", "warn");
    showModal(configModal);
    return;
  }

  // Étape 1 : écrire bridge_config.json dans le dossier du bridge.
  try {
    if (!cachedBridgeFolder) {
      dbg("Sélectionne le dossier contenant YdrBridge.exe (une fois par session)", "warn");
      cachedBridgeFolder = await uxpFS.getFolder();
      if (!cachedBridgeFolder) { dbg("Sélection annulée", "warn"); return; }
    }

    const cfgJson = JSON.stringify({
      exchangeDir: config.exchangeDir,
      cwDir: config.cwDir || "",
    }, null, 2);

    let cfgFile = await cachedBridgeFolder.getEntry("bridge_config.json").catch(() => null);
    if (!cfgFile) cfgFile = await cachedBridgeFolder.createFile("bridge_config.json", { overwrite: true });
    await cfgFile.write(cfgJson, { format: storage.formats.utf8 });
    dbg("bridge_config.json écrit : " + cfgFile.nativePath, "ok");
  } catch (e) {
    dbg("Erreur écriture bridge_config.json : " + e.message, "err");
    return;
  }

  // Étape 2 : lancer l'exe (qui va lire ce fichier de config au démarrage).
  dbg("Tentative de lancement : " + config.bridgePath, "info");

  let result;
  try {
    result = await shell.openPath(config.bridgePath);
  } catch (e) {
    result = e.message || "erreur inconnue";
  }

  if (result === "") {
    dbg("Bridge lancé avec succès", "ok");
    return;
  }

  dbg("Échec lancement direct (" + result + ") — ouverture du dossier parent...", "warn");
  const lastSep = Math.max(config.bridgePath.lastIndexOf("\\"), config.bridgePath.lastIndexOf("/"));
  const parentDir = lastSep > 0 ? config.bridgePath.substring(0, lastSep) : config.bridgePath;

  let result2;
  try {
    result2 = await shell.openPath(parentDir);
  } catch (e2) {
    result2 = e2.message || "erreur inconnue";
  }

  if (result2 === "") {
    dbg("Dossier ouvert : " + parentDir, "ok");
  } else {
    dbg("Impossible d'ouvrir le dossier non plus : " + result2, "err");
  }
});

// ─────────────────────────────────────────────
// DOC PICKER — lier explicitement un document PS comme texture du prop
// ─────────────────────────────────────────────
const docpickerModal = document.getElementById("docpicker-modal");
const docpickerList  = document.getElementById("docpicker-list");

function refreshDocPickerList() {
  docpickerList.innerHTML = "";
  const docs = app.documents;

  if (!docs || docs.length === 0) {
    docpickerList.innerHTML = '<p style="color:#666; font-size:11px;">Aucun document Photoshop ouvert.</p>';
    return;
  }

  docs.forEach(doc => {
    const row = document.createElement("button");
    row.className = "tb-btn";
    row.style.textAlign = "left";
    row.style.justifyContent = "flex-start";
    const isLinked = doc.id === linkedDocId;
    row.textContent = doc.name + (isLinked ? "  (lié actuellement)" : "");
    if (isLinked) row.classList.add("active");

    row.addEventListener("click", () => {
      linkedDocId = doc.id;
      linkedDocName = doc.name;
      lastTextureFingerprint = null; // force une relecture même si même contenu
      dbg("Document lié : " + doc.name + " (id=" + doc.id + ")", "ok");
      hideModal(docpickerModal);
      // Lecture immédiate après liaison
      readActiveDocTexture();
    });

    docpickerList.appendChild(row);
  });
}

document.getElementById("btn-link-doc").addEventListener("click", () => {
  refreshDocPickerList();
  showModal(docpickerModal);
});
document.getElementById("btn-docpicker-cancel").addEventListener("click", () => {
  hideModal(docpickerModal);
});
document.getElementById("btn-docpicker-refresh").addEventListener("click", () => {
  refreshDocPickerList();
});

// ─────────────────────────────────────────────
// YTD — importer un .ytd, choisir une texture, l'appliquer au modèle
// ─────────────────────────────────────────────
const ytdpickerModal  = document.getElementById("ytdpicker-modal");
const ytdpickerList   = document.getElementById("ytdpicker-list");
const ytdpickerStatus = document.getElementById("ytdpicker-status");
let currentYtdId = null;

document.getElementById("btn-import-ytd").addEventListener("click", async () => {
  try {
    const file = await uxpFS.getFileForOpening({ types: ["ytd"] });
    if (!file) return;

    dbg("YTD sélectionné : " + file.nativePath, "info");
    ytdpickerStatus.textContent = "Chargement des textures...";
    ytdpickerList.innerHTML = "";
    showModal(ytdpickerModal);

    const result = await requestConversion(file.nativePath);
    if (result.kind !== "ytd" || !result.textures) {
      throw new Error("Réponse inattendue du bridge pour ce .ytd");
    }

    currentYtdId = result.ytdId;
    dbg(`YTD chargé : ${result.textures.length} texture(s)`, "ok");
    ytdpickerStatus.textContent = `${result.textures.length} texture(s) — choisis-en une à appliquer :`;

    if (result.textures.length === 0) {
      ytdpickerList.innerHTML = '<p style="color:#666; font-size:11px;">Aucune texture dans ce fichier.</p>';
      return;
    }

    result.textures.forEach(texName => {
      const row = document.createElement("div");
      row.style.display = "flex";
      row.style.gap = "4px";

      const previewBtn = document.createElement("button");
      previewBtn.className = "tb-btn";
      previewBtn.style.flex = "1";
      previewBtn.style.textAlign = "left";
      previewBtn.style.justifyContent = "flex-start";
      previewBtn.textContent = texName;
      previewBtn.title = "Aperçu sur le modèle 3D";
      previewBtn.addEventListener("click", () => applyYtdTexture(currentYtdId, texName));

      const importBtn = document.createElement("button");
      importBtn.className = "tb-btn";
      importBtn.textContent = "Importer";
      importBtn.title = "Importer comme nouveau calque dans Photoshop";
      importBtn.addEventListener("click", () => importYtdTextureAsLayer(currentYtdId, texName));

      row.appendChild(previewBtn);
      row.appendChild(importBtn);
      ytdpickerList.appendChild(row);
    });

  } catch (e) {
    dbg("Import YTD erreur : " + e.message, "err");
    ytdpickerStatus.textContent = "Erreur : " + e.message;
  }
});

async function applyYtdTexture(ytdId, texName) {
  try {
    dbg(`Aperçu : récupération des pixels de "${texName}"...`, "info");
    const url = `http://localhost:8743/ytd-pixels?id=${encodeURIComponent(ytdId)}&name=${encodeURIComponent(texName)}`;
    const res = await fetch(url);
    if (!res.ok) {
      const errTxt = await res.text().catch(() => "");
      throw new Error(`HTTP ${res.status} : ${errTxt}`);
    }

    const w = parseInt(res.headers.get("X-Texture-Width"), 10);
    const h = parseInt(res.headers.get("X-Texture-Height"), 10);
    const comps = parseInt(res.headers.get("X-Texture-Comps"), 10) || 4;
    const buffer = await res.arrayBuffer();

    dbg(`Pixels reçus : ${w}x${h}, ${comps} canaux, ${buffer.byteLength} octets`, "ok");

    applyTextureToIframe({ data: new Uint8Array(buffer), w, h, comps });
    // Modal volontairement laissé ouvert — l'utilisateur peut prévisualiser
    // plusieurs textures avant de cliquer "Importer" sur celle qui lui convient.
    document.getElementById("texture-badge").textContent = "Aperçu .ytd : " + texName;
    document.getElementById("texture-badge").style.display = "block";

  } catch (e) {
    dbg("applyYtdTexture erreur : " + e.message, "err");
  }
}

async function importYtdTextureAsLayer(ytdId, texName) {
  try {
    dbg(`Import calque : récupération des pixels de "${texName}"...`, "info");
    const url = `http://localhost:8743/ytd-pixels?id=${encodeURIComponent(ytdId)}&name=${encodeURIComponent(texName)}`;
    const res = await fetch(url);
    if (!res.ok) {
      const errTxt = await res.text().catch(() => "");
      throw new Error(`HTTP ${res.status} : ${errTxt}`);
    }

    const w = parseInt(res.headers.get("X-Texture-Width"), 10);
    const h = parseInt(res.headers.get("X-Texture-Height"), 10);
    const comps = parseInt(res.headers.get("X-Texture-Comps"), 10) || 4;
    const buffer = await res.arrayBuffer();
    dbg(`Pixels reçus : ${w}x${h}, ${comps} canaux`, "ok");

    await core.executeAsModal(async () => {
      let doc = app.activeDocument;
      if (!doc) {
        dbg("Aucun document ouvert — création d'un nouveau document", "info");
        doc = await app.createDocument({
          width: w,
          height: h,
          resolution: 72,
          mode: "RGBColorMode",
        });
      }

      const layer = await doc.createLayer();
      layer.name = texName;

      const imageData = await imaging.createImageDataFromBuffer(new Uint8Array(buffer), {
        width: w,
        height: h,
        components: comps,
        chunky: true,
        colorSpace: "RGB",
      });

      try {
        await imaging.putPixels({
          documentID: doc.id,
          layerID: layer.id,
          imageData: imageData,
        });
      } finally {
        imageData.dispose();
      }
    }, { commandName: "Importer texture .ytd" });

    dbg(`Calque "${texName}" créé dans Photoshop`, "ok");
    hideModal(ytdpickerModal);

  } catch (e) {
    dbg("importYtdTextureAsLayer erreur : " + e.message, "err");
  }
}

document.getElementById("btn-ytdpicker-cancel").addEventListener("click", () => {
  hideModal(ytdpickerModal);
});

window.addEventListener("message", (event) => {
  if (event.data && event.data.type === "log") {
    dbg("Iframe: " + event.data.msg, event.data.level || "info");
  }
});

// ─────────────────────────────────────────────
// INIT
// ─────────────────────────────────────────────
(async () => {
  dbg("YDR Preview démarré", "info");
  await loadConfig();
  await setupPSListeners();
  dbg("Dossier d'échange configuré : " + config.exchangeDir, "info");
  dbg('Lance le bridge : YdrBridge.exe --watch "' + config.exchangeDir + '" "<dossier_codewalker>"', "warn");
  dbg("Prêt — importe un .ydr pour commencer", "ok");
})();