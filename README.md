# Jellyfin ABR HLS Cinema

![Version](https://img.shields.io/badge/Version-1.0.0-blue) ![Jellyfin](https://img.shields.io/badge/Jellyfin-10.9%2B-purple)

Dieses Plugin bringt **echtes Adaptive Bitrate Streaming (ABR)** auf deinen Jellyfin Server – genau wie bei Netflix oder YouTube.

Anstatt Videos live während des Abspielens zu transkodieren (was viel CPU frisst und beim Spulen hakt), erstellt dieses Plugin **im Hintergrund** optimierte Versionen deiner Filme in verschiedenen Qualitätsstufen (1080p, 720p, 480p).

## ✨ Features

* **Butterweiches Streaming:** Sofortiger Start und spulen ohne Verzögerung.
* **Adaptive Qualität:** Der Player wechselt automatisch die Qualität je nach Internetgeschwindigkeit (Auto-Modus).
* **Qualitäts-Wahl:** Manuelles Auswahlmenü (Zahnrad-Icon) im Player.
* **CPU-Schonend:** Beim Abspielen wird **0% CPU** verbraucht (Direct Play).
* **Hintergrund-Verarbeitung:** Nutzt FFmpeg mit niedriger Priorität, um den Server nicht zu blockieren.
* **FireTV Support:** Spezielle Profile für FireTV 4K (HEVC/HDR).

## ⚠️ Vorraussetzungen

1.  **Speicherplatz:** Da für jeden Film mehrere Versionen erstellt werden, benötigt dieses Plugin zusätzlichen Speicherplatz im `data`-Ordner von Jellyfin.
2.  **FFmpeg:** Muss auf dem System/im Container installiert sein (Jellyfin bringt das normalerweise mit).

---

## 🚀 Installation

### Methode A: Über Repository (Empfohlen)
Wenn du dieses Projekt auf GitHub veröffentlichst (wie besprochen), ist das der einfachste Weg:

1.  Öffne dein Jellyfin Dashboard.
2.  Gehe zu **Katalog** -> **Einstellungen (Zahnrad)** -> **Repositorys**.
3.  Klicke auf `(+)` und füge hinzu:
    * **Name:** ABR Cinema
    * **URL:** `https://DEIN_GITHUB_USER.github.io/DEIN_REPO_NAME/manifest.json`
4.  Gehe zurück zum Katalog und installiere **ABR HLS Cinema**.
5.  Starte Jellyfin neu.

### Methode B: Manuell (DLL)
1.  Lade die `Jellyfin.ABRHls.dll` aus den Releases herunter.
2.  Kopiere die Datei in den Plugins-Ordner deines Servers:
    * Linux/Docker: `/config/plugins/Jellyfin.ABRHls/Jellyfin.ABRHls.dll`
    * Windows: `%ProgramData%\Jellyfin\Server\plugins\Jellyfin.ABRHls\Jellyfin.ABRHls.dll`
3.  Starte Jellyfin neu.

---

## ⚙️ Konfiguration

Gehe im Dashboard auf **Plugins** -> **ABR HLS Cinema**.

* **Output Pfad:** Wo sollen die Videodateien gespeichert werden? (Standard: `data/abrhls`)
* **Auto On Library Scan:**
    * ❌ **Aus (Standard):** Empfohlen. Neue Filme werden ignoriert, bis du den Scan manuell auslöst.
    * ✅ **An:** Jeder neu hinzugefügte Film wird sofort automatisch konvertiert.
* **FFmpeg Pfad:** Leer lassen, um den von Jellyfin zu nutzen.

---

## 🎬 Nutzung

### 1. Videos vorbereiten
Das Plugin muss die Videos erst "verpacken". Das passiert automatisch (wenn aktiviert) oder wenn du den geplanten Task ausführst:
* Dashboard -> **Geplante Aufgaben** -> **Library Watcher** (oder ähnlich benannt) -> Play drücken.

*Hinweis: Die Konvertierung eines 4K Films kann je nach CPU-Leistung einige Zeit dauern.*

### 2. Der Player
Das Plugin bringt einen eigenen Web-Player mit, da der Standard-Jellyfin-Player noch kein manuelles Qualitäts-Menü unterstützt.

Rufe den Player im Browser auf:

`http://DEIN-SERVER-IP:8096/web/abr-player.html?itemId=DIE-ID-DES-FILMS`

* **Wo finde ich die ID?**
    Klicke in Jellyfin auf einen Film. Die ID steht oben in der URL (z.B. `.../details?id=34234...`).
* **Tipp:** Du kannst dir Lesezeichen für deine Lieblingsfilme setzen.

### 3. Qualitäts-Menü
Im Player findest du unten rechts ein Zahnrad ⚙️.
* **Automatisch:** Passt sich deiner Bandbreite an.
* **1080p / 720p / ...:** Erzwingt eine feste Qualität.

---

## 🛠 Troubleshooting

**Der Player zeigt "Stream wird generiert..."**
FFmpeg arbeitet noch. Überprüfe die Jellyfin Logs, um den Fortschritt zu sehen.

**Ich sehe keine Qualitäten im Menü**
Prüfe, ob der Output-Ordner Schreibrechte hat und ob FFmpeg korrekt arbeitet.

**Server ist langsam**
Die Generierung der HLS-Dateien ist rechenintensiv. Stelle sicher, dass du nicht 10 Filme gleichzeitig hinzufügst, wenn "Auto Scan" aktiviert ist.