<p align="center">
  <img src="src/Blitztext.Windows/Assets/Logo.png" width="96" height="96" alt="Blitztext für Windows">
</p>

<h1 align="center">Blitztext für Windows</h1>

<p align="center">
  <b>Sprechen. Text. Fertig.</b><br>
  Diktieren unter Windows, die Spracherkennung läuft komplett auf deinem PC.
</p>

Tastenkürzel halten, sprechen, loslassen: Blitztext macht aus deiner Sprache Text und fügt ihn dort ein, wo dein Cursor steht. Die Spracherkennung läuft lokal (Parakeet oder Whisper), kein Konto, kein Server, kein Audio verlässt den PC.

Dies ist eine Windows-Portierung von [Blitztext für macOS](https://github.com/cmagnussen/blitztext-app) (MIT). Nachbearbeitung, Einfügen am Cursor, Absätze, Halluzinationsfilter, Wiederherstellen der Zwischenablage und die Anweisungen der KI-Modi folgen dem Original.

## So funktioniert's

1. **Halten:** `Strg + Win` gedrückt halten.
2. **Sprechen**, in jeder Sprache, die das Modell kennt. Unten am Bildschirm zeigt eine kleine lila Anzeige, dass Blitztext zuhört.
3. **Loslassen.** Blitztext transkribiert auf deinem PC.
4. **Fertig.** Der Text erscheint am Cursor, passend zum Text drumherum. Deine Zwischenablage ist danach wieder wie vorher.

## Modi

| Modus | Tastenkürzel | Was passiert |
|---|---|---|
| **Blitztext** | `Strg + Win` | Sprache zu exaktem Text |
| **Blitztext+** | `Strg + Umschalt + Win` | Sprache zu sauber geschriebenem Text |
| **Blitztext $%&!** | `Strg + Alt + Win` | Frust zu einer ruhigen, wirksamen Nachricht |
| **Blitztext :)** | `Alt + Win` | Text mit passenden Emojis |

Die Reihenfolge beim Drücken ist egal: Kommt während der Aufnahme eine weitere Zusatztaste dazu, wechselt Blitztext in den passenden Modus. Wer `Strg + Win + →` für virtuelle Desktops nutzt, merkt nichts davon: Eine weitere Taste bricht die Aufnahme sofort ab. Jedes Kürzel lässt sich in den Einstellungen ändern (z. B. `Strg + Alt + D`, `F13` oder nur Zusatztasten wie `Strg + Umschalt`). **Halten** oder **Drücken** (einmal starten, nochmal oder `Esc` zum Stoppen) ist einstellbar.

## Funktionen

- **Lokale Spracherkennung:** Parakeet v3 (NVIDIA Parakeet TDT 0.6B v3 über sherpa-onnx, schnell auf jeder CPU, 25 europäische Sprachen) oder OpenAI Whisper über whisper.cpp (Small oder Large v3 Turbo, rund 100 Sprachen). Beide erkennen die Sprache selbst.
- **KI-Modi:** Blitztext+, $%&! und :) mit einem Modell-Anbieter deiner Wahl: OpenAI, Anthropic (Claude), Google Gemini, Mistral, OpenRouter oder ein lokaler Server wie **Ollama** oder **LM Studio** (dann bleibt alles auf dem PC).
- **Passt sich an:** Leerzeichen und Groß-/Kleinschreibung folgen dem Text am Cursor, auch mitten im Satz. Blitztext liest dazu das Textfeld über UI Automation, Passwortfelder nie.
- **Modus je App oder Website:** z. B. Outlook und Gmail immer Blitztext+, Slack mit Emojis, alles andere wortgenau.
- **Aufnahme sichtbar:** Die lila Anzeige unten bewegt sich mit deiner Stimme, ein Klick stoppt die Aufnahme. Sie nimmt nie den Fokus.
- **Ton aus beim Aufnehmen:** Musik oder Podcast sind stumm, solange du sprichst.
- **Zwischenablage bleibt:** Das Diktat wird mit verzögerter Bereitstellung angeboten; sobald die Ziel-App es gelesen hat, kommt dein vorheriger Inhalt zurück. Der Verlauf der Windows-Zwischenablage (`Win + V`) übernimmt Diktate nicht.
- **Sprachmemos und WhatsApp-Sprachnachrichten:** Audiodatei wählen (oder per „Öffnen mit“) und den Text bekommen, auf Wunsch mit Absätzen oder umgeschrieben.
- **Wörterbuch:** Eigennamen in deiner Schreibweise (mit Korrektur ähnlich klingender Wörter, die Windows-Rechtschreibprüfung schützt echte Wörter) und feste Ersetzungen („n acht n“ wird „n8n“).
- **Verlauf:** letzte Ergebnisse, mit Windows (DPAPI) für dein Konto verschlüsselt, nur auf diesem PC.
- **Sofortstart:** Das Mikrofon wartet vorbereitet und startet beim Tastendruck in wenigen Millisekunden, nach dem Loslassen nimmt Blitztext noch 0,3 s auf, damit das letzte Wort vollständig ist. Für Mikrofone, die lange zum Aufwachen brauchen (z. B. Bluetooth-Headsets), gibt es **Mikrofon bereithalten**: Dann ist auch der Moment vor dem Tastendruck dabei.
- **Mikrofon wählbar**, Autostart, Updates aus den GitHub-Releases.

