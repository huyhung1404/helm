@echo off
rem SPIKE: opens the Avalonia Gallery with Helm Keyboard's log on (%TEMP%\helm-keyboard-spike.log).
set HELM_KEYBOARD_LOG=%TEMP%\helm-keyboard-spike.log
start "" "%~dp0gallery\Helm.Gallery.exe"
echo Keyboard log: %HELM_KEYBOARD_LOG%
