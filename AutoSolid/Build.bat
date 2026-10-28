@echo off
echo ======================================================
echo Compilando AutoSolid Add-In para SolidWorks...
echo ======================================================

set MSBUILD="C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"

%MSBUILD% "%~dp0AutoSolid.sln" /p:Configuration=Debug

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ======================================================
    echo [SUCESSO] AutoSolid.dll compilada com sucesso!
    echo Abra o SolidWorks para usar a versao atualizada.
    echo ======================================================
) else (
    echo.
    echo [AVISO] Se ocorreu erro de arquivo em uso, feche o SolidWorks e execute novamente.
)

pause