## Installation

1. Unter [Releases](https://github.com/Marlon1694/Blitztext-Win/releases/latest) `Blitztext-Setup-x.y.z.exe` laden und starten. Die Installation braucht keine Administratorrechte. Alternativ die portable ZIP entpacken und `Blitztext.exe` starten.
2. Windows SmartScreen kann warnen, weil der Installer nicht signiert ist: **Weitere Informationen > Trotzdem ausführen**.
3. Blitztext erscheint im Infobereich der Taskleiste (ggf. über den Pfeil `^` einblenden und anheften).
4. Unter **Einstellungen > Modelle** einmal ein Sprachmodell laden. Empfohlen: **Parakeet v3** (rund 490 MB Download).
5. In Windows unter **Einstellungen > Datenschutz und Sicherheit > Mikrofon** muss „Desktop-Apps Zugriff auf das Mikrofon erlauben“ an sein.
6. `Strg + Win` halten, sprechen, loslassen.

**Noch kein Release?** Dann steckt der Installer in jedem Build: [Actions](https://github.com/Marlon1694/Blitztext-Win/actions/workflows/build.yml) > neuester Lauf von **Build** > ganz unten bei **Artifacts** > **Blitztext-Windows** laden (nur angemeldet, rund 90 Tage lang). Die ZIP enthält `Blitztext-Setup-x.y.z.exe` und die portable ZIP. Einen Release-Entwurf sehen nur Leute mit Schreibrecht unter [Releases](https://github.com/Marlon1694/Blitztext-Win/releases).

**Voraussetzungen:** Windows 10 (1809) oder Windows 11, 64 Bit. Rund 1 GB freier Arbeitsspeicher für das Sprachmodell.

## Datenschutz

```text
Spracherkennung:   dein PC (Parakeet oder Whisper, CPU)
KI-Modi:           dein Modell-Anbieter mit deinem API-Key, oder Ollama/LM Studio auf deinem PC
Modell-Download:   GitHub (Parakeet) bzw. Hugging Face (Whisper), einmal pro Modell, mit Prüfsumme
Update-Prüfung:    GitHub-Releases dieses Projekts (abschaltbar)
```

- **Audio** wird nur im Arbeitsspeicher gehalten, nie gespeichert und nie gesendet.
- **API-Keys** liegen in der Windows-Anmeldeinformationsverwaltung.
- **Einstellungen** in `%APPDATA%\Blitztext`, **Verlauf, Modelle und Protokoll** in `%LOCALAPPDATA%\Blitztext`. Das Protokoll enthält nie diktierten Text.
- Mit einem Modell-Anbieter im Internet geht der *erkannte Text* der KI-Modi an diesen Anbieter, nie die Sprache. Mit „KI-Modi sehen den Text am Cursor“ (standardmäßig aus) geht auch der Text rund um den Cursor mit.

## Bekannte Einschränkungen

- **Apps, die als Administrator laufen:** Windows lässt normale Apps dort nicht tippen. Blitztext erkennt das und legt den Text in die Zwischenablage (`Strg + V`).
- **`Win + L`, UAC-Dialoge und Anmeldebildschirm** sind für Blitztext nicht erreichbar.
- **Strg + Alt** entspricht auf deutschen Tastaturen AltGr; Kürzel mit `Strg + Alt + Taste` können Sonderzeichen wie `@` oder `€` belegen.
- **Bluetooth-Headsets** brauchen beim Start einen Moment und schalten beim Aufnehmen in den Freisprechmodus (Musik klingt dann kurz schlechter). Blitztext hält ihr Mikrofon deshalb nicht dauerhaft offen. Fehlt der Anfang, hilft **Einstellungen > Allgemein > Mikrofon bereithalten**, dann bleibt das Headset aber im Freisprechmodus.
- **Outlook und Word** zeigen nach dem Einfügen das kleine Symbol „(Strg)“ für Einfügeoptionen. Abschalten: Datei > Optionen > E-Mail > Editoroptionen (in Word: Datei > Optionen) > Erweitert > Ausschneiden, Kopieren und Einfügen > Schaltfläche für Einfügeoptionen anzeigen.
- **Erster Start eines Modells** dauert einige Sekunden; Blitztext lädt das gewählte Modell beim Start im Hintergrund vor.
- Die App ist auf Deutsch.

## Selbst bauen

Du brauchst das [.NET 10 SDK](https://dotnet.microsoft.com/download). Gebaut wird auch unter Linux und macOS (`EnableWindowsTargeting`), laufen muss die App unter Windows.

```powershell
git clone https://github.com/Marlon1694/Blitztext-Win.git
cd Blitztext-Win
dotnet test tests/Blitztext.Core.Tests
dotnet publish src/Blitztext.Windows -c Release -o publish
publish\Blitztext.exe
```

Installer: [Inno Setup 6](https://jrsoftware.org/isinfo.php), dann `iscc /DAppVersion=1.0.0 /DPublishDir=..\publish installer\Blitztext.iss`.

Selbsttest auf einem echten Windows (rendert alle Seiten in hell und dunkel als PNG, prüft Zwischenablage, Tastatur-Hook, Anmeldeinformationen, DPAPI, Modell-Download und Transkription; nutzt einen eigenen Datenordner):

```powershell
publish\Blitztext.exe --self-test .\selftest --install-models --audio sprache.wav
type selftest\selftest.log
```

```text
src/Blitztext.Core/      Plattformunabhängig: Nachbearbeitung, Einfügen am Cursor, Absätze,
                         Halluzinationsfilter, Prompts, Modell-Anbieter, Einstellungen, Verlauf
src/Blitztext.Speech/    Lokale Spracherkennung: Parakeet (sherpa-onnx), Whisper (whisper.cpp),
                         Modell-Download mit Prüfsummen, Audio-Umwandlung (WAV, Opus)
src/Blitztext.Windows/   WPF-App im Infobereich: Tastatur-Hook, WASAPI-Aufnahme, Zwischenablage,
                         UI Automation, Tray, Popover, Aufnahme-Anzeige, Selbsttest
tests/                   Unit-Tests (portiert aus dem macOS-Original) und Speech-Tests
installer/               Inno-Setup-Skript
```

Die GitHub Action baut bei jedem Push, testet unter Linux und Windows, führt den Selbsttest mit echten Modellen aus und erstellt Installer und ZIP (Artifact **Blitztext-Windows**).

- **Release-Entwurf:** Actions > Build > **Run workflow** mit Haken bei „Release-Entwurf mit Installer anlegen“. Der Entwurf hat die Version aus `Directory.Build.props` und ist nur für Leute mit Schreibrecht sichtbar.
- **Veröffentlichen:** Tag pushen (`git tag v1.0.0` und `git push origin v1.0.0`). Die Action baut dann neu und veröffentlicht den Entwurf mit der Version 1.0.0. Über **Publish release** im Entwurf ginge es auch, dann zeigt die App aber „1.0.0-ci.N“ als Version.
- Für jede weitere Version zuerst `Version` in `Directory.Build.props` erhöhen.

## Lizenz und Marke

MIT-Lizenz, siehe [LICENSE](LICENSE). Komponenten und Modelle von Dritten: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Dies ist ein inoffizielles Projekt, nicht von Blackboat erstellt oder unterstützt. Name und Marke „Blitztext“ gehören nicht zur MIT-Lizenz des Originals (siehe dessen [TRADEMARKS.md](https://github.com/cmagnussen/blitztext-app/blob/main/TRADEMARKS.md)); wer diese Portierung veröffentlicht oder weitergibt, sollte vorher einen eigenen Namen wählen oder die Erlaubnis einholen. Das App-Symbol ist eigenständig gestaltet.
