# GTA Prop Viewer — Photoshop ⇄ CodeWalker Live Preview

![Status](https://img.shields.io/badge/Status-Working-brightgreen)
![Photoshop](https://img.shields.io/badge/Photoshop-UXP%20Plugin-31A8FF?style=flat&logo=adobephotoshop&logoColor=white)
![Platform](https://img.shields.io/badge/platform-Windows-0078D6?style=flat&logo=windows&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat&logo=dotnet&logoColor=white)
![License](https://img.shields.io/badge/license-Proprietary-lightgrey?style=flat)
![CodeWalker](https://img.shields.io/badge/CodeWalker-dev48-orange)
![Claude](https://img.shields.io/badge/Claude-black?logo=claude)


Plugin Photoshop (UXP) qui affiche en 3D, dans un panneau natif, des fichiers
GTA V `.ydr` / `.ydd` / `.yft`, applique en direct la texture en cours
d'édition dans Photoshop, et permet d'extraire/réimporter des textures
depuis un dictionnaire `.ytd`.

La conversion des formats binaires GTA V se fait via un petit bridge C#
externe (`YdrBridge.exe`) qui s'appuie sur [CodeWalker](https://github.com/dexyfex/CodeWalker)
et SharpGLTF.

---

## Architecture

```
┌─────────────────────────────┐         fichiers JSON          ┌──────────────────────────────┐
│   Plugin UXP (Photoshop)    │ ───────────────────────────►   │   YdrBridge.exe (--watch)     │
│                              │   request_<id>.json            │                                │
│  index.html / index.js      │ ◄───────────────────────────   │  CodeWalker.Core + SharpGLTF  │
│  <webview> Three.js viewer  │   response_<id>.json           │  Serveur HTTP local (8743)    │
└──────────────┬───────────────┘                                 └───────────────┬──────────────┘
               │  HTTP localhost:8743                                            │
               │  /viewer  /model  /ytd-pixels  /ytd-dds  /ping                  │
               └──────────────────────────────────────────────────────────────────┘
```

- **Le plugin** écrit une requête sur disque (chemin du fichier à convertir),
  car UXP ne permet pas de lancer un exécutable avec des arguments
  personnalisés ni d'utiliser `fetch()` librement vers un process externe
  sans ce détour fichier pour le déclenchement initial.
- **Le bridge** surveille ce dossier, convertit le fichier (CodeWalker →
  glTF via SharpGLTF), garde le résultat en mémoire, et sert le rendu 3D
  (Three.js) via un petit serveur HTTP local consommé par une `<webview>`
  UXP — seul composant capable d'afficher du WebGL dans un panneau
  Photoshop (le canvas UXP natif ne le supporte pas).

---

## Structure du dépôt

```
ydr-tool/
├── plugin/                      ← Plugin UXP (à charger dans Photoshop)
│   ├── manifest.json
│   ├── index.html
│   ├── index.js
│   ├── three.min.js             ← Three.js r128 (build classique, pas ES module)
│   ├── GLTFLoader.js            ← loader Three.js r128
│   └── icons/
│       ├── icon_D.png / icon_D@2x.png   (icône plugin, thème sombre)
│       ├── icon_N.png / icon_N@2x.png   (icône plugin, thème clair)
│       ├── dark.png / dark@2x.png       (icône panneau, thème sombre)
│       └── light.png / light@2x.png     (icône panneau, thème clair)
│
└── bridge/                      ← Bridge C# (exécuté séparément, hors Photoshop)
    ├── YdrBridge.cs
    ├── YdrBridge.csproj
    └── publish/                 ← généré par la compilation (voir ci-dessous)
        ├── YdrBridge.exe
        ├── three.min.js          (copié ici, servi par le bridge à la webview)
        ├── GLTFLoader.js         (copié ici, servi par le bridge à la webview)
        └── bridge_config.json    (généré au premier lancement, ou via le plugin)
```

---

## Prérequis

| Outil | Usage |
|---|---|
| Windows 10/11 | seule plateforme supportée actuellement |
| Adobe Photoshop 2025+ (UXP, manifest v6) | hôte du plugin |
| [Adobe UXP Developer Tool](https://developer.adobe.com/photoshop/uxp/) | chargement/packaging du plugin |
| [.NET 8 SDK](https://dotnet.microsoft.com/) | compilation du bridge |
| [CodeWalker](https://github.com/dexyfex/CodeWalker) (dossier d'installation) | fournit `CodeWalker.Core.dll` + `SharpDX.Mathematics.dll` |

---

## Installation — Bridge

1. Ouvre un terminal dans `bridge/`.
2. Compile en pointant vers ton dossier CodeWalker (qui contient `CodeWalker.Core.dll`) :

   ```bat
   dotnet publish YdrBridge.csproj -c Release -r win-x64 --self-contained true ^
     -p:PublishSingleFile=true ^
     -p:CW_DIR="D:\Chemin\Vers\CodeWalker" ^
     -o .\publish
   ```

3. Copie `three.min.js` et `GLTFLoader.js` (récupérés depuis `plugin/`) dans `bridge/publish/`.
4. Lance une première fois `YdrBridge.exe` directement (double-clic ou terminal) :
   il va créer un `bridge_config.json` vide à côté de lui et s'arrêter.
5. Remplis ce fichier (ou configure-le depuis le plugin, voir plus bas) :

   ```json
   {
     "exchangeDir": "D:\\chemin\\vers\\un\\dossier\\d_echange",
     "cwDir": "D:\\Chemin\\Vers\\CodeWalker"
   }
   ```

6. Relance `YdrBridge.exe`. Une fenêtre console doit rester ouverte
   (`Mode watch actif sur : ...`, `Serveur démarré : http://localhost:8743/`).

> Le bridge n'a besoin d'aucun argument en ligne de commande — toute sa
> configuration vient de `bridge_config.json`, lu au démarrage à côté de
> l'exécutable.

---

## Installation — Plugin

### Mode développement (UDT)

1. Ouvre **Adobe UXP Developer Tool**.
2. **Add Plugin** → sélectionne `plugin/manifest.json`.
3. **Load** le plugin, puis dans Photoshop : **Fenêtre → Modules externes → GTA Prop Viewer**.

### Mode packagé (.ccx)

1. Dans UDT, `•••` (menu Actions) → **Package**.
2. Choisis un dossier de sortie ; un fichier `*.ccx` est généré.
3. Double-clique sur le `.ccx` → Creative Cloud Desktop s'ouvre → accepte
   l'avertissement (plugin non vérifié, normal en développement) → **Install locally**.
4. Le plugin apparaît dans Photoshop sans passer par UDT.

---

## Utilisation

1. **Lance le bridge** : depuis le menu **Outils ▾** du panneau, bouton
   *Lancer le bridge* — il écrit `bridge_config.json` puis ouvre l'exécutable.
   À la première utilisation, un sélecteur de dossier demande où se trouve
   `YdrBridge.exe`.
2. **Configuration** (menu **Outils ▾ → Configuration**) :
   - chemin de `YdrBridge.exe`
   - dossier d'échange (fichiers de requête/réponse)
   - dossier CodeWalker (optionnel, nécessaire pour les fichiers chiffrés vanilla)
3. **Importer ▾ → Modèle** : sélectionne un `.ydr`, `.ydd` ou `.yft`.
4. **Outils ▾ → Lier une texture** : choisis le document Photoshop ouvert
   qui correspond à la texture du prop — toute modification (pinceau,
   calque, sauvegarde) se répercute en direct sur le modèle 3D.
5. **Importer ▾ → Texture (.ytd)** : charge un dictionnaire de textures,
   clique un nom pour prévisualiser sur le modèle, **Importer** pour créer
   un nouveau calque Photoshop à partir de cette texture.

---

## Formats supportés

| Extension | Description | Notes |
|---|---|---|
| `.ydr` | Drawable (prop simple) | |
| `.ydd` | Dictionnaire de drawables | tous les drawables sont fusionnés dans la même scène |
| `.yft` | Fragment (véhicules, objets destructibles) | seul le drawable principal + sous-pièces visibles sont exportés, pas la simulation physique |
| `.ytd` | Dictionnaire de textures | extraction/aperçu/import calque uniquement, pas de réinjection dans le `.ytd` |

Les géométries sans coordonnées UV (vertex-color only) sont affichées en
gris uni (`no_uv_flat`) ; seules les géométries texturées (`textured`)
reçoivent la texture liée depuis Photoshop.

---

## Limitations connues

- **WebGL** : UXP ne supporte que le canvas 2D nativement. Le rendu 3D
  passe obligatoirement par une `<webview>` (navigateur intégré), qui
  nécessite la permission `webview` dans le manifest et ne fonctionne que
  dans le flux normal du document (pas en overlay `position: fixed`,
  toujours rendue au-dessus par le compositeur natif).
- **`launchProcess`** : UXP ne permet pas de lancer un exécutable avec des
  arguments personnalisés. Le bridge doit donc lire sa configuration
  depuis un fichier plutôt que des arguments CLI.
- **Filtre de fichiers** : le sélecteur de fichiers Windows n'autorise pas
  de regrouper plusieurs extensions sous un seul nom de filtre — le
  plugin affiche donc tous les fichiers et valide l'extension après coup.
- **Windows uniquement** pour le moment (le bridge dépend de CodeWalker /
  SharpDX, et n'a pas été testé sur macOS).

---

## Dépannage rapide

| Symptôme | Cause probable |
|---|---|
| Import bloqué à l'étape 3 (polling infini) | Le bridge ne tourne pas, ou `exchangeDir` du plugin ≠ celui de `bridge_config.json` |
| Console bridge ferme instantanément, aucun log | DLL manquantes dans `publish/` après une recompilation — relancer un build propre (`rmdir /s /q bin obj` puis `dotnet publish`) |
| `NotImplementedException` sur une liste CodeWalker | Certains types `ResourcePointerList64<T>` de cette version de CodeWalker n'implémentent pas `GetEnumerator()` — utiliser `.data_items` à la place d'un `foreach` direct |
| Modèle gris partout, jamais texturé | Les géométries sans UV sont normalement grises (vertex-color only) ; vérifier dans les logs du bridge la répartition `texturée(s) / sans UV` |
| Échec de packaging : icônes manquantes | Le manifest doit déclarer un tableau `icons` au niveau racine **et** dans l'entrypoint `panel` |

---

## Remerciements

- [CodeWalker](https://github.com/dexyfex/CodeWalker) — parsing des formats GTA V
- [SharpGLTF](https://github.com/vpenades/SharpGLTF) — export glTF/GLB
- [Three.js](https://threejs.org/) — rendu 3D dans la webview
