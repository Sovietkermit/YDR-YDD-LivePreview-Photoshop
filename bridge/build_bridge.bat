@echo off
REM ─────────────────────────────────────────────────────────────────
REM build_bridge.bat
REM Compile YdrBridge.exe avec CodeWalker.Core.dll
REM
REM Usage :
REM   build_bridge.bat D:\Application\CodeWalker30_dev48
REM   (si pas d'argument, utilise CW_DIR ou le défaut ci-dessous)
REM ─────────────────────────────────────────────────────────────────

SET DEFAULT_CW=D:\Application\CodeWalker30_dev48

IF "%~1"=="" (
    IF "%CW_DIR%"=="" (
        SET CW_DIR=%DEFAULT_CW%
    )
) ELSE (
    SET CW_DIR=%~1
)

ECHO [Build] CodeWalker dir : %CW_DIR%

IF NOT EXIST "%CW_DIR%\CodeWalker.Core.dll" (
    ECHO [ERREUR] CodeWalker.Core.dll introuvable dans : %CW_DIR%
    ECHO Verifie le chemin et relance.
    PAUSE
    EXIT /B 1
)

ECHO [Build] Compilation en cours...
dotnet publish YdrBridge.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:CW_DIR="%CW_DIR%" ^
  -o .\publish

IF %ERRORLEVEL% NEQ 0 (
    ECHO [ERREUR] Compilation echouee — voir erreurs ci-dessus
    PAUSE
    EXIT /B %ERRORLEVEL%
)

ECHO.
ECHO [OK] YdrBridge.exe dans .\publish\YdrBridge.exe
ECHO Copie dans le plugin UXP et configure le chemin dans le panneau.
PAUSE
