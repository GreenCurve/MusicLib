# MusicLib – step 3: library home, tag overrides, state sync, import

Extract over the existing folder (same layout as before). No changes to the .csproj files.

## Model
- LIBRARY HOME (one folder per device, shared with Syncthing, same relative layout everywhere)
    library/     media files (imports land here)
    .musiclib/   state-<deviceId>.json, one small file per device, written only by that device
- LOCAL DATA (not synced): library.db3 (index + this device's own user state), settings.json (device id, home path), roots.json (extra scan folders, console only)

## Windows
    dotnet run --project src/MusicLib.Console -- home
    dotnet run --project src/MusicLib.Console -- import "D:\Hoard\The Heap\Music\other music"
    dotnet run --project src/MusicLib.Console -- list
    dotnet run --project src/MusicLib.Console -- fav 1
    dotnet run --project src/MusicLib.Console -- tag 2 title "Make Them Bleed"
    dotnet run --project src/MusicLib.Console -- sync

## Syncthing
Share the home folder on both devices (type "Send & Receive"). On the phone, choose the same
path the app shows in its text box (default /storage/emulated/0/Music/MusicLib).

## Test
1. Import files on Windows, let Syncthing copy them, tap "Scan library" on the phone.
2. `fav` a track on Windows and wait for Syncthing. (Favorites and play counts are not shown in the app UI for now; they are still stored and synced.)
3. Import a file from the phone (button 2); it appears on Windows after Syncthing + `scan`.
