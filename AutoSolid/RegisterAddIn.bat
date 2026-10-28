@echo off
:: Script para registrar o Add-In AutoSolid no SolidWorks (Executar como Administrador)
echo ======================================================
echo Registrando AutoSolid Add-In no SolidWorks...
echo ======================================================

set REGASM="C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"
set SW_DIR=C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS
set BIN_DIR=%~dp0AutoSolid\bin\Debug

if not exist %REGASM% (
    echo [ERRO] RegAsm 64-bit nao encontrado no caminho padrao do .NET Framework.
    pause
    exit /b 1
)

if not exist "%BIN_DIR%\AutoSolid.dll" (
    echo [ERRO] AutoSolid.dll nao encontrada em %BIN_DIR%.
    echo Compile o projeto no Visual Studio em modo Debug antes de registrar.
    pause
    exit /b 1
)

:: Inclui o diretorio do SolidWorks e a pasta bin no PATH para resolucao de dependencias
set PATH=%SW_DIR%;%BIN_DIR%;%PATH%

:: Entra na pasta onde a DLL e os Interops residem
cd /d "%BIN_DIR%"

%REGASM% /codebase AutoSolid.dll

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ======================================================
    echo [SUCESSO] AutoSolid registrado com sucesso!
    echo Agora abra o SolidWorks e verifique a barra lateral (Taskpane)
    echo ou va em Ferramentas -> Suplementos (Add-Ins).
    echo ======================================================
) else (
    echo.
    echo [ERRO] Falha ao registrar. Certifique-se de executar como Administrador.
)

pause
