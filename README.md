### 🎨 YDR-YDD-LivePreview-Photoshop

Plugin UXP pour Photoshop couplé à un pont C# (.exe) permettant de visualiser des modèles 3D de GTA V (`.ydr` / `.ydd`) directement dans un panneau Photoshop, et de leur appliquer des textures en temps réel.

![Status](https://img.shields.io/badge/Status-Working-brightgreen)
![Photoshop](https://img.shields.io/badge/Adobe%20Photoshop-2024%20%2B-blue)
![CodeWalker](https://img.shields.io/badge/CodeWalker-dev48-orange)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)

## 📖 Contexte & Problématique

Avec la transition vers UXP dans Photoshop (notamment la version 2025), Adobe a drastiquement restreint l'utilisation de `WebGL` dans les panneaux natifs et bloqué les requêtes réseau locales (`fetch` vers `localhost`). Cela rend impossible l'affichage d'un modèle 3D classique via Three.js directement dans le DOM du plugin.

Ce projet contourne ces limitations en utilisant une architecture hybride intelligente :
1. **Échange de fichiers local** (Watch Folder) au lieu de requêtes HTTP bloquées par UXP.
2. **Iframe HTML** servie par un serveur web local C# léger, contournant le blocage WebGL d'UXP tout en respectant les règles de sécurité (CSP/Sandbox).

## ✨ Fonctionnalités

- 📂 **Import .ydr / .ydd** : Conversion des modèles CodeWalker en `.glb` via `CodeWalker.Core.dll` et `SharpGLTF`.
- 🖼️ **Visualisation 3D** : Rendu du modèle dans Photoshop via Three.js (WebGL2) à travers une iframe locale.
- 🎨 **Texture Live Update** : Peignez votre texture dans Photoshop, sauvegardez, et le modèle 3D se met à jour instantanément avec votre image (via `postMessage` et `executeAsModal`).
- 🖱️ **Contrôles 3D** : Orbite, zoom et pan intégrés directement dans le panneau.

## 🏗️ Architecture du Projet

Le projet est divisé en deux parties indépendantes :

### 1. Le Plugin UXP (`plugin/`)
Panneau Photoshop codé en HTML/JS. Il gère l'interface, lit le document actif de Photoshop pour extraire les pixels de la texture, et communique avec le pont C# via un dossier d'échange (`request_*.json` / `response_*.json`).

### 2. Le Pont C# (`bridge/`)
Application console .NET 8 (`YdrBridge.exe`). 
- **Mode Watch** : Surveille un dossier pour les requêtes de conversion du plugin.
- **Conversion** : Utilise les bibliothèques de CodeWalker pour parser le `.ydr` et génère un `.glb`.
- **Serveur HTTP** : Héberge le modèle converti et une page web Three.js (le "Viewer") accessible via une iframe locale (`http://127.0.0.1:8743/viewer`).

## 🚀 Installation & Utilisation

### Prérequis
- Adobe Photoshop 2024 ou 2025 (UXP)
- .NET 8 Runtime (pour lancer le bridge)
- UXP Developer Tool (UDT) installé via Adobe Creative Cloud
- Les fichiers `CodeWalker.Core.dll` et `SharpDX.Mathematics.dll` (placés dans le dossier du bridge)

### Étape 1 : Lancer le Bridge C#
Le bridge doit tourner en arrière-plan pendant l'utilisation du plugin.

1. Compilez le projet C# ou utilisez la version précompilée dans `bridge/publish/`.
2. Assurez-vous que les fichiers `three.min.js` et `GLTFLoader.js` sont bien présents à côté de `YdrBridge.exe`.
3. Créez un dossier d'échange vide (ex: `D:\ydrpreview_exchange`).
4. Lancez l'exécutable en ligne de commande :

```bash
YdrBridge.exe --watch "D:\ydrpreview_exchange" "C:\Chin\Vers\CodeWalker30_dev48"
```
*(Remplacez le dernier paramètre par le chemin vers votre dossier CodeWalker contenant les clés de déchiffrement GTA5).*

### Étape 2 : Installer le plugin Photoshop
1. Ouvrez **UXP Developer Tool** (UDT).
2. Cliquez sur **Add Plugin...** et sélectionnez le fichier `manifest.json` dans le dossier `plugin/`.
3. Cliquez sur **Load** pour démarrer le plugin dans Photoshop.

### Étape 3 : Workflow d'utilisation
1. Ouvrez le panneau **YDR Preview** dans Photoshop (menu `Plugins > YDR Preview`).
2. Cliquez sur le bouton **📂 Import YDR** et sélectionnez votre fichier `.ydr` ou `.ydd`.
3. Photoshop va vous demander de sélectionner un dossier (permission UXP) : choisissez le dossier d'échange créé à l'étape 1 (ex: `D:\ydrpreview_exchange`).
4. Le modèle 3D apparaît dans le panneau !
5. Ouvrez/peignez une texture (`.dds` ou `.png`) dans Photoshop, faites `Ctrl+S` (ou `Cmd+S`) : la texture s'applique automatiquement sur le modèle 3D en temps réel.

## 🛠️ Stack Technique

- **UXP / Photoshop API** : `core.executeAsModal`, `action.addNotificationListener`, `localFileSystem`, `postMessage`.
- **C# .NET 8** : `HttpListener`, `FileSystemWatcher` (Polling), Reflection sur CodeWalker.
- **3D / Web** : `Three.js` (r160), `GLTFLoader`, `OrbitControls` (Custom).
- **Librairies externes** : `CodeWalker.Core`, `SharpGLTF`, `SharpDX.Mathematics`.

## 🤝 Contribution

Les contributions (Pull Requests) sont les bienvenues ! Que ce soit pour améliorer le parsing des matériaux complexes de CodeWalker, optimiser la vitesse de conversion, ou améliorer l'UI du plugin UXP.
