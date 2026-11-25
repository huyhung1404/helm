@echo off
rem Helm Gallery: one shared Avalonia page at a time over fake data, with hot reload (save an .axaml, the page updates).
dotnet run --project "%~dp0..\src\Helm.Gallery" -c Debug
