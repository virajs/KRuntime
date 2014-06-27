@Echo OFF
SETLOCAL
SET ERRORLEVEL=

REM <dev>
@Echo ON
REM </dev>

SET ARGS=%*
IF NOT "%ARGS%"=="" SET ARGS=%ARGS:/?="/?"%

CALL "%~dp0KLR.cmd" --lib "%~dp0;%~dp0lib\Microsoft.Framework.PackageManager" "Microsoft.Framework.PackageManager" %ARGS%

exit /b %ERRORLEVEL%
ENDLOCAL