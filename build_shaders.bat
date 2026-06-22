@echo off
setlocal

set UNITY="C:\Program Files\Unity\Unity 2022.3.60f1\Editor\Unity.exe"
set PROJECT="%~dp0ShaderProject"
set LOG="%~dp0shader_build.log"

echo Building shader bundle...
%UNITY% -batchmode -nographics -projectPath %PROJECT% -executeMethod BuildBundles.Build -logFile %LOG% -quit

if %ERRORLEVEL% equ 0 (
    echo SUCCESS - bundle written to BabyBlocks\Shaders\babyblocks_shaders.bundle
) else (
    echo FAILED - see shader_build.log for details
    type %LOG% | findstr /i "error warning failed success"
    exit /b 1
)
