@echo off
echo ========================================================
echo   STAAD.Pro 3D Parametric Portal Frame Modeler
echo ========================================================
echo.
echo Closing any previous running instance...
taskkill /F /IM StaadPortalApp.exe >nul 2>&1

echo Starting 3D Interactive Web Application...
echo.
cd /d "%~dp0src\StaadPortalApp"
timeout /t 1 /nobreak >nul
start http://localhost:5000
dotnet run --urls "http://localhost:5000"
pause
