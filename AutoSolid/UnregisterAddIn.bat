@echo off
:: Script para desregistrar o Add-In AutoSolid do SolidWorks (Executar como Administrador)
echo ======================================================
echo Desregistrando AutoSolid Add-In...
echo ======================================================

set REGASM="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
set SW_DIR=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS
set BIN_DIR=%~dp0AutoSolid\bin\Debug

if not exist %REGASM% (
    echo [ERRO] RegAsm 64-bit nao encontrado.
    pause
    exit /b 1
)

if not exist "%BIN_DIR%\AutoSolid.dll" (
    echo [AVISO] AutoSolid.dll nao encontrada em %BIN_DIR%.
    pause
    exit /b 1
)

set PATH=%SW_DIR%;%BIN_DIR%;%PATH%
cd /d "%BIN_DIR%"

%REGASM% /unregister AutoSolid.dll

echo.
echo ======================================================
echo Add-In desregistrado com sucesso.
echo ======================================================

pause
