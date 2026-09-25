# Testing installation and updates locally

This checklist exercises the full Velopack flow on one machine without GitHub. You build two versions into the same `releases/` folder and point an installed Helm at that folder with a hidden developer setting.

> The build installs Helm for your user (under `%LOCALAPPDATA%\HelmApp`) and creates Start menu and desktop shortcuts. Uninstall it from **Settings → Apps** when you are done.

## 0. Prepare

- [ ] Close any running Helm (tray → **Exit**).
- [ ] Back up `%LOCALAPPDATA%\Helm` if you care about your current settings.
- [ ] Start from an empty packaging folder: `Remove-Item -Recurse -Force releases, artifacts -ErrorAction SilentlyContinue`

## 1. Build and install v0.3.0

```powershell
./scripts/release-local.ps1 -Version 0.3.0
./releases/HelmApp-stable-Setup.exe
```

- [ ] Setup finishes. Helm starts and **one UAC prompt** appears (Helm elevates itself; this is expected).
- [ ] Home tile shows **You're up to date** or **Checking…**, and not "Updates unavailable". General → Updates shows `Helm v0.3.0`.
- [ ] Shortcuts exist, and the tray icon is visible (white on a dark taskbar).
- [ ] `%LOCALAPPDATA%\HelmApp\Helm.exe` (the stable launcher) exists next to `Update.exe` and `current\`.

## 2. Create some state to verify later

- [ ] General → **Run at startup** = On.
- [ ] Zones: open the editor (<kbd>Win</kbd>+<kbd>Shift</kbd>+<kbd>`</kbd>), apply a non-default layout to one monitor.
- [ ] Always On Top: change the border color.
- [ ] In an elevated PowerShell, confirm the task points at the stable launcher:

  ```powershell
  (Get-ScheduledTask -TaskName Helm).Actions.Execute   # → ...\AppData\Local\HelmApp\Helm.exe
  ```

## 3. Point Helm at the local folder

- [ ] Exit Helm (tray → **Exit**).
- [ ] Edit `%LOCALAPPDATA%\Helm\settings\general.json` and add the developer override inside `"updates"`. Use your repo path:

  ```json
  "updates": {
    "channel": "stable",
    "autoDownload": true,
    "sourceOverride": "file:///C:/Project/Helm/releases"
  }
  ```

  A plain path such as `C:\\Project\\Helm\\releases` also works.
- [ ] Start Helm from the Start menu. General → Updates shows *Developer update source override: …*.

## 4. Build v0.3.1 into the same folder

```powershell
./scripts/release-local.ps1 -Version 0.3.1
```

- [ ] `releases/` now contains `HelmApp-0.3.1-stable-full.nupkg`, a `-delta.nupkg`, and an updated `releases.stable.json`.

## 5. Check → download → restart

- [ ] General → Updates → **Check for updates**. The status becomes *Version 0.3.1 is available*, and a tray balloon appears.
- [ ] With auto-download on, the progress bar runs and the status becomes *ready — restart to update*. With auto-download off, press **Download** first.
- [ ] The Home tile reads **Update available · v0.3.1**, and clicking it opens General → Updates.
- [ ] The tray menu contains **Restart to update (v0.3.1)**.
- [ ] Press **Restart to update**. Before exiting, Helm disables every module: pinned windows lose their topmost state and border, and no overlay is left on screen.
- [ ] Helm comes back **without a UAC prompt**, because the update runs from the elevated process. If a prompt does appear, note it: that means the fallback relauncher ran (see `%LOCALAPPDATA%\Helm\logs\velopack-hooks.log`).
- [ ] General → Updates shows `Helm v0.3.1`.

## 6. Verify nothing was lost

- [ ] The Zones layout from step 2 is still applied, and the Always On Top border color is unchanged.
- [ ] General → Run at startup is still On. In elevated PowerShell, `(Get-ScheduledTask -TaskName Helm).Actions.Execute` still points at `...\HelmApp\Helm.exe`, not `current\` or an `app-0.3.x` folder.
- [ ] Sign out and back in. Helm starts elevated with no UAC prompt, straight into the tray.
- [ ] `velopack-hooks.log` contains `Updated to 0.3.1.`

## 7. Auto-install on restart (optional)

- [ ] Build `0.3.2`, let Helm download it, turn **Install updates automatically when Helm restarts** on, then exit and start Helm again. It starts directly on 0.3.2.

## 8. Uninstall

- [ ] Settings → Apps → Helm → Uninstall.
- [ ] `%LOCALAPPDATA%\HelmApp` is gone, and `%LOCALAPPDATA%\Helm\settings` still exists (purge was off).
- [ ] `Get-ScheduledTask -TaskName Helm` returns nothing. If a UAC prompt for `schtasks.exe` appeared during uninstall, that was the elevated removal. Accept it.
- [ ] Repeat with General → **Delete my settings when Helm is uninstalled** = On. Afterwards `%LOCALAPPDATA%\Helm` is gone too.

## Cleanup

- Remove `sourceOverride` from `general.json` (or reset settings) so a future install uses GitHub again.
- `Remove-Item -Recurse -Force releases, artifacts`
